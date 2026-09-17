using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.Approvals;

/// <summary>
/// S1-010 invariants: ST-RR-006 Return for Correction from UNDER_REVIEW only (DEC-PRE-S1-010-03) back to an editable DRAFT
/// with Request No and submit data kept; the returned approval step is a final decision with the required reason; Resubmit
/// keeps Request No, submitted_by and submitted_at (DEC-PRE-S1-010-02) and replaces the continuation reason only when a new
/// one applies (DEC-PRE-S1-010-04); approval cycles start at 1 (DEC-PRE-S1-010-01).
/// </summary>
public class RepairRequestReturnForCorrectionDomainTests
{
    private static readonly DateTime SubmittedAt = new(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc);

    private static RepairRequestAggregate UnderReviewRequest(string? continuationReason = null)
    {
        var owner = Guid.NewGuid();
        var request = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), owner);
        request.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", Now, Now.AddHours(2));
        request.Submit("RR-2026-000001", owner, SubmittedAt, continuationReason);
        request.RouteForReview();
        return request;
    }

    private static RepairRequestAggregate ReturnedRequest(string? continuationReason = null)
    {
        var request = UnderReviewRequest(continuationReason);
        request.ReturnForCorrection();
        return request;
    }

    private static RepairRequestApproval AssignedStep() =>
        RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, Guid.NewGuid(), Now);

    [Fact]
    public void ReturnForCorrection_FromUnderReview_MovesToAnEditableDraft_AndKeepsRequestNoAndSubmitData()
    {
        var request = UnderReviewRequest("Separate fault");
        var submittedBy = request.SubmittedBy;

        request.ReturnForCorrection();

        Assert.Equal(RepairRequestStatus.Draft, request.Status);
        Assert.True(request.IsReturnedDraft);
        Assert.Equal("RR-2026-000001", request.RequestNo);
        Assert.Equal(SubmittedAt, request.SubmittedAt);
        Assert.Equal(submittedBy, request.SubmittedBy);
        Assert.Equal("Separate fault", request.DuplicateContinuationReason);
        Assert.Null(request.RejectReason);

        request.EditDraft(request.SiteId, null, "ELECTRICAL", "HIGH", request.RequestContactId, "Pump leaking at the seal", Now, Now.AddHours(3));
        Assert.Equal("Pump leaking at the seal", request.Description);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public void ReturnForCorrection_FromAnyOtherState_IsDenied(RepairRequestStatus status)
    {
        var request = UnderReviewRequest();
        typeof(RepairRequestAggregate).GetProperty(nameof(RepairRequestAggregate.Status))!.SetValue(request, status);

        Assert.Throws<DomainRuleViolationException>(request.ReturnForCorrection);
        Assert.Equal(status, request.Status);
    }

    [Fact]
    public void ApprovalStep_ReturnForCorrection_RecordsTheTrimmedDecision_AndIsFinal()
    {
        var step = AssignedStep();

        step.ReturnForCorrection("Photo does not show the fault", Now);

        Assert.Equal(ApprovalStatus.ReturnedForCorrection, step.Status);
        Assert.Equal("Photo does not show the fault", step.DecisionReason);
        Assert.Equal(Now, step.DecidedAt);
        Assert.False(step.IsAssignedPending);
        Assert.Throws<DomainRuleViolationException>(() => step.Approve(Now));
        Assert.Throws<DomainRuleViolationException>(() => step.ReturnForCorrection("Again", Now));
        Assert.Throws<DomainRuleViolationException>(step.EnsureDiscardableRoutingFailure);
    }

    [Fact]
    public void ApprovalStep_ReturnForCorrection_RequiresAReasonWithinTheMaximum_AndAnAssignedPendingStep()
    {
        Assert.Throws<ArgumentException>(() => AssignedStep().ReturnForCorrection("   ", Now));
        Assert.Throws<ArgumentException>(() => AssignedStep().ReturnForCorrection(new string('R', RepairRequestApproval.DecisionReasonMaxLength + 1), Now));
        var exact = AssignedStep();
        exact.ReturnForCorrection(new string('R', RepairRequestApproval.DecisionReasonMaxLength), Now);
        Assert.Equal(RepairRequestApproval.DecisionReasonMaxLength, exact.DecisionReason!.Length);

        var failure = RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, RoutingFailureCodes.ApproverNotFound);
        Assert.Throws<DomainRuleViolationException>(() => failure.ReturnForCorrection("Reason", Now));
    }

    [Fact]
    public void ApprovalCycle_StartsAtOne_AndNeverBelow()
    {
        Assert.Equal(1, AssignedStep().ApprovalCycleNo);
        Assert.Equal(2, RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid(), 1, Guid.NewGuid(), Now).ApprovalCycleNo);
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), 1, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), 1, RoutingFailureCodes.ApproverNotFound));
    }

    [Fact]
    public void Resubmit_KeepsRequestNoSubmittedByAndSubmittedAt_AndKeepsTheStoredReasonWhenNoneApplies()
    {
        var request = ReturnedRequest("Separate fault");
        var submittedBy = request.SubmittedBy!.Value;

        request.Resubmit(request.CreatedBy, null);

        Assert.Equal(RepairRequestStatus.Submitted, request.Status);
        Assert.Equal("RR-2026-000001", request.RequestNo);
        Assert.Equal(SubmittedAt, request.SubmittedAt);
        Assert.Equal(submittedBy, request.SubmittedBy);
        Assert.Equal("Separate fault", request.DuplicateContinuationReason);
        Assert.False(request.IsReturnedDraft);
    }

    [Fact]
    public void Resubmit_WithAnAppliedReason_ReplacesTheStoredReason()
    {
        var request = ReturnedRequest("Separate fault");

        request.Resubmit(request.CreatedBy, "Second pump on the same line");

        Assert.Equal("Second pump on the same line", request.DuplicateContinuationReason);
    }

    [Fact]
    public void Resubmit_OfADraftNeverSubmitted_IsDenied_AndSubmitOfAReturnedDraftIsDenied()
    {
        var owner = Guid.NewGuid();
        var neverSubmitted = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), owner);
        neverSubmitted.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", Now, Now.AddHours(2));
        Assert.False(neverSubmitted.IsReturnedDraft);
        Assert.Throws<DomainRuleViolationException>(() => neverSubmitted.Resubmit(owner, null));
        Assert.Equal(RepairRequestStatus.Draft, neverSubmitted.Status);

        var returned = ReturnedRequest();
        Assert.Throws<DomainRuleViolationException>(() => returned.Submit("RR-2026-000002", returned.CreatedBy, Now, null));
        Assert.Equal("RR-2026-000001", returned.RequestNo);
    }

    [Fact]
    public void Resubmit_RequiresTheOwner_ADraft_AndTheSubmitRequiredFields()
    {
        var request = ReturnedRequest();
        Assert.Throws<DomainRuleViolationException>(() => request.Resubmit(Guid.NewGuid(), null));

        var submitted = UnderReviewRequest();
        Assert.Throws<DomainRuleViolationException>(() => submitted.Resubmit(submitted.CreatedBy, null));

        var incomplete = ReturnedRequest();
        incomplete.EditDraft(incomplete.SiteId, null, null, "HIGH", incomplete.RequestContactId, "Pump leaking", Now, Now.AddHours(2));
        Assert.Throws<DomainRuleViolationException>(() => incomplete.Resubmit(incomplete.CreatedBy, null));
        Assert.Equal(RepairRequestStatus.Draft, incomplete.Status);
    }
}
