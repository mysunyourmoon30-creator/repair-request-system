using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.RepairRequests;

public class RepairRequestTests
{
    [Fact]
    public void CreateDraft_StartsInDraftWithServerDerivedScope()
    {
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        var request = RepairRequestAggregate.CreateDraft(tenantId, requesterId);

        Assert.Equal(RepairRequestStatus.Draft, request.Status);
        Assert.Equal(tenantId, request.TenantId);
        Assert.Equal(requesterId, request.CreatedBy);
    }

    [Fact]
    public void CreateDraft_HasNoSubmitData()
    {
        var request = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(request.RequestNo);
        Assert.Null(request.SubmittedBy);
        Assert.Null(request.SubmittedAt);
    }

    [Fact]
    public void CreateDraft_WithoutTenant_Throws()
    {
        Assert.Throws<ArgumentException>(() => RepairRequestAggregate.CreateDraft(Guid.Empty, Guid.NewGuid()));
    }

    [Fact]
    public void CreateDraft_WithoutRequester_Throws()
    {
        Assert.Throws<ArgumentException>(() => RepairRequestAggregate.CreateDraft(Guid.NewGuid(), Guid.Empty));
    }
}
