using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.MasterData;

/// <summary>
/// Equipment master (DEC-PS1-003). Belongs to a Site in the same Tenant;
/// equipment_code is unique within the Site (DEC-PS1-015). The owning Site
/// never changes after creation (S1-004 decision D3).
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
        EquipmentCode = ValidCode(equipmentCode, nameof(equipmentCode));
    }

    public Guid SiteId { get; private set; }

    public string EquipmentCode { get; private set; }

    /// <summary>Uniqueness within the Site is checked by the Application layer and the database.</summary>
    public void ChangeCode(string equipmentCode) =>
        EquipmentCode = ValidCode(equipmentCode, nameof(equipmentCode));
}
