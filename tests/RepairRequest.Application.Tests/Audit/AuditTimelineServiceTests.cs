using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using RepairRequest.Application.Audit;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Audit;

/// <summary>
/// The timeline use case against in-memory fakes (UC-WO-002; docs/15 §4–§5): validation order (cursor syntax → HMAC → entity
/// type → scoped lookup), paging, actor classification and the no-leak shape of the DTO. Scope itself (404 for another Site
/// or tenant) is the store's responsibility and is proven end to end in the integration and API tests.
/// </summary>
public class AuditTimelineServiceTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _workOrderId = Guid.NewGuid();
    private readonly FakeTimelineStore _store = new();
    private readonly FakeCursorProtector _cursors = new();
    private readonly AuditTimelineService _service;
    private readonly CurrentUser _user;

    public AuditTimelineServiceTests()
    {
        _service = new AuditTimelineService(_store, _cursors);
        _user = new CurrentUser(Guid.NewGuid(), _tenantId, [RoleCodes.Supervisor]);
        _store.Sources = new AuditTimelineSources(_tenantId, _workOrderId, Guid.NewGuid(), [], [], []);
    }

    private static AuditTimelineRow Row(int minutesAgo, string code = "WORK_ORDER_STARTED", Guid? actor = null, string? json = null) =>
        new(Guid.NewGuid(), new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Unspecified).AddMinutes(-minutesAgo),
            "WORK_ORDER", Guid.NewGuid(), code, "FROM", "TO", actor ?? Guid.NewGuid(), json);

    private Task<AuditTimelineResult> GetAsync(string entityType = "WORK_ORDER", int pageSize = 20, string? cursor = null) =>
        _service.GetWorkOrderTimelineAsync(_user, entityType, _workOrderId, pageSize, cursor, CancellationToken.None);

    private string AuthenticCursor(string entityType = "WORK_ORDER", TimelinePosition? position = null) =>
        _cursors.Protect(_tenantId, entityType, _workOrderId, position ?? new TimelinePosition(new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc), Guid.NewGuid()));

    // ------------------------------------------------------------------ validation order

    [Theory]
    [InlineData("")]
    [InlineData("not-base64url!!")]
    [InlineData("AAAA")]
    public async Task MalformedCursor_IsAGenericBadCursor_AndTouchesNothing(string cursor)
    {
        var result = await GetAsync(cursor: cursor);

        Assert.Equal(AuditTimelineOutcome.BadCursor, result.Outcome);
        Assert.Equal(0, _store.ResolveCalls + _store.ReadCalls + _store.UserLookups);
    }

    [Fact]
    public async Task OverlongCursor_IsABadCursor_BeforeAnyDecoding()
    {
        var result = await GetAsync(cursor: new string('A', TimelineCursorFormat.MaxTextLength + 1));

        Assert.Equal(AuditTimelineOutcome.BadCursor, result.Outcome);
        Assert.Equal(0, _store.ResolveCalls);
    }

    [Fact]
    public async Task InauthenticCursor_IsABadCursor_WithoutAnyDatabaseAccess_EvenWhenTheWorkOrderDoesNotExist()
    {
        _store.Sources = null; // the Work Order does not exist
        var forged = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TimelineCursorFormat.TotalLength));

        var result = await GetAsync(cursor: forged);

        Assert.Equal(AuditTimelineOutcome.BadCursor, result.Outcome);
        Assert.Equal(0, _store.ResolveCalls + _store.ReadCalls); // decided before and regardless of existence
    }

    [Fact]
    public async Task CursorIssuedForAnotherWorkOrderTenantOrEntityType_IsABadCursor()
    {
        var foreignWorkOrder = _cursors.Protect(_tenantId, "WORK_ORDER", Guid.NewGuid(), new TimelinePosition(DateTime.UtcNow, Guid.NewGuid()));
        var foreignTenant = _cursors.Protect(Guid.NewGuid(), "WORK_ORDER", _workOrderId, new TimelinePosition(DateTime.UtcNow, Guid.NewGuid()));
        var foreignType = _cursors.Protect(_tenantId, "SERVICE_VISIT", _workOrderId, new TimelinePosition(DateTime.UtcNow, Guid.NewGuid()));

        foreach (var cursor in new[] { foreignWorkOrder, foreignTenant, foreignType })
        {
            Assert.Equal(AuditTimelineOutcome.BadCursor, (await GetAsync(cursor: cursor)).Outcome);
        }

        Assert.Equal(0, _store.ResolveCalls);
    }

    [Theory]
    [InlineData("USER")]
    [InlineData("FILE_ASSET")]
    [InlineData("REPAIR_REQUEST")]
    [InlineData("SERVICE_VISIT")]
    [InlineData("")]
    public async Task UnsupportedEntityType_IsNotFound_WithoutResolvingAnything(string entityType)
    {
        var result = await GetAsync(entityType);

        Assert.Equal(AuditTimelineOutcome.NotFound, result.Outcome);
        Assert.Equal(0, _store.ResolveCalls + _store.ReadCalls);
    }

    [Fact]
    public async Task UnsupportedEntityType_WithAnAuthenticCursorForThatType_PassesTheHmacThenIsNotFound()
    {
        var result = await GetAsync("USER", cursor: AuthenticCursor("USER"));

        Assert.Equal(AuditTimelineOutcome.NotFound, result.Outcome);
        Assert.Equal(0, _store.ResolveCalls);
    }

    [Fact]
    public async Task MissingOrOutOfScopeWorkOrder_IsNotFound_AndNothingIsRead()
    {
        _store.Sources = null;

        var result = await GetAsync();

        Assert.Equal(AuditTimelineOutcome.NotFound, result.Outcome);
        Assert.Equal(1, _store.ResolveCalls);
        Assert.Equal(0, _store.ReadCalls);
    }

    [Theory]
    [InlineData("work_order")]
    [InlineData("Work_Order")]
    [InlineData("WORK_ORDER")]
    public async Task EntityType_IsNormalizedByInvariantUpperCasing(string entityType)
    {
        _store.Rows = [Row(1)];

        var result = await GetAsync(entityType);

        Assert.Equal(AuditTimelineOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task TheCursorBindingUsesTheNormalizedEntityType_TheAuthenticatedTenant_AndTheRouteWorkOrderId()
    {
        _store.Rows = [Row(1), Row(2), Row(3)];

        var result = await GetAsync("work_order", pageSize: 2);

        Assert.NotNull(result.Page!.NextCursor);
        Assert.Equal((_tenantId, "WORK_ORDER", _workOrderId), _cursors.LastProtectContext);
    }

    // ------------------------------------------------------------------ paging

    [Fact]
    public async Task FullPage_ReadsOneExtraRow_ReturnsHasMore_AndACursorForTheLastReturnedRow()
    {
        _store.Rows = [Row(1), Row(2), Row(3), Row(4), Row(5)];

        var result = await GetAsync(pageSize: 3);

        var page = result.Page!;
        Assert.Equal(4, _store.LastTake);                  // pageSize + 1
        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal(_store.Rows.Take(3).Select(r => r.AuditId), page.Items.Select(i => i.AuditId));

        // The next cursor positions after the LAST RETURNED row, not after the extra one.
        Assert.True(TimelineCursorSyntax.TryDecode(page.NextCursor, out var bytes));
        Assert.True(_cursors.TryUnprotect(_tenantId, "WORK_ORDER", _workOrderId, bytes, out var position));
        Assert.Equal(_store.Rows[2].AuditId, position.AuditId);
        Assert.Equal(_store.Rows[2].OccurredAt.Ticks, position.OccurredAt.Ticks);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 2)]
    [InlineData(3, 0)]
    public async Task LastPage_HasNoMoreAndNoCursor(int pageSize, int rowCount)
    {
        _store.Rows = Enumerable.Range(1, rowCount).Select(i => Row(i)).ToList();

        var page = (await GetAsync(pageSize: pageSize)).Page!;

        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Equal(rowCount, page.Items.Count);
    }

    [Fact]
    public async Task AValidCursor_IsPassedToTheStoreAsTheKeysetPosition()
    {
        var position = new TimelinePosition(new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc), Guid.NewGuid());
        _store.Rows = [Row(1)];

        var result = await GetAsync(cursor: AuthenticCursor(position: position));

        Assert.Equal(AuditTimelineOutcome.Ok, result.Outcome);
        Assert.Equal(position, _store.LastAfter);
    }

    [Fact]
    public async Task NoCursor_StartsFromTheNewestRow()
    {
        _store.Rows = [Row(1)];

        await GetAsync();

        Assert.Null(_store.LastAfter);
    }

    [Fact]
    public async Task PageSizeBelowOne_IsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GetAsync(pageSize: 0));
    }

    // ------------------------------------------------------------------ actors, time, details

    [Fact]
    public async Task Actors_AreClassified_SystemUserOrUnknown_AndOnlyExistingTenantUsersAreLookedUp()
    {
        var existingUser = Guid.NewGuid();
        var deletedUser = Guid.NewGuid();
        _store.ExistingUsers = new HashSet<Guid> { existingUser };
        _store.Rows =
        [
            Row(1, actor: existingUser), Row(2, actor: deletedUser), Row(3, actor: SystemActors.ApprovalRouting),
            Row(4, actor: SystemActors.MalwareScanner), Row(5, actor: existingUser),
        ];

        var items = (await GetAsync()).Page!.Items;

        Assert.Equal(AuditActorKind.User, items[0].Actor.Kind);
        Assert.Equal(AuditActorKind.Unknown, items[1].Actor.Kind);
        Assert.Equal(AuditActorKind.System, items[2].Actor.Kind);
        Assert.Equal(AuditActorKind.System, items[3].Actor.Kind);
        Assert.Equal(AuditActorKind.User, items[4].Actor.Kind);
        Assert.Equal(existingUser, items[0].Actor.ActorId);

        Assert.Equal(1, _store.UserLookups);                                  // one set-based lookup per page
        Assert.Equal(_tenantId, _store.LastLookupTenant);                     // tenant-scoped
        Assert.Equal(new[] { existingUser, deletedUser }.OrderBy(g => g), _store.LastLookupIds.OrderBy(g => g)); // distinct, no system actors
    }

    [Fact]
    public async Task OccurredAt_IsReturnedAsUtc()
    {
        _store.Rows = [Row(1)];

        var item = (await GetAsync()).Page!.Items.Single();

        Assert.Equal(DateTimeKind.Utc, item.OccurredAt.Kind);
        Assert.Equal(new DateTime(2026, 10, 1, 11, 59, 0, DateTimeKind.Utc), item.OccurredAt);
    }

    [Fact]
    public async Task Details_AreProjectedThroughTheAllowlist_NeverTheRawPayload()
    {
        _store.Rows =
        [
            Row(1, "COST_SUMMARY_PREPARED", json: "{\"costSummaryId\":\"cs-1\",\"totalAmount\":999.99,\"currencyCode\":\"USD\"}"),
            Row(2, "WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE", json: "{\"acceptanceContactSnapshot\":\"jane@example.test\"}"),
        ];

        var items = (await GetAsync()).Page!.Items;

        Assert.Equal(["costSummaryId"], items[0].Details.Keys);
        Assert.Empty(items[1].Details);
    }

    [Fact]
    public void TheItemDto_HasNoReasonCorrelationRawJsonOrIdentityFields()
    {
        var properties = typeof(AuditTimelineItemDto).GetProperties().Select(p => p.Name).Concat(
            typeof(AuditTimelineActorDto).GetProperties().Select(p => p.Name)).ToList();

        foreach (var forbidden in new[] { "Reason", "CorrelationId", "OldValueJson", "NewValueJson", "OldValue", "NewValue", "Email", "DisplayName", "UserName", "Phone" })
        {
            Assert.DoesNotContain(forbidden, properties);
        }

        Assert.Equal(
            new[] { "ActionCode", "Actor", "ActorId", "AuditId", "Details", "EntityId", "EntityType", "FromState", "Kind", "OccurredAt", "ToState" },
            properties.OrderBy(p => p, StringComparer.Ordinal));
        Assert.DoesNotContain("NewValueJson", typeof(AuditTimelinePageDto).GetProperties().Select(p => p.Name));
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeTimelineStore : IAuditTimelineStore
    {
        public AuditTimelineSources? Sources { get; set; }

        public IReadOnlyList<AuditTimelineRow> Rows { get; set; } = [];

        public IReadOnlySet<Guid> ExistingUsers { get; set; } = new HashSet<Guid>();

        public int ResolveCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public int UserLookups { get; private set; }

        public int LastTake { get; private set; }

        public TimelinePosition? LastAfter { get; private set; }

        public Guid LastLookupTenant { get; private set; }

        public IReadOnlyCollection<Guid> LastLookupIds { get; private set; } = [];

        public Task<AuditTimelineSources?> ResolveWorkOrderSourcesAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(Sources);
        }

        public Task<IReadOnlyList<AuditTimelineRow>> ReadPageAsync(AuditTimelineSources sources, TimelinePosition? after, int take, CancellationToken cancellationToken)
        {
            ReadCalls++;
            LastTake = take;
            LastAfter = after;
            return Task.FromResult<IReadOnlyList<AuditTimelineRow>>(Rows.Take(take).ToList());
        }

        public Task<IReadOnlySet<Guid>> GetExistingUserIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken)
        {
            UserLookups++;
            LastLookupTenant = tenantId;
            LastLookupIds = actorIds;
            return Task.FromResult<IReadOnlySet<Guid>>(ExistingUsers.Where(actorIds.Contains).ToHashSet());
        }
    }

    /// <summary>A real-format cursor (57 bytes, Base64url) whose tag is a plain SHA-256 over the binding context + payload.</summary>
    private sealed class FakeCursorProtector : ITimelineCursorProtector
    {
        public (Guid Tenant, string Type, Guid WorkOrder)? LastProtectContext { get; private set; }

        public string Protect(Guid tenantId, string normalizedEntityType, Guid workOrderId, TimelinePosition position)
        {
            LastProtectContext = (tenantId, normalizedEntityType, workOrderId);
            var bytes = new byte[TimelineCursorFormat.TotalLength];
            bytes[0] = TimelineCursorFormat.Version;
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1, 8), position.OccurredAt.Ticks);
            position.AuditId.TryWriteBytes(bytes.AsSpan(9, 16));
            Tag(tenantId, normalizedEntityType, workOrderId, bytes.AsSpan(0, TimelineCursorFormat.PayloadLength)).CopyTo(bytes, TimelineCursorFormat.PayloadLength);
            return Base64Url.EncodeToString(bytes);
        }

        public bool TryUnprotect(Guid tenantId, string normalizedEntityType, Guid workOrderId, ReadOnlySpan<byte> cursorBytes, out TimelinePosition position)
        {
            position = default;
            var payload = cursorBytes[..TimelineCursorFormat.PayloadLength];
            if (!Tag(tenantId, normalizedEntityType, workOrderId, payload).AsSpan().SequenceEqual(cursorBytes[TimelineCursorFormat.PayloadLength..]))
            {
                return false;
            }

            position = new TimelinePosition(
                new DateTime(BinaryPrimitives.ReadInt64BigEndian(payload.Slice(1, 8)), DateTimeKind.Utc), new Guid(payload.Slice(9, 16)));
            return true;
        }

        private static byte[] Tag(Guid tenantId, string type, Guid workOrderId, ReadOnlySpan<byte> payload)
        {
            var context = Encoding.UTF8.GetBytes($"{tenantId:D}|{type}|{workOrderId:D}");
            var message = new byte[context.Length + payload.Length];
            context.CopyTo(message, 0);
            payload.CopyTo(message.AsSpan(context.Length));
            return SHA256.HashData(message);
        }
    }
}
