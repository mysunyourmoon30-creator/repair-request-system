using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.MasterData;

/// <summary>
/// Site master (DEC-PS1-002). Belongs to a Customer in the same Tenant;
/// site_code is unique within the Customer (DEC-PS1-015).
/// </summary>
public sealed class Site : MasterDataEntity
{
    private Site()
    {
        SiteCode = null!;
    }

    public Site(Guid tenantId, Guid customerId, string siteCode)
        : base(tenantId)
    {
        CustomerId = DomainGuard.NotEmpty(customerId, nameof(customerId));
        SiteCode = DomainGuard.RequiredText(siteCode, CodeMaxLength, nameof(siteCode));
    }

    public Guid CustomerId { get; private set; }

    public string SiteCode { get; private set; }
}
