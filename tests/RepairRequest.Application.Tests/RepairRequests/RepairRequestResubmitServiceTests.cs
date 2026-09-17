using Microsoft.Extensions.Logging.Abstractions;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.RepairRequests;

/// <summary>
/// S1-010 Resubmit through Submit (ST-RR-002 after ST-RR-006): the same validation order and CLEAN-photo rule; no Request No
/// allocation; Request No, submitted_by and submitted_at kept (DEC-PRE-S1-010-02); a new duplicate check excluding the
/// request itself whose applied reason replaces the stored one (DEC-PRE-S1-010-04); one RESUBMITTED audit; routing after the
/// commit.
/// </summary>
public class RepairRequestResubmitServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, 250, TimeSpan.Zero);
    private static readonly DateTime FirstSubmittedAt = new(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeRepairRequestDraftStore _drafts = new();
    private readonly FakeRepairRequestSubmitStore _submits = new();
    private readonly FakeSubmittedRequestRouter _router = new();
    private readonly RepairRequestSubmitService _service;
    private readonly Guid _tenantId = Guid.NewGuid();

    public RepairRequestResubmitServiceTests()
    {
        _service = new RepairRequestSubmitService(_drafts, _submits, _router, new FixedClock(Now), NullLogger<RepairRequestSubmitService>.Instance);
        _drafts.Categories["ELECTRICAL"] = MasterDataStatus.Active;
        _drafts.Priorities["HIGH"] = MasterDataStatus.Active;
    }

    private (CommandContext Owner, RepairRequestAggregate Request) ReturnedDraft(bool cleanPhoto = true, string? storedReason = null)
    {
        var owner = FakeRepairRequestDraftStore.RequesterContext(_tenantId);
        var siteId = _drafts.AddSite();
        var contactId = _drafts.AddEligibleContact(siteId);
        var request = _drafts.SeedDraft(owner, siteId, null, "Pump leaking", "ELECTRICAL", "HIGH", contactId, Start, Start.AddHours(2));
        request.Submit("RR-2026-000007", owner.User.UserId, FirstSubmittedAt, storedReason);
        request.RouteForReview();
        request.ReturnForCorrection();
        if (cleanPhoto)
        {
            _submits.RequestsWithCleanPhoto.Add(request.Id);
        }

        return (owner, request);
    }

    private Task<CommandResult<RepairRequestDraftDto>> SubmitAsync(CommandContext caller, RepairRequestAggregate request, string? reason = null) =>
        _service.SubmitAsync(caller, request.Id, FakeRepairRequestDraftStore.InitialRowVersion, reason, CancellationToken.None);

    [Fact]
    public async Task Resubmit_KeepsRequestNoAndSubmitData_AllocatesNothing_AuditsResubmitted_AndRoutesAgain()
    {
        var (owner, request) = ReturnedDraft(storedReason: "Separate fault");

        var result = await SubmitAsync(owner, request);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal("RR-2026-000007", result.Value.RequestNo);
        Assert.Equal(FirstSubmittedAt, result.Value.SubmittedAt);
        Assert.Equal(owner.User.UserId, request.SubmittedBy);
        Assert.Equal("Separate fault", request.DuplicateContinuationReason);
        Assert.Empty(_submits.Counters);

        var audit = Assert.Single(_submits.Audits);
        Assert.Equal(RepairRequestAudit.ResubmittedAction, audit.ActionCode);
        Assert.Equal("DRAFT", audit.FromState);
        Assert.Equal("SUBMITTED", audit.ToState);
        Assert.Null(audit.Reason);
        Assert.Equal(owner.User.UserId, audit.ActorId);
        Assert.Equal(new DateTime(2026, 9, 16, 12, 0, 0, 250, DateTimeKind.Utc), audit.OccurredAt);
        Assert.Contains("\"requestNo\":\"RR-2026-000007\"", audit.NewValueJson);
        Assert.Contains("\"submittedAt\":\"2026-09-15T08:00:00Z\"", audit.NewValueJson);

        var query = Assert.Single(_submits.DuplicateQueries);
        Assert.Equal(request.Id, query.RepairRequestId);
        Assert.Equal(Now.UtcDateTime.AddTicks(-(Now.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond)).AddHours(-24), query.WindowStart);
        Assert.Equal([request.Id], _router.RoutedRequestIds);
    }

    [Fact]
    public async Task Resubmit_WithADuplicate_RequiresAReason_ThenTheAppliedReasonReplacesTheStoredOne()
    {
        var (owner, request) = ReturnedDraft(storedReason: "Separate fault");
        _submits.DuplicateCount = 2;

        var warning = await SubmitAsync(owner, request);

        Assert.Equal(CommandFailure.ValidationFailed, warning.Error?.Failure);
        Assert.Equal(2, warning.Error!.DuplicateCount);
        Assert.Equal(RepairRequestStatus.Draft, request.Status);
        Assert.Equal("Separate fault", request.DuplicateContinuationReason);
        Assert.Empty(_submits.Audits);
        Assert.Empty(_router.RoutedRequestIds);

        var result = await SubmitAsync(owner, request, "  Second pump on the same line ");

        Assert.True(result.Succeeded);
        Assert.Equal("Second pump on the same line", request.DuplicateContinuationReason);
        var audit = Assert.Single(_submits.Audits);
        Assert.Equal("Second pump on the same line", audit.Reason);
        Assert.Contains("\"duplicateCount\":2", audit.NewValueJson);
        Assert.Empty(_submits.Counters);
    }

    [Fact]
    public async Task Resubmit_WithoutACleanPhoto_Returns422_AndStaysDraft()
    {
        var (owner, request) = ReturnedDraft(cleanPhoto: false);

        var result = await SubmitAsync(owner, request);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.Attachments));
        Assert.Equal(RepairRequestStatus.Draft, request.Status);
        Assert.Equal("RR-2026-000007", request.RequestNo);
        Assert.Empty(_submits.Audits);
        Assert.Empty(_router.RoutedRequestIds);
    }

    [Fact]
    public async Task Resubmit_ByAnotherUser_IsNotFound_AndAStaleVersionIsConflict()
    {
        var (owner, request) = ReturnedDraft();

        var other = await SubmitAsync(FakeRepairRequestDraftStore.RequesterContext(_tenantId), request);
        var stale = await _service.SubmitAsync(owner, request.Id, [9, 9, 9, 9, 9, 9, 9, 9], null, CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, other.Error?.Failure);
        Assert.Equal(CommandFailure.ConcurrencyConflict, stale.Error?.Failure);
        Assert.Equal(RepairRequestStatus.Draft, request.Status);
        Assert.Empty(_submits.Audits);
    }

    [Fact]
    public async Task Resubmit_SaveConflict_WritesNothing_AndDoesNotRoute()
    {
        var (owner, request) = ReturnedDraft();
        _submits.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;

        var result = await SubmitAsync(owner, request);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_submits.Audits);
        Assert.Empty(_submits.Counters);
        Assert.Empty(_router.RoutedRequestIds);
    }
}
