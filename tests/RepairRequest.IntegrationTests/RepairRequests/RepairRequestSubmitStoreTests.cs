using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.IntegrationTests.RepairRequests;

/// <summary>
/// ST-RR-002 Submit persistence against LocalDB (DEC-PRE-S1-007-06/09/11/12): transactional per-tenant, per-year Request No
/// allocation under concurrency with rollback on failure, CLEAN-photo EXISTS, the BR-14 duplicate COUNT predicate, the
/// duplicate-key lock under concurrent matching and non-matching Submits, idempotent lookup seeding, and a bounded number of
/// SQL commands for a full Submit.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class RepairRequestSubmitStoreTests : IAsyncLifetime
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan ConcurrencyTimeout = TimeSpan.FromSeconds(15);

    private readonly AuthenticationTestHost _host = new();

    public RepairRequestSubmitStoreTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private int ServiceYear => _host.Clock.GetUtcNow().UtcDateTime.Year;

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task WithDbAsync(Func<RepairRequestDbContext, Task> action)
    {
        await using var scope = _host.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task<ScopeWorld> WorldAsync()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        await using var scope = _host.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>();
        await seeder.SeedTenantAsync(world.TenantId, None);
        await seeder.SeedTenantAsync(world.OtherTenantId, None);
        return world;
    }

    private async Task<CurrentUser> RequesterAsync(Guid tenantId, Site site)
    {
        var user = await _host.CreateUserInTenantAsync(tenantId, RoleCodes.Requester);
        await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, tenantId, user.Id, site));
        return new CurrentUser(user.Id, tenantId, [RoleCodes.Requester]);
    }

    private async Task<Guid> CompleteDraftAsync(CurrentUser owner, Site site, Guid? equipmentId = null, string category = "ELECTRICAL")
    {
        await using var scope = _host.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>().CreateAsync(
            new CommandContext(owner, Guid.NewGuid()),
            new RepairRequestDraftFields(site.Id, equipmentId, category, "HIGH", owner.UserId, "Pump leaking", Start, Start.AddHours(2)),
            None);
        Assert.True(result.Succeeded, string.Join(";", result.Error?.Errors.Keys ?? []));
        return result.Value!.Id;
    }

    /// <summary>A complete Draft with one CLEAN photo, ready for the Submit service.</summary>
    private async Task<Guid> EvidencedDraftAsync(CurrentUser owner, Site site, Guid? equipmentId = null, string category = "ELECTRICAL")
    {
        var id = await CompleteDraftAsync(owner, site, equipmentId, category);
        await AttachAsync(owner, id, "image/png", MalwareScanStatus.Clean);
        return id;
    }

    private Task AttachAsync(CurrentUser owner, Guid requestId, string mimeType, MalwareScanStatus status, Guid? fileTenantId = null) =>
        WithDbAsync(async db =>
        {
            var tenantId = fileTenantId ?? owner.TenantId;
            var file = new FileAsset(tenantId, "evidence.bin", mimeType, 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            if (status == MalwareScanStatus.Clean)
            {
                file.MarkClean();
            }
            else if (status == MalwareScanStatus.Failed)
            {
                file.MarkFailed();
            }

            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(requestId, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

    private static DuplicateQuery QueryFor(RepairRequestAggregate request, DateTime submittedAt) =>
        new(request.TenantId, request.Id, request.SiteId!.Value, request.RequestCategoryCode!, request.EquipmentId, submittedAt - RepairRequestSubmitService.DuplicateWindow);

    private static string DuplicateKey(Guid tenantId, Site site, string category, Guid? equipmentId = null) =>
        RepairRequestDuplicateLock.Resource(new DuplicateQuery(tenantId, Guid.Empty, site.Id, category, equipmentId, DateTime.UnixEpoch));

    /// <summary>
    /// Submits straight through the stores, as the service does. Allocation tests are not about BR-14, so they pass a
    /// continuation decision that lets duplicates proceed; the duplicate-key lock and counter are still exercised.
    /// </summary>
    private async Task<RepairRequestSubmitResult> SubmitThroughStoreAsync(CurrentUser owner, Guid requestId, int year = 2026, Action? beforeApply = null)
    {
        await using var scope = _host.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();
        var submits = scope.ServiceProvider.GetRequiredService<IRepairRequestSubmitStore>();
        var request = (await drafts.FindOwnAsync(owner, requestId, None))!;
        var context = new CommandContext(owner, Guid.NewGuid());
        var submittedAt = new DateTime(year, 3, 1, 8, 0, 0, DateTimeKind.Utc);

        return await submits.SubmitAsync(request, request.RowVersion, QueryFor(request, submittedAt), year, hasContinuationReason: true, (sequence, duplicateCount) =>
        {
            beforeApply?.Invoke();
            request.Submit(RequestNumber.Format(year, sequence), owner.UserId, submittedAt, null);
            return RepairRequestAudit.Submitted(context, request, duplicateCount, submittedAt);
        }, None);
    }

    /// <summary>Submits through the real service (validation, duplicate policy, atomic store call).</summary>
    private async Task<CommandResult<RepairRequestDraftDto>> SubmitThroughServiceAsync(CurrentUser owner, Guid requestId, string? reason = null)
    {
        await using var scope = _host.CreateScope();
        var rowVersion = (await scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().GetAsync(owner, requestId, None))!.RowVersion;
        return await scope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
            .SubmitAsync(new CommandContext(owner, Guid.NewGuid()), requestId, rowVersion, reason, None);
    }

    private Task<int?> CounterAsync(Guid tenantId, int year) =>
        WithDbAsync(db => db.RequestNumberCounters
            .Where(counter => counter.TenantId == tenantId && counter.RequestYear == year)
            .Select(counter => (int?)counter.LastValue)
            .SingleOrDefaultAsync());

    private Task<List<AuditHistory>> SubmittedAuditsAsync(Guid requestId) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(audit => audit.EntityId == requestId && audit.ActionCode == RepairRequestAudit.SubmittedAction)
            .ToListAsync());

    private async Task AssertNotSubmittedAsync(Guid requestId)
    {
        var stored = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));
        Assert.Equal(RepairRequestStatus.Draft, stored.Status);
        Assert.Null(stored.RequestNo);
        Assert.Null(stored.SubmittedAt);
        Assert.Null(stored.SubmittedBy);
        Assert.Empty(await SubmittedAuditsAsync(requestId));
    }

    /// <summary>True when no session holds or waits for the duplicate-key lock (it could be granted right now).</summary>
    private Task<bool> IsDuplicateKeyFreeAsync(string resource) =>
        WithDbAsync(async db => await db.Database
            .SqlQuery<int>($"SELECT APPLOCK_TEST('public', {resource}, 'Exclusive', 'Session') AS [Value]")
            .SingleAsync() == 1);

    /// <summary>Waits (bounded) until at least <paramref name="expected"/> sessions of this database wait on an application lock.</summary>
    private async Task WaitForDuplicateLockWaitersAsync(int expected)
    {
        var deadline = DateTime.UtcNow + ConcurrencyTimeout;
        while (true)
        {
            var waiting = await WithDbAsync(db => db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND resource_database_id = DB_ID()
                """).SingleAsync());

            if (waiting >= expected)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Expected {expected} duplicate-lock waiter(s); observed {waiting}.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>
    /// An in-flight competitor: a separate session holding the duplicate-key lock in an open transaction until disposed.
    /// </summary>
    private sealed class HeldDuplicateLock : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly IDbContextTransaction _transaction;
        private bool _released;

        private HeldDuplicateLock(AsyncServiceScope scope, IDbContextTransaction transaction)
        {
            _scope = scope;
            _transaction = transaction;
        }

        public static async Task<HeldDuplicateLock> AcquireAsync(AuthenticationTestHost host, string resource)
        {
            var scope = host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
            var transaction = await db.Database.BeginTransactionAsync();
            var result = (await db.Database.SqlQuery<int>($"""
                DECLARE @lock_result int;
                EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
                SELECT @lock_result AS [Value];
                """).ToListAsync()).Single();
            Assert.True(result >= 0, $"The test could not take the duplicate-key lock ({result}).");
            return new HeldDuplicateLock(scope, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }

    // ---------------- Request No allocation ----------------

    [Fact]
    public async Task Allocation_ConcurrentSubmitsInOneTenantYear_ProduceUniqueGaplessNumbers()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var ids = new List<Guid>();
        for (var index = 0; index < 12; index++)
        {
            ids.Add(await CompleteDraftAsync(owner, world.SiteA1));
        }

        var outcomes = await Task.WhenAll(ids.Select(id => Task.Run(() => SubmitThroughStoreAsync(owner, id))));

        Assert.All(outcomes, outcome => Assert.Equal(RepairRequestSubmitStatus.Saved, outcome.Status));
        var numbers = await WithDbAsync(db => db.RepairRequests
            .Where(request => request.TenantId == world.TenantId)
            .Select(request => request.RequestNo!)
            .ToListAsync());
        Assert.Equal(Enumerable.Range(1, 12).Select(sequence => RequestNumber.Format(2026, sequence)), numbers.Order());
        Assert.Equal(12, await CounterAsync(world.TenantId, 2026));
    }

    [Fact]
    public async Task Allocation_SequencesAreSeparatePerTenantAndPerYear()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var otherOwner = await RequesterAsync(world.OtherTenantId, world.OtherTenantSite);

        var first2026 = await CompleteDraftAsync(owner, world.SiteA1);
        var second2026 = await CompleteDraftAsync(owner, world.SiteA1);
        var first2027 = await CompleteDraftAsync(owner, world.SiteA1);
        var otherTenant2026 = await CompleteDraftAsync(otherOwner, world.OtherTenantSite);

        await SubmitThroughStoreAsync(owner, first2026, 2026);
        await SubmitThroughStoreAsync(owner, second2026, 2026);
        await SubmitThroughStoreAsync(owner, first2027, 2027);
        await SubmitThroughStoreAsync(otherOwner, otherTenant2026, 2026);

        var numbers = await WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == first2026 || request.Id == second2026 || request.Id == first2027 || request.Id == otherTenant2026)
            .ToDictionaryAsync(request => request.Id, request => request.RequestNo));
        Assert.Equal("RR-2026-000001", numbers[first2026]);
        Assert.Equal("RR-2026-000002", numbers[second2026]);
        Assert.Equal("RR-2027-000001", numbers[first2027]);
        Assert.Equal("RR-2026-000001", numbers[otherTenant2026]);
    }

    [Fact]
    public async Task StaleSecondSubmitOfTheSameDraft_Returns409_AndRollsBackItsAllocation()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var draftId = await CompleteDraftAsync(owner, world.SiteA1);

        await using var firstScope = _host.CreateScope();
        await using var secondScope = _host.CreateScope();
        var firstCopy = (await firstScope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().FindOwnAsync(owner, draftId, None))!;
        var staleCopy = (await secondScope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().FindOwnAsync(owner, draftId, None))!;
        var context = new CommandContext(owner, Guid.NewGuid());
        var at = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

        var first = await firstScope.ServiceProvider.GetRequiredService<IRepairRequestSubmitStore>().SubmitAsync(
            firstCopy, firstCopy.RowVersion, QueryFor(firstCopy, at), 2026, hasContinuationReason: false, (sequence, duplicateCount) =>
            {
                firstCopy.Submit(RequestNumber.Format(2026, sequence), owner.UserId, at, null);
                return RepairRequestAudit.Submitted(context, firstCopy, duplicateCount, at);
            }, None);
        var second = await secondScope.ServiceProvider.GetRequiredService<IRepairRequestSubmitStore>().SubmitAsync(
            staleCopy, staleCopy.RowVersion, QueryFor(staleCopy, at), 2026, hasContinuationReason: false, (sequence, duplicateCount) =>
            {
                staleCopy.Submit(RequestNumber.Format(2026, sequence), owner.UserId, at, null);
                return RepairRequestAudit.Submitted(context, staleCopy, duplicateCount, at);
            }, None);

        Assert.Equal(RepairRequestSubmitStatus.Saved, first.Status);
        Assert.Equal(RepairRequestSubmitStatus.ConcurrencyConflict, second.Status);
        Assert.Equal(1, await CounterAsync(world.TenantId, 2026));
        Assert.Equal("RR-2026-000001", await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == draftId).Select(request => request.RequestNo).SingleAsync()));
        Assert.Single(await SubmittedAuditsAsync(draftId));

        // The rolled-back increment left no gap for the next Submit.
        await SubmitThroughStoreAsync(owner, await CompleteDraftAsync(owner, world.SiteA1));
        Assert.Equal(2, await CounterAsync(world.TenantId, 2026));
    }

    [Fact]
    public async Task FailureInsideTheTransaction_RollsBackAllocationTransitionAndAudit_AndReleasesTheDuplicateLock()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var draftId = await CompleteDraftAsync(owner, world.SiteA1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubmitThroughStoreAsync(owner, draftId, beforeApply: () => throw new InvalidOperationException("Simulated failure")));

        Assert.Null(await CounterAsync(world.TenantId, 2026));
        await AssertNotSubmittedAsync(draftId);
        Assert.True(await IsDuplicateKeyFreeAsync(DuplicateKey(world.TenantId, world.SiteA1, "ELECTRICAL")));

        // A matching Draft proceeds at once and sees no duplicate: the failed Submit left neither a lock nor a row behind.
        var next = await SubmitThroughStoreAsync(owner, await CompleteDraftAsync(owner, world.SiteA1));
        Assert.Equal(new RepairRequestSubmitResult(RepairRequestSubmitStatus.Saved, 0), next);
        Assert.Equal(1, await CounterAsync(world.TenantId, 2026));
    }

    // ---------------- Duplicate guard under concurrency ----------------

    [Fact]
    public async Task ConcurrentSubmitsOfDifferentDraftsWithTheSameDuplicateKey_ExactlyOneSucceeds_TheOtherGetsTheDuplicateWarning()
    {
        var world = await WorldAsync();
        var firstOwner = await RequesterAsync(world.TenantId, world.SiteA1);
        var secondOwner = await RequesterAsync(world.TenantId, world.SiteA1);
        var firstDraft = await EvidencedDraftAsync(firstOwner, world.SiteA1);
        var secondDraft = await EvidencedDraftAsync(secondOwner, world.SiteA1);
        var key = DuplicateKey(world.TenantId, world.SiteA1, "ELECTRICAL");

        // Force true overlap: both Submits pass validation and wait on the same key before either may enter the critical section.
        Task<CommandResult<RepairRequestDraftDto>>[] submits;
        await using (await HeldDuplicateLock.AcquireAsync(_host, key))
        {
            submits =
            [
                Task.Run(() => SubmitThroughServiceAsync(firstOwner, firstDraft)),
                Task.Run(() => SubmitThroughServiceAsync(secondOwner, secondDraft))
            ];
            await WaitForDuplicateLockWaitersAsync(2);
        }

        var results = await Task.WhenAll(submits).WaitAsync(ConcurrencyTimeout);

        var winner = Assert.Single(results, result => result.Succeeded);
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Equal(CommandFailure.ValidationFailed, loser.Error!.Failure);
        Assert.Equal(1, loser.Error.DuplicateCount);
        Assert.Equal(new[] { RepairRequestFields.DuplicateContinuationReason }, loser.Error.Errors.Keys);
        Assert.Equal(RequestNumber.Format(ServiceYear, 1), winner.Value!.RequestNo);

        var (loserId, loserOwner) = winner.Value.Id == firstDraft ? (secondDraft, secondOwner) : (firstDraft, firstOwner);
        await AssertNotSubmittedAsync(loserId);
        Assert.Single(await SubmittedAuditsAsync(winner.Value.Id));
        Assert.Equal(1, await CounterAsync(world.TenantId, ServiceYear));
        Assert.True(await IsDuplicateKeyFreeAsync(key));

        // The approved continuation path still works for the rejected Draft.
        var continued = await SubmitThroughServiceAsync(loserOwner, loserId, "  Different fault on the same asset  ");
        Assert.True(continued.Succeeded);
        Assert.Equal(RequestNumber.Format(ServiceYear, 2), continued.Value!.RequestNo);
        var audit = Assert.Single(await SubmittedAuditsAsync(loserId));
        Assert.Equal("Different fault on the same asset", audit.Reason);
        Assert.Contains("\"duplicateCount\":1", audit.NewValueJson);

        // An unforced race of two more matching Drafts (another Category) also admits exactly one Submit without a reason.
        var racingA = await EvidencedDraftAsync(firstOwner, world.SiteA1, category: "PLUMBING");
        var racingB = await EvidencedDraftAsync(secondOwner, world.SiteA1, category: "PLUMBING");
        var race = await Task.WhenAll(
            Task.Run(() => SubmitThroughServiceAsync(firstOwner, racingA)),
            Task.Run(() => SubmitThroughServiceAsync(secondOwner, racingB))).WaitAsync(ConcurrencyTimeout);
        Assert.Single(race, result => result.Succeeded);
        Assert.Equal(1, Assert.Single(race, result => !result.Succeeded).Error!.DuplicateCount);

        var numbers = await WithDbAsync(db => db.RepairRequests
            .Where(request => request.TenantId == world.TenantId && request.RequestNo != null)
            .Select(request => request.RequestNo!)
            .ToListAsync());
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.Equal(3, numbers.Count);
    }

    [Fact]
    public async Task ConcurrentSubmitsOfNonMatchingDrafts_DoNotWaitOnEachOthersDuplicateLock()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var electrical = await EvidencedDraftAsync(owner, world.SiteA1);
        var plumbing = await EvidencedDraftAsync(owner, world.SiteA1, category: "PLUMBING");
        var withEquipment = await EvidencedDraftAsync(owner, world.SiteA1, world.EquipmentA1.Id);
        var electricalKey = DuplicateKey(world.TenantId, world.SiteA1, "ELECTRICAL");

        Task<CommandResult<RepairRequestDraftDto>> blocked;
        await using (await HeldDuplicateLock.AcquireAsync(_host, electricalKey))
        {
            blocked = Task.Run(() => SubmitThroughServiceAsync(owner, electrical));
            await WaitForDuplicateLockWaitersAsync(1);

            // Other keys (another Category; the same Category with Equipment) proceed while the ELECTRICAL/no-Equipment key is busy.
            var unrelated = await Task.WhenAll(
                Task.Run(() => SubmitThroughServiceAsync(owner, plumbing)),
                Task.Run(() => SubmitThroughServiceAsync(owner, withEquipment))).WaitAsync(ConcurrencyTimeout);

            Assert.All(unrelated, result => Assert.True(result.Succeeded));
            Assert.False(blocked.IsCompleted);
        }

        var released = await blocked.WaitAsync(ConcurrencyTimeout);

        Assert.True(released.Succeeded);
        var numbers = await WithDbAsync(db => db.RepairRequests
            .Where(request => request.TenantId == world.TenantId && request.RequestNo != null)
            .Select(request => request.RequestNo!)
            .ToListAsync());
        Assert.Equal(Enumerable.Range(1, 3).Select(sequence => RequestNumber.Format(ServiceYear, sequence)), numbers.Order());
    }

    [Fact]
    public async Task DuplicateKeyLockTimeout_Returns409_WritesNothing_AndTheDraftCanBeSubmittedAfterwards()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var draft = await EvidencedDraftAsync(owner, world.SiteA1);
        await using var impatientHost = new AuthenticationTestHost(services =>
            services.AddSingleton(new RepairRequestDuplicateLockOptions { TimeoutMilliseconds = 200 }));

        await using (await HeldDuplicateLock.AcquireAsync(_host, DuplicateKey(world.TenantId, world.SiteA1, "ELECTRICAL")))
        {
            await using var scope = impatientHost.CreateScope();
            var rowVersion = (await scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().GetAsync(owner, draft, None))!.RowVersion;
            var result = await scope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
                .SubmitAsync(new CommandContext(owner, Guid.NewGuid()), draft, rowVersion, null, None);

            Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        }

        await AssertNotSubmittedAsync(draft);
        Assert.Null(await CounterAsync(world.TenantId, ServiceYear));
        Assert.True((await SubmitThroughServiceAsync(owner, draft)).Succeeded);
    }

    // ---------------- CLEAN photo ----------------

    [Fact]
    public async Task CleanPhoto_OnlyCleanJpegOrPngOfTheRequestTenantCounts()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var otherOwner = await RequesterAsync(world.OtherTenantId, world.OtherTenantSite);

        async Task<bool> HasCleanPhotoAfter(params (string MimeType, MalwareScanStatus Status)[] files)
        {
            var requestId = await CompleteDraftAsync(owner, world.SiteA1);
            foreach (var (mimeType, status) in files)
            {
                await AttachAsync(owner, requestId, mimeType, status);
            }

            await using var scope = _host.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IRepairRequestSubmitStore>().HasCleanPhotoAsync(world.TenantId, requestId, None);
        }

        Assert.False(await HasCleanPhotoAfter());
        Assert.False(await HasCleanPhotoAfter(("image/png", MalwareScanStatus.Pending)));
        Assert.False(await HasCleanPhotoAfter(("image/jpeg", MalwareScanStatus.Failed)));
        Assert.False(await HasCleanPhotoAfter(("application/pdf", MalwareScanStatus.Clean)));
        Assert.True(await HasCleanPhotoAfter(("image/png", MalwareScanStatus.Clean)));
        Assert.True(await HasCleanPhotoAfter(("image/jpeg", MalwareScanStatus.Clean), ("image/png", MalwareScanStatus.Pending)));
        Assert.True(await HasCleanPhotoAfter(("image/jpeg", MalwareScanStatus.Clean), ("image/png", MalwareScanStatus.Failed)));

        // A file of another tenant linked to the request never counts.
        var requestId = await CompleteDraftAsync(owner, world.SiteA1);
        await AttachAsync(otherOwner, requestId, "image/png", MalwareScanStatus.Clean);
        await using var verify = _host.CreateScope();
        Assert.False(await verify.ServiceProvider.GetRequiredService<IRepairRequestSubmitStore>().HasCleanPhotoAsync(world.TenantId, requestId, None));
    }

    // ---------------- Duplicate COUNT ----------------

    [Fact]
    public async Task DuplicateCount_MatchesSiteCategoryAndExactEquipment_InTheActiveSetWithinTheInclusiveWindow()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var windowStart = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

        async Task<Guid> Existing(RepairRequestStatus status, DateTime submittedAt, Guid? equipmentId = null, string category = "ELECTRICAL", Site? site = null)
        {
            var id = await CompleteDraftAsync(owner, site ?? world.SiteA1, equipmentId, category);
            await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(request => request.Status, status)
                .SetProperty(request => request.SubmittedAt, submittedAt)));
            return id;
        }

        await Existing(RepairRequestStatus.Submitted, windowStart);                       // counts: inclusive window start
        await Existing(RepairRequestStatus.UnderReview, windowStart.AddHours(5));          // counts
        await Existing(RepairRequestStatus.Approved, windowStart.AddHours(6));             // counts
        await Existing(RepairRequestStatus.Converted, windowStart.AddHours(7));            // counts (no Work Order yet)
        await Existing(RepairRequestStatus.Rejected, windowStart.AddHours(8));             // excluded state
        await Existing(RepairRequestStatus.Cancelled, windowStart.AddHours(9));            // excluded state
        await Existing(RepairRequestStatus.Draft, windowStart.AddHours(10));               // excluded state
        await Existing(RepairRequestStatus.Submitted, windowStart.AddMilliseconds(-1));    // before the window
        await Existing(RepairRequestStatus.Submitted, windowStart.AddHours(1), category: "PLUMBING");
        await Existing(RepairRequestStatus.Submitted, windowStart.AddHours(1), world.EquipmentA1.Id);
        var self = await Existing(RepairRequestStatus.Submitted, windowStart.AddHours(2));  // the request itself is excluded

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestSubmitStore>();

        var withoutEquipment = await store.CountDuplicatesAsync(new DuplicateQuery(world.TenantId, self, world.SiteA1.Id, "ELECTRICAL", null, windowStart), None);
        var withEquipment = await store.CountDuplicatesAsync(new DuplicateQuery(world.TenantId, Guid.NewGuid(), world.SiteA1.Id, "ELECTRICAL", world.EquipmentA1.Id, windowStart), None);
        var otherEquipment = await store.CountDuplicatesAsync(new DuplicateQuery(world.TenantId, Guid.NewGuid(), world.SiteA1.Id, "ELECTRICAL", world.EquipmentB1.Id, windowStart), None);
        var otherSite = await store.CountDuplicatesAsync(new DuplicateQuery(world.TenantId, Guid.NewGuid(), world.SiteA2.Id, "ELECTRICAL", null, windowStart), None);
        var otherTenant = await store.CountDuplicatesAsync(new DuplicateQuery(world.OtherTenantId, Guid.NewGuid(), world.SiteA1.Id, "ELECTRICAL", null, windowStart), None);

        Assert.Equal(4, withoutEquipment);
        Assert.Equal(1, withEquipment);
        Assert.Equal(0, otherEquipment);
        Assert.Equal(0, otherSite);
        Assert.Equal(0, otherTenant);
    }

    [Fact]
    public void DuplicateLockResource_IsTheExactDuplicateKey_AndEmptyEquipmentNeverSharesAKeyWithSelectedEquipment()
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        var equipment = Guid.NewGuid();

        var withoutEquipment = RepairRequestDuplicateLock.Resource(new DuplicateQuery(tenant, Guid.NewGuid(), site, "ELECTRICAL", null, DateTime.UnixEpoch));
        var sameKeyOtherRequest = RepairRequestDuplicateLock.Resource(new DuplicateQuery(tenant, Guid.NewGuid(), site, "ELECTRICAL", null, DateTime.UtcNow));
        var withEquipment = RepairRequestDuplicateLock.Resource(new DuplicateQuery(tenant, Guid.NewGuid(), site, "ELECTRICAL", equipment, DateTime.UnixEpoch));
        var otherCategory = RepairRequestDuplicateLock.Resource(new DuplicateQuery(tenant, Guid.NewGuid(), site, "PLUMBING", null, DateTime.UnixEpoch));

        Assert.Equal(withoutEquipment, sameKeyOtherRequest);
        Assert.NotEqual(withoutEquipment, withEquipment);
        Assert.NotEqual(withoutEquipment, otherCategory);
        Assert.True(RepairRequestDuplicateLock.Resource(new DuplicateQuery(tenant, Guid.NewGuid(), site, new string('C', RepairRequestAggregate.RequestCategoryCodeMaxLength), equipment, DateTime.UnixEpoch)).Length <= 255);
    }

    // ---------------- Lookup seeding ----------------

    [Fact]
    public async Task LookupSeeding_IsIdempotent_ConcurrencySafe_AndCoversTenantsWithUsers()
    {
        var tenantId = Guid.NewGuid();
        await using (var scope = _host.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>();
            Assert.Equal(10, await seeder.SeedTenantAsync(tenantId, None));
            Assert.Equal(0, await seeder.SeedTenantAsync(tenantId, None));
        }

        var concurrentTenant = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var scope = _host.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedTenantAsync(concurrentTenant, None);
        })));

        var userTenant = Guid.NewGuid();
        await _host.CreateUserInTenantAsync(userTenant, RoleCodes.Requester);
        await using (var scope = _host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedAllTenantsAsync(None);
        }

        foreach (var tenant in new[] { tenantId, concurrentTenant, userTenant })
        {
            Assert.Equal(
                RequestLookupSeeder.CategorySeed.Select(seed => seed.Code).Order(),
                await WithDbAsync(db => db.RequestCategories.Where(category => category.TenantId == tenant).Select(category => category.Code).OrderBy(code => code).ToListAsync()));
            Assert.Equal(
                RequestLookupSeeder.PrioritySeed.Select(seed => seed.Code).Order(),
                await WithDbAsync(db => db.RequestPriorities.Where(priority => priority.TenantId == tenant).Select(priority => priority.Code).OrderBy(code => code).ToListAsync()));
        }
    }

    // ---------------- Full service path ----------------

    [Fact]
    public async Task SubmitService_EndToEnd_IsBounded_AndFailedValidationLeavesNoTrace()
    {
        var world = await WorldAsync();
        var owner = await RequesterAsync(world.TenantId, world.SiteA1);
        var valid = await CompleteDraftAsync(owner, world.SiteA1);
        var withoutPhoto = await CompleteDraftAsync(owner, world.SiteA1, world.EquipmentA1.Id);
        await AttachAsync(owner, valid, "image/png", MalwareScanStatus.Clean);
        var year = ServiceYear;

        async Task<(CommandResult<RepairRequestDraftDto> Result, int Commands)> SubmitAsync(Guid id)
        {
            await using var scope = _host.CreateScope();
            var rowVersion = (await scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().GetAsync(owner, id, None))!.RowVersion;
            var service = scope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>();
            _host.Logs.Clear();
            var result = await service.SubmitAsync(new CommandContext(owner, Guid.NewGuid()), id, rowVersion, null, None);
            return (result, _host.Logs.ExecutedDbCommandCount);
        }

        var rejected = await SubmitAsync(withoutPhoto);

        Assert.Equal(CommandFailure.ValidationFailed, rejected.Result.Error?.Failure);
        Assert.Null(await CounterAsync(world.TenantId, year));
        await AssertNotSubmittedAsync(withoutPhoto);

        var accepted = await SubmitAsync(valid);

        Assert.True(accepted.Result.Succeeded);
        Assert.Equal(RequestNumber.Format(year, 1), accepted.Result.Value!.RequestNo);

        // Submit: owned load, selection, CLEAN photo, duplicate-key lock, duplicate COUNT, counter MERGE, UPDATE + audit INSERT (7).
        // Routing after the commit (DEC-PRE-S1-007R-01): routing-key application lock, request row lock, tracked reload, step
        // approval lookup, active route lookup, routing audit INSERT (6). No route is configured here, so routing records
        // ROUTE_NOT_FOUND.
        Assert.InRange(accepted.Commands, 1, 13);
        Assert.Equal(1, await CounterAsync(world.TenantId, year));
        var audit = Assert.Single(await SubmittedAuditsAsync(valid));
        Assert.Equal("SUBMITTED", audit.ToState);
    }
}
