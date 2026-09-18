using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.RepairRequests;

/// <summary>
/// S2-002 Convert invariants: ST-RR-008 APPROVED -> CONVERTED only (BR-03); no reason applies (absent from BR-04's
/// list); CONVERTED is terminal (no further transition from it exists in RepairRequestStatusTransitions).
/// </summary>
public class RepairRequestConvertDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void Convert_FromApproved_MovesToConverted()
    {
        var request = RequestIn(RepairRequestStatus.Approved);
        var requestNo = request.RequestNo;

        request.Convert();

        Assert.Equal(RepairRequestStatus.Converted, request.Status);
        Assert.Equal(requestNo, request.RequestNo);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public void Convert_FromAnyStateOtherThanApproved_IsDenied_AndChangesNothing(RepairRequestStatus status)
    {
        var request = RequestIn(status);

        Assert.Throws<DomainRuleViolationException>(request.Convert);

        Assert.Equal(status, request.Status);
    }

    [Fact]
    public void ConvertedRequest_IsTerminal()
    {
        var request = RequestIn(RepairRequestStatus.Approved);
        request.Convert();

        // No duplicate Convert, and no other transition exists out of CONVERTED.
        Assert.Throws<DomainRuleViolationException>(request.Convert);
        Assert.Throws<DomainRuleViolationException>(() => request.Cancel("Too late"));
        Assert.Equal(RepairRequestStatus.Converted, request.Status);
    }
}
