using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.RepairRequests;

/// <summary>
/// S1-009 Cancel invariants: ST-RR-007 DRAFT / SUBMITTED / UNDER_REVIEW / APPROVED -> CANCELLED with the required reason
/// (RR-DD-001 RR-015); REJECTED, CANCELLED and CONVERTED are never cancelled and CANCELLED is terminal (UC-RR-004).
/// </summary>
public class RepairRequestCancelDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc);

    private static RepairRequestAggregate RequestIn(RepairRequestStatus status)
    {
        var owner = Guid.NewGuid();
        var request = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), owner);
        if (status == RepairRequestStatus.Draft)
        {
            return request;
        }

        request.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", Now, Now.AddHours(2));
        request.Submit("RR-2026-000001", owner, Now, null);
        typeof(RepairRequestAggregate).GetProperty(nameof(RepairRequestAggregate.Status))!.SetValue(request, status);
        return request;
    }

    [Theory]
    [InlineData(RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Approved)]
    public void Cancel_FromAnAllowedState_MovesToCancelled_WithTheReason_AndKeepsSubmitData(RepairRequestStatus status)
    {
        var request = RequestIn(status);
        var requestNo = request.RequestNo;
        var submittedAt = request.SubmittedAt;

        request.Cancel("No longer needed");

        Assert.Equal(RepairRequestStatus.Cancelled, request.Status);
        Assert.Equal("No longer needed", request.CancelReason);
        Assert.Equal(requestNo, request.RequestNo);
        Assert.Equal(submittedAt, request.SubmittedAt);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public void Cancel_FromARejectedCancelledOrConvertedRequest_IsDenied_AndChangesNothing(RepairRequestStatus status)
    {
        var request = RequestIn(status);

        Assert.Throws<DomainRuleViolationException>(() => request.Cancel("No longer needed"));

        Assert.Equal(status, request.Status);
        Assert.Null(request.CancelReason);
    }

    [Fact]
    public void CancelledRequest_IsTerminal()
    {
        var request = RequestIn(RepairRequestStatus.UnderReview);
        request.Cancel("No longer needed");

        Assert.Throws<DomainRuleViolationException>(() => request.Cancel("Again"));
        Assert.Throws<DomainRuleViolationException>(request.Approve);
        Assert.Throws<DomainRuleViolationException>(() => request.Reject("Too late"));
        Assert.Equal(RepairRequestStatus.Cancelled, request.Status);
        Assert.Equal("No longer needed", request.CancelReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Cancel_RequiresANonBlankReason_WithinTheMaximumLength(string reason)
    {
        var request = RequestIn(RepairRequestStatus.Submitted);

        Assert.Throws<ArgumentException>(() => request.Cancel(reason));
        Assert.Throws<ArgumentException>(() => request.Cancel(new string('R', RepairRequestAggregate.ReasonMaxLength + 1)));

        Assert.Equal(RepairRequestStatus.Submitted, request.Status);
        Assert.Null(request.CancelReason);
    }

    [Fact]
    public void Cancel_AcceptsAReasonOfExactlyTheMaximumLength()
    {
        var request = RequestIn(RepairRequestStatus.Draft);
        var reason = new string('R', RepairRequestAggregate.ReasonMaxLength);

        request.Cancel(reason);

        Assert.Equal(reason, request.CancelReason);
    }
}
