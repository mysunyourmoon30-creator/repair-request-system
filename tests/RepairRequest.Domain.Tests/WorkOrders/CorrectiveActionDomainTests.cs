using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// <see cref="CorrectiveAction.CreateDraft"/> (ST-CA-001, Ticket 3, unchanged), <see cref="CorrectiveAction.SubmitPlan"/>
/// (ST-CA-002) and <see cref="CorrectiveAction.ApprovePlan"/> (ST-CA-003) — `docs/13` §4.21; Ticket 6. Eligibility
/// (Team Lead/Supervisor role, Site scope) and the linked Work Order's own transition are the Application
/// service's responsibility; these tests cover only the Corrective Action's own state guard and field mutation.
/// </summary>
public class CorrectiveActionDomainTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static CorrectiveAction Draft() =>
        CorrectiveAction.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cycleNo: 1);

    // ---------------- CreateDraft (Ticket 3, unchanged — re-asserted here for Ticket 6's own baseline) ----------------

    [Fact]
    public void CreateDraft_SetsIdentityAndFields_WithOwnerTeamLeadIdStillNull()
    {
        var tenantId = Guid.NewGuid();
        var workOrderId = Guid.NewGuid();
        var acceptanceId = Guid.NewGuid();

        var action = CorrectiveAction.CreateDraft(tenantId, workOrderId, acceptanceId, cycleNo: 1);

        Assert.Equal(tenantId, action.TenantId);
        Assert.Equal(workOrderId, action.WorkOrderId);
        Assert.Equal(acceptanceId, action.AcceptanceId);
        Assert.Equal(1, action.CycleNo);
        Assert.Equal(CorrectiveActionStatus.Draft, action.Status);
        Assert.Null(action.OwnerTeamLeadId);
        Assert.Null(action.PlanText);
        Assert.Null(action.PlanFileAssetId);
        Assert.Null(action.ApprovedBy);
        Assert.Null(action.ApprovedAt);
    }

    // ---------------- SubmitPlan (ST-CA-002) ----------------

    [Fact]
    public void SubmitPlan_FromDraft_MovesToPendingPlanApproval_AndBindsTheOwnerAndPlanFields()
    {
        var action = Draft();
        var teamLeadId = Guid.NewGuid();
        var planFileAssetId = Guid.NewGuid();

        action.SubmitPlan(teamLeadId, "Replace the seal and retest.", planFileAssetId);

        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, action.Status);
        Assert.Equal(teamLeadId, action.OwnerTeamLeadId);
        Assert.Equal("Replace the seal and retest.", action.PlanText);
        Assert.Equal(planFileAssetId, action.PlanFileAssetId);
    }

    [Fact]
    public void SubmitPlan_WhenAlreadyPendingPlanApproval_Throws_AndDoesNotOverwriteTheOwner()
    {
        var action = Draft();
        var firstOwner = Guid.NewGuid();
        action.SubmitPlan(firstOwner, "First plan.", Guid.NewGuid());

        Assert.Throws<DomainRuleViolationException>(() => action.SubmitPlan(Guid.NewGuid(), "Second plan.", Guid.NewGuid()));

        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, action.Status);
        Assert.Equal(firstOwner, action.OwnerTeamLeadId);
        Assert.Equal("First plan.", action.PlanText);
    }

    [Fact]
    public void SubmitPlan_WhenAlreadyApproved_Throws()
    {
        var action = Draft();
        action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.NewGuid());
        action.ApprovePlan(Guid.NewGuid(), Now);

        Assert.Throws<DomainRuleViolationException>(() => action.SubmitPlan(Guid.NewGuid(), "Another plan.", Guid.NewGuid()));

        Assert.Equal(CorrectiveActionStatus.Approved, action.Status);
    }

    [Fact]
    public void SubmitPlan_RequiresANonEmptyOwnerTeamLeadId_AndDoesNotMutate()
    {
        var action = Draft();

        Assert.Throws<ArgumentException>(() => action.SubmitPlan(Guid.Empty, "Plan.", Guid.NewGuid()));

        Assert.Equal(CorrectiveActionStatus.Draft, action.Status);
        Assert.Null(action.OwnerTeamLeadId);
    }

    [Fact]
    public void SubmitPlan_RequiresANonBlankPlanText_AndDoesNotPartiallyBindTheOwner()
    {
        var action = Draft();

        Assert.Throws<ArgumentException>(() => action.SubmitPlan(Guid.NewGuid(), " ", Guid.NewGuid()));

        Assert.Equal(CorrectiveActionStatus.Draft, action.Status);
        Assert.Null(action.OwnerTeamLeadId);
    }

    [Fact]
    public void SubmitPlan_RejectsAPlanTextLongerThanTheMaxLength()
    {
        var action = Draft();

        Assert.Throws<ArgumentException>(() => action.SubmitPlan(Guid.NewGuid(), new string('A', CorrectiveAction.PlanTextMaxLength + 1), Guid.NewGuid()));
    }

    [Fact]
    public void SubmitPlan_RequiresANonEmptyPlanFileAssetId()
    {
        var action = Draft();

        Assert.Throws<ArgumentException>(() => action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.Empty));
        Assert.Equal(CorrectiveActionStatus.Draft, action.Status);
        Assert.Null(action.PlanFileAssetId);
    }

    // ---------------- ApprovePlan (ST-CA-003) ----------------

    [Fact]
    public void ApprovePlan_FromPendingPlanApproval_MovesToApproved_AndSetsApprovedByAndAt()
    {
        var action = Draft();
        action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.NewGuid());
        var supervisorId = Guid.NewGuid();
        var approvedAt = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

        action.ApprovePlan(supervisorId, approvedAt);

        Assert.Equal(CorrectiveActionStatus.Approved, action.Status);
        Assert.Equal(supervisorId, action.ApprovedBy);
        Assert.Equal(approvedAt, action.ApprovedAt);
    }

    [Fact]
    public void ApprovePlan_FromDraft_Throws_AndDoesNotMutate()
    {
        var action = Draft();

        Assert.Throws<DomainRuleViolationException>(() => action.ApprovePlan(Guid.NewGuid(), Now));

        Assert.Equal(CorrectiveActionStatus.Draft, action.Status);
        Assert.Null(action.ApprovedBy);
        Assert.Null(action.ApprovedAt);
    }

    [Fact]
    public void ApprovePlan_WhenAlreadyApproved_Throws_AndKeepsTheOriginalApprover()
    {
        var action = Draft();
        action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.NewGuid());
        var firstApprover = Guid.NewGuid();
        var firstApprovedAt = Now;
        action.ApprovePlan(firstApprover, firstApprovedAt);

        Assert.Throws<DomainRuleViolationException>(() => action.ApprovePlan(Guid.NewGuid(), Now.AddHours(1)));

        Assert.Equal(firstApprover, action.ApprovedBy);
        Assert.Equal(firstApprovedAt, action.ApprovedAt);
    }

    [Fact]
    public void ApprovePlan_RequiresANonEmptyApprovedBy_AndDoesNotMutate()
    {
        var action = Draft();
        action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => action.ApprovePlan(Guid.Empty, Now));

        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, action.Status);
        Assert.Null(action.ApprovedAt);
    }

    [Fact]
    public void ApprovePlan_RequiresAUtcTimestamp_AndDoesNotPartiallyAssignApprovedBy()
    {
        var action = Draft();
        action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => action.ApprovePlan(Guid.NewGuid(), DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Local)));

        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, action.Status);
        Assert.Null(action.ApprovedBy);
    }

    // ---------------- ScheduleRework (CA-API-003; `docs/13` §4.22) ----------------

    private static CorrectiveAction Approved()
    {
        var action = Draft();
        action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.NewGuid());
        action.ApprovePlan(Guid.NewGuid(), Now);
        return action;
    }

    [Fact]
    public void ScheduleRework_FromApproved_LinksTheServiceVisit_AndKeepsStatusApproved()
    {
        var action = Approved();
        var visitId = Guid.NewGuid();

        action.ScheduleRework(visitId);

        Assert.Equal(visitId, action.CorrectiveServiceVisitId);
        Assert.Equal(CorrectiveActionStatus.Approved, action.Status);
    }

    [Theory]
    [InlineData(CorrectiveActionStatus.Draft)]
    [InlineData(CorrectiveActionStatus.PendingPlanApproval)]
    public void ScheduleRework_WhenNotApproved_Throws_AndDoesNotMutate(CorrectiveActionStatus status)
    {
        var action = Draft();
        if (status == CorrectiveActionStatus.PendingPlanApproval)
        {
            action.SubmitPlan(Guid.NewGuid(), "Plan.", Guid.NewGuid());
        }

        Assert.Throws<DomainRuleViolationException>(() => action.ScheduleRework(Guid.NewGuid()));

        Assert.Equal(status, action.Status);
        Assert.Null(action.CorrectiveServiceVisitId);
    }

    [Fact]
    public void ScheduleRework_WhenAlreadyScheduled_Throws_AndKeepsTheFirstVisitLinked()
    {
        var action = Approved();
        var firstVisitId = Guid.NewGuid();
        action.ScheduleRework(firstVisitId);

        Assert.Throws<DomainRuleViolationException>(() => action.ScheduleRework(Guid.NewGuid()));

        Assert.Equal(firstVisitId, action.CorrectiveServiceVisitId);
    }

    [Fact]
    public void ScheduleRework_RequiresANonEmptyServiceVisitId_AndDoesNotMutate()
    {
        var action = Approved();

        Assert.Throws<ArgumentException>(() => action.ScheduleRework(Guid.Empty));

        Assert.Null(action.CorrectiveServiceVisitId);
    }
}
