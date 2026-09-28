using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.Tests.WorkOrders;

/// <summary>
/// Work Order Close (ST-WO-006; WO-API-010; BR-08; `docs/13` §4.20): the service's own check order, every guard
/// with its specific message, and the "nothing is written on any failure" rule — against an in-memory store.
/// Scope (404 for another tenant/Site) is the store's responsibility and is proven end to end in the API tests;
/// here the store simply returns null. The "WO-008 is null" guard is not arranged in this file (the public domain
/// API assigns WO-008 in ST-WO-004); it is verified in the API tests by setting the column to NULL directly in SQL.
/// </summary>
public class WorkOrderCloseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 30, 0, TimeSpan.Zero);
    private static readonly Guid ContactId = Guid.NewGuid();

    private readonly FakeWorkOrderCloseStore _store = new();
    private readonly WorkOrderCloseService _service;
    private readonly CommandContext _context = new(new CurrentUser(Guid.NewGuid(), Guid.NewGuid(), [RoleCodes.Supervisor]), Guid.NewGuid());
    private readonly Guid _costSummaryId = Guid.NewGuid();

    public WorkOrderCloseServiceTests()
    {
        _service = new WorkOrderCloseService(_store, new CloseFixedClock(Now));
    }

    private static WorkOrder At(WorkOrderStatus status)
    {
        var workOrder = WorkOrder.Create(Guid.NewGuid(), Guid.NewGuid(), "WO-1", DateTime.UtcNow);

        // The Id is assigned on insert by EF (sequential GUID) and has a private setter; a persisted Work Order
        // always has one, and the audit row requires it — the same test-only idiom AttachmentFakes uses.
        workOrder.GetType().GetProperty(nameof(WorkOrder.Id))!.SetValue(workOrder, Guid.NewGuid());

        if (status == WorkOrderStatus.Open)
        {
            return workOrder;
        }

        workOrder.Schedule(Guid.NewGuid());
        if (status == WorkOrderStatus.Scheduled)
        {
            return workOrder;
        }

        workOrder.BeginWork();
        if (status == WorkOrderStatus.InProgress)
        {
            return workOrder;
        }

        workOrder.SubmitWorkSummary();
        if (status == WorkOrderStatus.AwaitingSupervisorReview)
        {
            return workOrder;
        }

        workOrder.SubmitForAcceptance(ContactId, "{}");
        if (status == WorkOrderStatus.AwaitingCustomerAcceptance)
        {
            return workOrder;
        }

        if (status == WorkOrderStatus.CorrectiveActionRequired)
        {
            workOrder.Reject();
            return workOrder;
        }

        workOrder.Accept();
        return workOrder;
    }

    /// <summary>A COMPLETED Work Order whose every prerequisite holds (the store's default evidence).</summary>
    private WorkOrder Ready()
    {
        var workOrder = At(WorkOrderStatus.Completed);
        _store.WorkOrder = workOrder;
        _store.Evidence = new WorkOrderCloseEvidence(HasWorkSummary: true, AcceptanceDecision.Accept, _costSummaryId, Now.UtcDateTime.AddHours(-1));
        return workOrder;
    }

    private Task<CommandResult<Guid>> CloseAsync(WorkOrder workOrder, byte[]? rowVersion = null) =>
        _service.CloseAsync(_context, workOrder.Id, rowVersion ?? workOrder.RowVersion, CancellationToken.None);

    private void AssertNothingWritten()
    {
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.SaveCalls);
    }

    // ---------------- Success ----------------

    [Fact]
    public async Task Close_WhenEveryPrerequisiteHolds_ClosesTheWorkOrder_AndWritesOneAuditRow()
    {
        var workOrder = Ready();

        var result = await CloseAsync(workOrder);

        Assert.True(result.Succeeded);
        Assert.Equal(workOrder.Id, result.Value);
        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.Equal(_context.User.UserId, workOrder.ClosedBy);
        Assert.Equal(Now.UtcDateTime, workOrder.ClosedAt);
        Assert.Equal(1, _store.SaveCalls);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal("WORK_ORDER", audit.EntityType);
        Assert.Equal(workOrder.Id, audit.EntityId);
        Assert.Equal("WORK_ORDER_CLOSED", audit.ActionCode);
        Assert.Equal("COMPLETED", audit.FromState);
        Assert.Equal("CLOSED", audit.ToState);
        Assert.Equal(_context.User.UserId, audit.ActorId);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAt);
        Assert.Equal(_context.CorrelationId, audit.CorrelationId);
        Assert.Null(audit.Reason);

        var newValue = JsonDocument.Parse(audit.NewValueJson!).RootElement;
        Assert.Equal(_costSummaryId, newValue.GetProperty("costSummaryId").GetGuid());
        Assert.DoesNotContain("closedBy", audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Close_SavesWithTheClientsRowVersionAsTheCompareAndSwapToken()
    {
        var workOrder = Ready();

        await CloseAsync(workOrder);

        Assert.Equal(workOrder.RowVersion, _store.LastExpectedRowVersion);
    }

    // ---------------- Scope / concurrency / state (check order) ----------------

    [Fact]
    public async Task Close_WhenTheWorkOrderIsOutOfScopeOrMissing_IsNotFound_AndWritesNothing()
    {
        _store.WorkOrder = null;

        var result = await _service.CloseAsync(_context, Guid.NewGuid(), [], CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error!.Failure);
        Assert.False(_store.EvidenceRead);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Close_WithAStaleRowVersion_IsAConcurrencyConflict_BeforeAnyGuard_AndWritesNothing()
    {
        var workOrder = Ready();
        // Even with a broken prerequisite, a stale token is reported first (check order: concurrency before state).
        _store.Evidence = new WorkOrderCloseEvidence(false, null, null, null);

        var result = await CloseAsync(workOrder, rowVersion: [9, 9, 9]);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error!.Failure);
        Assert.False(_store.EvidenceRead);
        AssertNothingWritten();
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    public async Task Close_WhenNotCompleted_IsAStateConflict_AndWritesNothing(WorkOrderStatus status)
    {
        var workOrder = At(status);
        _store.WorkOrder = workOrder;
        _store.Evidence = new WorkOrderCloseEvidence(true, AcceptanceDecision.Accept, _costSummaryId, Now.UtcDateTime);

        var result = await CloseAsync(workOrder);

        Assert.Equal(CommandFailure.StateConflict, result.Error!.Failure);
        Assert.Equal("Only a COMPLETED Work Order can be closed.", result.Error.Message);
        Assert.False(_store.EvidenceRead);
        AssertNothingWritten();
        Assert.Equal(status, workOrder.Status);
        Assert.Null(workOrder.ClosedAt);
    }

    [Fact]
    public async Task Close_WhenAlreadyClosed_IsAStateConflict_WithTheAlreadyClosedMessage_AndWritesNoSecondAudit()
    {
        var workOrder = Ready();
        Assert.True((await CloseAsync(workOrder)).Succeeded);
        var closedAt = workOrder.ClosedAt;

        var second = await CloseAsync(workOrder);

        Assert.Equal(CommandFailure.StateConflict, second.Error!.Failure);
        Assert.Equal("This Work Order was already closed.", second.Error.Message);
        Assert.Single(_store.Audits);
        Assert.Equal(1, _store.SaveCalls);
        Assert.Equal(closedAt, workOrder.ClosedAt);
    }

    // ---------------- Guards (each prerequisite from docs/13 §4.20 Q2) ----------------

    [Fact]
    public async Task Close_WithoutACurrentWorkSummary_IsAStateConflict()
    {
        var workOrder = Ready();
        _store.Evidence = _store.Evidence with { HasWorkSummary = false };

        var result = await CloseAsync(workOrder);

        Assert.Equal(CommandFailure.StateConflict, result.Error!.Failure);
        Assert.Contains("no Work Summary", result.Error.Message);
        AssertNothingWritten();
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AcceptanceDecision.Reject)]
    public async Task Close_WhenTheLatestAcceptanceIsNotAnAccept_IsAStateConflict(AcceptanceDecision? latest)
    {
        // The store reports the decision of the HIGHEST round only, so an earlier ACCEPT superseded by a later
        // REJECT (or no decision at all) reaches the service as anything but Accept.
        var workOrder = Ready();
        _store.Evidence = _store.Evidence with { LatestAcceptanceDecision = latest };

        var result = await CloseAsync(workOrder);

        Assert.Equal(CommandFailure.StateConflict, result.Error!.Failure);
        Assert.Contains("has not accepted", result.Error.Message);
        AssertNothingWritten();
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    [Fact]
    public async Task Close_WithoutACostSummary_IsAStateConflict()
    {
        var workOrder = Ready();
        _store.Evidence = _store.Evidence with { CostSummaryId = null, CostSummaryReviewedAt = null };

        var result = await CloseAsync(workOrder);

        Assert.Equal(CommandFailure.StateConflict, result.Error!.Failure);
        Assert.Contains("no Cost Summary", result.Error.Message);
        AssertNothingWritten();
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    [Fact]
    public async Task Close_WithAPreparedButUnreviewedCostSummary_IsAStateConflict()
    {
        var workOrder = Ready();
        _store.Evidence = _store.Evidence with { CostSummaryReviewedAt = null };

        var result = await CloseAsync(workOrder);

        Assert.Equal(CommandFailure.StateConflict, result.Error!.Failure);
        Assert.Contains("has not been reviewed", result.Error.Message);
        AssertNothingWritten();
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    [Fact]
    public async Task Close_ReportsTheFirstUnmetGuard_InTheDocumentedOrder()
    {
        // Work Summary, then acceptance, then Cost Summary existence, then Cost Summary review.
        var workOrder = Ready();
        _store.Evidence = new WorkOrderCloseEvidence(false, null, null, null);

        var first = await CloseAsync(workOrder);
        Assert.Contains("no Work Summary", first.Error!.Message);

        _store.Evidence = new WorkOrderCloseEvidence(true, null, null, null);
        var second = await CloseAsync(workOrder);
        Assert.Contains("has not accepted", second.Error!.Message);

        _store.Evidence = new WorkOrderCloseEvidence(true, AcceptanceDecision.Accept, null, null);
        var third = await CloseAsync(workOrder);
        Assert.Contains("no Cost Summary", third.Error!.Message);

        _store.Evidence = new WorkOrderCloseEvidence(true, AcceptanceDecision.Accept, _costSummaryId, null);
        var fourth = await CloseAsync(workOrder);
        Assert.Contains("has not been reviewed", fourth.Error!.Message);

        AssertNothingWritten();
    }

    // ---------------- Persistence outcome ----------------

    [Fact]
    public async Task Close_WhenTheSaveLosesTheRace_IsAConcurrencyConflict()
    {
        var workOrder = Ready();
        _store.SaveOutcome = WorkOrderSaveOutcome.ConcurrencyConflict;

        var result = await CloseAsync(workOrder);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error!.Failure);
    }

    [Fact]
    public async Task Close_RunsInsideTheStoresTransaction()
    {
        var workOrder = Ready();

        await CloseAsync(workOrder);

        Assert.Equal(1, _store.TransactionCount);
    }
}

internal sealed class FakeWorkOrderCloseStore : IWorkOrderCloseStore
{
    public WorkOrder? WorkOrder { get; set; }

    public WorkOrderCloseEvidence Evidence { get; set; } = new(false, null, null, null);

    public WorkOrderSaveOutcome SaveOutcome { get; set; } = WorkOrderSaveOutcome.Saved;

    public List<AuditHistory> Audits { get; } = [];

    public bool EvidenceRead { get; private set; }

    public int SaveCalls { get; private set; }

    public int TransactionCount { get; private set; }

    public byte[]? LastExpectedRowVersion { get; private set; }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        TransactionCount++;
        return await command();
    }

    public Task<WorkOrder?> LoadInScopeAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(WorkOrder);

    public Task<WorkOrderCloseEvidence> GetCloseEvidenceAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        EvidenceRead = true;
        return Task.FromResult(Evidence);
    }

    public void AddAudit(AuditHistory audit) => Audits.Add(audit);

    public Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        SaveCalls++;
        LastExpectedRowVersion = expectedRowVersion;
        return Task.FromResult(SaveOutcome);
    }
}

internal sealed class CloseFixedClock(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
