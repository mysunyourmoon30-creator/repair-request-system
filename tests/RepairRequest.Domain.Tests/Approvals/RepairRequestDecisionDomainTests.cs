using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.Approvals;

/// <summary>
/// S1-008 decision invariants: ST-RR-004 Approve and ST-RR-005 Reject from UNDER_REVIEW only (DEC-PRE-S1-008-03), the
/// required reject reason (RR-DD-001 RR-016 / APR-008), and final, non-removable approval-step decisions.
/// </summary>
public class RepairRequestDecisionDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc);

    private static RepairRequestAggregate UnderReviewRequest()
    {
        var owner = Guid.NewGuid();
        var request = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), owner);
        request.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", Now, Now.AddHours(2));
        request.Submit("RR-2026-000001", owner, Now, null);
        request.RouteForReview();
        return request;
    }

    private static RepairRequestAggregate RequestIn(RepairRequestStatus status)
    {
        var request = UnderReviewRequest();
        typeof(RepairRequestAggregate).GetProperty(nameof(RepairRequestAggregate.Status))!.SetValue(request, status);
        return request;
    }

    private static RepairRequestApproval AssignedStep() =>
        RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Now);

    // ---------------- Repair Request ----------------

    [Fact]
    public void Approve_MovesUnderReviewToApproved_AndKeepsSubmitData()
    {
        var request = UnderReviewRequest();

        request.Approve();

        Assert.Equal(RepairRequestStatus.Approved, request.Status);
        Assert.Equal("RR-2026-000001", request.RequestNo);
        Assert.Equal(Now, request.SubmittedAt);
        Assert.Null(request.RejectReason);
    }

    [Fact]
    public void Reject_MovesUnderReviewToRejected_WithTheReason()
    {
        var request = UnderReviewRequest();

        request.Reject("Not a repair: covered by the service contract");

        Assert.Equal(RepairRequestStatus.Rejected, request.Status);
        Assert.Equal("Not a repair: covered by the service contract", request.RejectReason);
        Assert.Equal("RR-2026-000001", request.RequestNo);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public void ApproveAndReject_AreOnlyAllowedFromUnderReview(RepairRequestStatus status)
    {
        var approved = RequestIn(status);
        var rejected = RequestIn(status);

        Assert.Throws<DomainRuleViolationException>(approved.Approve);
        Assert.Throws<DomainRuleViolationException>(() => rejected.Reject("A valid reason"));

        Assert.Equal(status, approved.Status);
        Assert.Equal(status, rejected.Status);
        Assert.Null(rejected.RejectReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reject_RequiresANonBlankReason_AndChangesNothingOtherwise(string reason)
    {
        var request = UnderReviewRequest();

        Assert.Throws<ArgumentException>(() => request.Reject(reason));
        Assert.Throws<ArgumentException>(() => request.Reject(new string('R', RepairRequestAggregate.ReasonMaxLength + 1)));

        Assert.Equal(RepairRequestStatus.UnderReview, request.Status);
        Assert.Null(request.RejectReason);
    }

    [Fact]
    public void Reject_AcceptsAReasonOfExactlyTheMaximumLength()
    {
        var request = UnderReviewRequest();
        var reason = new string('R', RepairRequestAggregate.ReasonMaxLength);

        request.Reject(reason);

        Assert.Equal(reason, request.RejectReason);
    }

    // ---------------- Approval step ----------------

    [Fact]
    public void ApprovalStep_Approve_RecordsTheDecisionAndTime()
    {
        var step = AssignedStep();

        step.Approve(Now.AddMinutes(5));

        Assert.Equal(ApprovalStatus.Approved, step.Status);
        Assert.Equal(Now.AddMinutes(5), step.DecidedAt);
        Assert.Null(step.DecisionReason);
        Assert.False(step.IsAssignedPending);
    }

    [Fact]
    public void ApprovalStep_Reject_RecordsTheDecisionReasonAndTime()
    {
        var step = AssignedStep();

        step.Reject("Out of warranty", Now.AddMinutes(5));

        Assert.Equal(ApprovalStatus.Rejected, step.Status);
        Assert.Equal("Out of warranty", step.DecisionReason);
        Assert.Equal(Now.AddMinutes(5), step.DecidedAt);
    }

    [Fact]
    public void ApprovalStep_Decision_IsFinal_AndADecidedStepIsNeverDiscardable()
    {
        var approved = AssignedStep();
        approved.Approve(Now);
        var rejected = AssignedStep();
        rejected.Reject("Out of warranty", Now);

        Assert.Throws<DomainRuleViolationException>(() => approved.Approve(Now));
        Assert.Throws<DomainRuleViolationException>(() => approved.Reject("Changed my mind", Now));
        Assert.Throws<DomainRuleViolationException>(() => rejected.Approve(Now));
        Assert.Throws<DomainRuleViolationException>(approved.EnsureDiscardableRoutingFailure);
        Assert.Throws<DomainRuleViolationException>(rejected.EnsureDiscardableRoutingFailure);
        Assert.Equal(ApprovalStatus.Approved, approved.Status);
        Assert.Equal(ApprovalStatus.Rejected, rejected.Status);
    }

    [Fact]
    public void ApprovalStep_WithoutAnAssignedApprover_CannotBeDecided()
    {
        var failure = RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, RoutingFailureCodes.ApproverNotFound);

        Assert.Throws<DomainRuleViolationException>(() => failure.Approve(Now));
        Assert.Throws<DomainRuleViolationException>(() => failure.Reject("Reason", Now));
        Assert.Equal(ApprovalStatus.Pending, failure.Status);
    }

    [Fact]
    public void ApprovalStep_Reject_RequiresAReason_AndUtcTime()
    {
        var step = AssignedStep();

        Assert.Throws<ArgumentException>(() => step.Reject(" ", Now));
        Assert.Throws<ArgumentException>(() => step.Approve(DateTime.SpecifyKind(Now, DateTimeKind.Local)));
        Assert.Equal(ApprovalStatus.Pending, step.Status);
    }
}
