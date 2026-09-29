using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.Tests.WorkOrders;

/// <summary>
/// Corrective Action Submit Plan (ST-CA-002; CA-API-001) and Approve Plan (ST-CA-003; CA-API-002) — `docs/13`
/// §4.21; Ticket 6: the service's own check order, every guard with its specific message, and "nothing is
/// written on any failure" — against an in-memory store. Scope (404 for another tenant/Site) is the store's own
/// responsibility and is proven end to end in the API tests; here the store simply returns (null, null).
/// </summary>
public class CorrectiveActionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeCorrectiveActionStore _store = new();
    private readonly CorrectiveActionService _service;
    private readonly Guid _teamLeadId = Guid.NewGuid();
    private readonly Guid _supervisorId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _planFileAssetId = Guid.NewGuid();

    public CorrectiveActionServiceTests()
    {
        _service = new CorrectiveActionService(_store, new CorrectiveActionFixedClock(Now));
        _store.PlanFileUsable = true;
    }

    private CommandContext TeamLeadContext() => new(new CurrentUser(_teamLeadId, _tenantId, [RoleCodes.TeamLead]), Guid.NewGuid());

    private CommandContext SupervisorContext() => new(new CurrentUser(_supervisorId, _tenantId, [RoleCodes.Supervisor]), Guid.NewGuid());

    /// <summary>
    /// A DRAFT Corrective Action and its Work Order (walked through the real ST-WO-001..007 transitions to
    /// CORRECTIVE_ACTION_REQUIRED, the only status ST-WO-008/SubmitPlan accepts), both wired into the fake store
    /// as "in scope."
    /// </summary>
    private (WorkOrder WorkOrder, CorrectiveAction CorrectiveAction) Ready()
    {
        var workOrder = WorkOrder.Create(_tenantId, Guid.NewGuid(), "WO-1", DateTime.UtcNow);
        SetId(workOrder, Guid.NewGuid());
        workOrder.Schedule(Guid.NewGuid());
        workOrder.BeginWork();
        workOrder.SubmitWorkSummary();
        workOrder.SubmitForAcceptance(Guid.NewGuid(), "{}");
        workOrder.Reject();

        var correctiveAction = CorrectiveAction.CreateDraft(_tenantId, workOrder.Id, Guid.NewGuid(), cycleNo: 1);
        SetId(correctiveAction, Guid.NewGuid());

        _store.WorkOrder = workOrder;
        _store.CorrectiveAction = correctiveAction;
        return (workOrder, correctiveAction);
    }

    private static void SetId(object entity, Guid id) => entity.GetType().GetProperty("Id")!.SetValue(entity, id);

    private void AssertNothingWritten()
    {
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.SaveCalls);
    }

    // ---------------- Submit Plan: success ----------------

    [Fact]
    public async Task SubmitPlan_FromDraft_BindsTheOwnerAndPlan_AndWritesOneAuditRow()
    {
        var (workOrder, correctiveAction) = Ready();

        var result = await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, "Replace the seal.", _planFileAssetId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, correctiveAction.Status);
        Assert.Equal(_teamLeadId, correctiveAction.OwnerTeamLeadId);
        Assert.Equal("Replace the seal.", correctiveAction.PlanText);
        Assert.Equal(_planFileAssetId, correctiveAction.PlanFileAssetId);
        Assert.Equal(WorkOrderStatus.CorrectivePlanPending, workOrder.Status);
        Assert.Equal(1, _store.SaveCalls);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal("CORRECTIVE_ACTION", audit.EntityType);
        Assert.Equal(correctiveAction.Id, audit.EntityId);
        Assert.Equal("CORRECTIVE_ACTION_PLAN_SUBMITTED", audit.ActionCode);
        Assert.Equal("DRAFT", audit.FromState);
        Assert.Equal("PENDING_PLAN_APPROVAL", audit.ToState);
        Assert.Equal(_teamLeadId, audit.ActorId);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAt);
    }

    [Fact]
    public async Task SubmitPlan_TrimsThePlanText()
    {
        var (workOrder, correctiveAction) = Ready();

        await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, "  Replace the seal.  ", _planFileAssetId, CancellationToken.None);

        Assert.Equal("Replace the seal.", correctiveAction.PlanText);
    }

    [Fact]
    public async Task SubmitPlan_SavesWithTheWorkOrdersOwnRowVersionAsTheCompareAndSwapToken()
    {
        var (workOrder, correctiveAction) = Ready();

        await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, "Plan.", _planFileAssetId, CancellationToken.None);

        Assert.Equal(workOrder.RowVersion, _store.LastExpectedRowVersion);
    }

    // ---------------- Submit Plan: scope / concurrency / state ----------------

    [Fact]
    public async Task SubmitPlan_WhenOutOfScopeOrMissing_IsNotFound_AndWritesNothing()
    {
        _store.WorkOrder = null;
        _store.CorrectiveAction = null;

        var result = await _service.SubmitPlanAsync(TeamLeadContext(), Guid.NewGuid(), [], "Plan.", _planFileAssetId, CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error!.Failure);
        AssertNothingWritten();
    }

    [Fact]
    public async Task SubmitPlan_WithAStaleRowVersion_IsAConcurrencyConflict_BeforeAnyGuard_AndWritesNothing()
    {
        var (_, correctiveAction) = Ready();

        var result = await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, [9, 9, 9], "Plan.", _planFileAssetId, CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error!.Failure);
        AssertNothingWritten();
        Assert.Equal(CorrectiveActionStatus.Draft, correctiveAction.Status);
    }

    [Fact]
    public async Task SubmitPlan_WhenAlreadySubmitted_IsAStateConflict_AndWritesNoSecondAudit()
    {
        var (workOrder, correctiveAction) = Ready();
        correctiveAction.SubmitPlan(_teamLeadId, "First plan.", _planFileAssetId);
        workOrder.SubmitCorrectivePlan();

        var result = await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, "Second plan.", _planFileAssetId, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error!.Failure);
        Assert.Equal("This Corrective Action's plan has already been submitted.", result.Error.Message);
        AssertNothingWritten();
        Assert.Equal("First plan.", correctiveAction.PlanText);
    }

    // ---------------- Submit Plan: validation ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitPlan_WithoutAPlanText_IsAValidationFailure(string? planText)
    {
        var (workOrder, correctiveAction) = Ready();

        var result = await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, planText, _planFileAssetId, CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error!.Failure);
        Assert.Contains(CorrectiveActionFields.PlanText, result.Error.Errors.Keys);
        AssertNothingWritten();
    }

    [Fact]
    public async Task SubmitPlan_WithoutAPlanFileAssetId_IsAValidationFailure()
    {
        var (workOrder, correctiveAction) = Ready();

        var result = await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, "Plan.", null, CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error!.Failure);
        Assert.Contains(CorrectiveActionFields.PlanFileAssetId, result.Error.Errors.Keys);
        AssertNothingWritten();
    }

    [Fact]
    public async Task SubmitPlan_WhenThePlanFileIsNotUsable_IsAValidationFailure()
    {
        // The store's single IsPlanFileUsableAsync check stands in for "missing", "another tenant", "PENDING" and
        // "FAILED" alike (UC-WO-023 Preconditions: "Plan text + CLEAN evidence") — each concrete case is proven
        // against the real database in the API tests; here only the service's own reaction to "not usable" matters.
        var (workOrder, correctiveAction) = Ready();
        _store.PlanFileUsable = false;

        var result = await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, "Plan.", _planFileAssetId, CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error!.Failure);
        Assert.Contains(CorrectiveActionFields.PlanFileAssetId, result.Error.Errors.Keys);
        AssertNothingWritten();
    }

    [Fact]
    public async Task SubmitPlan_ChecksThePlanFileAgainstTheWorkOrdersOwnTenant()
    {
        var (workOrder, correctiveAction) = Ready();
        _store.PlanFileUsable = true;

        await _service.SubmitPlanAsync(TeamLeadContext(), correctiveAction.Id, workOrder.RowVersion, "Plan.", _planFileAssetId, CancellationToken.None);

        Assert.Equal(workOrder.TenantId, _store.LastPlanFileTenantIdChecked);
        Assert.Equal(_planFileAssetId, _store.LastPlanFileIdChecked);
    }

    // ---------------- Approve Plan: success ----------------

    [Fact]
    public async Task ApprovePlan_FromPendingPlanApproval_Approves_AndWritesOneAuditRow()
    {
        var (workOrder, correctiveAction) = Ready();
        correctiveAction.SubmitPlan(_teamLeadId, "Plan.", _planFileAssetId);
        workOrder.SubmitCorrectivePlan();

        var result = await _service.ApprovePlanAsync(SupervisorContext(), correctiveAction.Id, workOrder.RowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(CorrectiveActionStatus.Approved, correctiveAction.Status);
        Assert.Equal(_supervisorId, correctiveAction.ApprovedBy);
        Assert.Equal(Now.UtcDateTime, correctiveAction.ApprovedAt);
        Assert.Equal(WorkOrderStatus.CorrectivePlanApproved, workOrder.Status);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal("CORRECTIVE_ACTION_PLAN_APPROVED", audit.ActionCode);
        Assert.Equal("PENDING_PLAN_APPROVAL", audit.FromState);
        Assert.Equal("APPROVED", audit.ToState);
        Assert.Equal(_supervisorId, audit.ActorId);
    }

    [Fact]
    public async Task ApprovePlan_BySameUserWhoSubmitted_Succeeds_BecauseThereIsNoSeparationOfDutiesGuard()
    {
        // `docs/13` §4.21 Decision (d): a recorded review risk, not a rule the service enforces.
        var (workOrder, correctiveAction) = Ready();
        var multiRole = Guid.NewGuid();
        correctiveAction.SubmitPlan(multiRole, "Plan.", _planFileAssetId);
        workOrder.SubmitCorrectivePlan();
        var context = new CommandContext(new CurrentUser(multiRole, _tenantId, [RoleCodes.TeamLead, RoleCodes.Supervisor]), Guid.NewGuid());

        var result = await _service.ApprovePlanAsync(context, correctiveAction.Id, workOrder.RowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(multiRole, correctiveAction.ApprovedBy);
    }

    // ---------------- Approve Plan: scope / concurrency / state ----------------

    [Fact]
    public async Task ApprovePlan_WhenOutOfScopeOrMissing_IsNotFound_AndWritesNothing()
    {
        _store.WorkOrder = null;
        _store.CorrectiveAction = null;

        var result = await _service.ApprovePlanAsync(SupervisorContext(), Guid.NewGuid(), [], CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error!.Failure);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ApprovePlan_WithAStaleRowVersion_IsAConcurrencyConflict_AndWritesNothing()
    {
        var (workOrder, correctiveAction) = Ready();
        correctiveAction.SubmitPlan(_teamLeadId, "Plan.", _planFileAssetId);
        workOrder.SubmitCorrectivePlan();

        var result = await _service.ApprovePlanAsync(SupervisorContext(), correctiveAction.Id, [9, 9, 9], CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error!.Failure);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ApprovePlan_WhenStillDraft_IsAStateConflict_AndWritesNothing()
    {
        var (workOrder, correctiveAction) = Ready();

        var result = await _service.ApprovePlanAsync(SupervisorContext(), correctiveAction.Id, workOrder.RowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error!.Failure);
        Assert.Equal("Only a Corrective Action with a submitted plan can be approved.", result.Error.Message);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ApprovePlan_WhenAlreadyApproved_IsAStateConflict_WithTheAlreadyApprovedMessage_AndWritesNoSecondAudit()
    {
        var (workOrder, correctiveAction) = Ready();
        correctiveAction.SubmitPlan(_teamLeadId, "Plan.", _planFileAssetId);
        workOrder.SubmitCorrectivePlan();
        Assert.True((await _service.ApprovePlanAsync(SupervisorContext(), correctiveAction.Id, workOrder.RowVersion, CancellationToken.None)).Succeeded);

        var second = await _service.ApprovePlanAsync(SupervisorContext(), correctiveAction.Id, workOrder.RowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, second.Error!.Failure);
        Assert.Equal("This Corrective Action's plan has already been approved.", second.Error.Message);
        Assert.Single(_store.Audits);
    }

    [Fact]
    public async Task ApprovePlan_RunsInsideTheStoresTransaction()
    {
        var (workOrder, correctiveAction) = Ready();
        correctiveAction.SubmitPlan(_teamLeadId, "Plan.", _planFileAssetId);
        workOrder.SubmitCorrectivePlan();

        await _service.ApprovePlanAsync(SupervisorContext(), correctiveAction.Id, workOrder.RowVersion, CancellationToken.None);

        Assert.Equal(1, _store.TransactionCount);
    }
}

internal sealed class FakeCorrectiveActionStore : ICorrectiveActionStore
{
    public WorkOrder? WorkOrder { get; set; }

    public CorrectiveAction? CorrectiveAction { get; set; }

    public bool PlanFileUsable { get; set; }

    public Guid? LastPlanFileTenantIdChecked { get; private set; }

    public Guid? LastPlanFileIdChecked { get; private set; }

    public List<AuditHistory> Audits { get; } = [];

    public int SaveCalls { get; private set; }

    public int TransactionCount { get; private set; }

    public byte[]? LastExpectedRowVersion { get; private set; }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        TransactionCount++;
        return await command();
    }

    public Task<(WorkOrder? WorkOrder, CorrectiveAction? CorrectiveAction)> LoadInScopeAsync(CurrentUser user, Guid correctiveActionId, CancellationToken cancellationToken) =>
        Task.FromResult((WorkOrder, CorrectiveAction));

    public Task<bool> IsPlanFileUsableAsync(Guid tenantId, Guid fileAssetId, CancellationToken cancellationToken)
    {
        LastPlanFileTenantIdChecked = tenantId;
        LastPlanFileIdChecked = fileAssetId;
        return Task.FromResult(PlanFileUsable);
    }

    public void AddAudit(AuditHistory audit) => Audits.Add(audit);

    public Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        SaveCalls++;
        LastExpectedRowVersion = expectedRowVersion;
        return Task.FromResult(WorkOrderSaveOutcome.Saved);
    }
}

internal sealed class CorrectiveActionFixedClock(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
