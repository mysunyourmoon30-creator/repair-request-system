using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.MasterData;

/// <summary>
/// Equipment master (DEC-PS1-003). Belongs to a Site in the same Tenant;
/// equipment_code is unique within the Site (DEC-PS1-015).
/// </summary>
public sealed class Equipment : MasterDataEntity
{
    private Equipment()
    {
        EquipmentCode = null!;
    }

    public Equipment(Guid tenantId, Guid siteId, string equipmentCode)
        : base(tenantId)
    {
        SiteId = DomainGuard.NotEmpty(siteId, nameof(siteId));
        EquipmentCode = DomainGuard.RequiredText(equipmentCode, CodeMaxLength, nameof(equipmentCode));
    }

    public Guid SiteId { get; private set; }

    public string EquipmentCode { get; private set; }
}
