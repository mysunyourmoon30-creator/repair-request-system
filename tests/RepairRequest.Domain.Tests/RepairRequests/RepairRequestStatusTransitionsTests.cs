using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Domain.Tests.RepairRequests;

/// <summary>Verifies the encoded matrix against RR-STS-001 section 2 (ST-RR-002..ST-RR-008).</summary>
public class RepairRequestStatusTransitionsTests
{
    [Fact]
    public void CanonicalStatuses_MatchRrSts001()
    {
        Assert.Equal(
            ["Draft", "Submitted", "UnderReview", "Approved", "Rejected", "Cancelled", "Converted"],
            Enum.GetNames<RepairRequestStatus>());
    }

    [Theory]
    [InlineData("ST-RR-002", RepairRequestStatus.Draft, RepairRequestStatus.Submitted)]
    [InlineData("ST-RR-003", RepairRequestStatus.Submitted, RepairRequestStatus.UnderReview)]
    [InlineData("ST-RR-004", RepairRequestStatus.Submitted, RepairRequestStatus.Approved)]
    [InlineData("ST-RR-004", RepairRequestStatus.UnderReview, RepairRequestStatus.Approved)]
    [InlineData("ST-RR-005", RepairRequestStatus.Submitted, RepairRequestStatus.Rejected)]
    [InlineData("ST-RR-005", RepairRequestStatus.UnderReview, RepairRequestStatus.Rejected)]
    [InlineData("ST-RR-006", RepairRequestStatus.Submitted, RepairRequestStatus.Draft)]
    [InlineData("ST-RR-006", RepairRequestStatus.UnderReview, RepairRequestStatus.Draft)]
    [InlineData("ST-RR-007", RepairRequestStatus.Draft, RepairRequestStatus.Cancelled)]
    [InlineData("ST-RR-007", RepairRequestStatus.Submitted, RepairRequestStatus.Cancelled)]
    [InlineData("ST-RR-007", RepairRequestStatus.UnderReview, RepairRequestStatus.Cancelled)]
    [InlineData("ST-RR-007", RepairRequestStatus.Approved, RepairRequestStatus.Cancelled)]
    [InlineData("ST-RR-008", RepairRequestStatus.Approved, RepairRequestStatus.Converted)]
    public void AllowedTransition_IsPresentWithCanonicalId(string transitionId, RepairRequestStatus from, RepairRequestStatus to)
    {
        Assert.True(RepairRequestStatusTransitions.IsAllowed(from, to));
        Assert.Contains(
            RepairRequestStatusTransitions.All,
            transition => transition.TransitionId == transitionId && transition.From == from && transition.To == to);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheBaselineTransitions()
    {
        Assert.Equal(13, RepairRequestStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Draft, RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Draft, RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Draft, RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Draft, RepairRequestStatus.Converted)]
    [InlineData(RepairRequestStatus.Submitted, RepairRequestStatus.Converted)]
    [InlineData(RepairRequestStatus.UnderReview, RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.UnderReview, RepairRequestStatus.Converted)]
    [InlineData(RepairRequestStatus.Approved, RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Approved, RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Approved, RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Rejected, RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Cancelled, RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Converted, RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted, RepairRequestStatus.Approved)]
    public void TransitionOutsideMatrix_IsNotAllowed(RepairRequestStatus from, RepairRequestStatus to)
    {
        Assert.False(RepairRequestStatusTransitions.IsAllowed(from, to));
    }

    [Fact]
    public void Converted_IsReachableOnlyFromApproved()
    {
        var sources = RepairRequestStatusTransitions.All
            .Where(transition => transition.To == RepairRequestStatus.Converted)
            .Select(transition => transition.From);

        Assert.Equal([RepairRequestStatus.Approved], sources);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public void FinalRequestStates_HaveNoOutgoingRequestTransition(RepairRequestStatus status)
    {
        Assert.Empty(RepairRequestStatusTransitions.From(status));
    }

    [Fact]
    public void Matrix_HasNoSelfTransitions()
    {
        Assert.DoesNotContain(RepairRequestStatusTransitions.All, transition => transition.From == transition.To);
    }
}
