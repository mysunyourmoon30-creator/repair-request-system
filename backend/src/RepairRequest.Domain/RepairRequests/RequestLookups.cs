using RepairRequest.Domain.Common;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Domain.RepairRequests;

/// <summary>
/// Tenant-scoped Category lookup (RR-DD-001 RR-008 "Active Category"; DEC-PRE-S1-007-01). Seeded reference data with no
/// management commands in Sprint 1; deactivation blocks future selection but never erases history (RR-DBD-001 section 6).
/// No application rule depends on a specific code.
/// </summary>
public sealed class RequestCategory
{
    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 100;

    private RequestCategory()
    {
        Code = null!;
        Name = null!;
    }

    public RequestCategory(Guid tenantId, string code, string name, MasterDataStatus status = MasterDataStatus.Active)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        Code = DomainGuard.RequiredText(code, CodeMaxLength, nameof(code));
        Name = DomainGuard.RequiredText(name, NameMaxLength, nameof(name));
        Status = status;
    }

    public Guid TenantId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public MasterDataStatus Status { get; private set; }
}

/// <summary>
/// Tenant-scoped Priority lookup (RR-DD-001 RR-010 "Active Priority"; DEC-PRE-S1-007-02). Selected by the Requester; it has
/// no SLA effect (BR-12). Seeded reference data with no management commands in Sprint 1.
/// </summary>
public sealed class RequestPriority
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 100;

    private RequestPriority()
    {
        Code = null!;
        Name = null!;
    }

    public RequestPriority(Guid tenantId, string code, string name, MasterDataStatus status = MasterDataStatus.Active)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        Code = DomainGuard.RequiredText(code, CodeMaxLength, nameof(code));
        Name = DomainGuard.RequiredText(name, NameMaxLength, nameof(name));
        Status = status;
    }

    public Guid TenantId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public MasterDataStatus Status { get; private set; }
}
