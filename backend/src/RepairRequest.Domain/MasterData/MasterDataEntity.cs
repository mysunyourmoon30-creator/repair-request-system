using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.MasterData;

/// <summary>
/// Common shape of the Sprint 1 master entities (Customer, Site, Equipment).
/// Only fields fixed by RR-DEC-001 are modelled: tenant scope, status,
/// deactivate reason and the concurrency token. Descriptive fields (names etc.)
/// are not yet defined by the baseline and are intentionally absent.
/// </summary>
public abstract class MasterDataEntity
{
    /// <summary>Follows the baseline code convention (e.g. request_category_code varchar(30)).</summary>
    public const int CodeMaxLength = 30;

    /// <summary>Follows the baseline reason convention (e.g. cancel_reason nvarchar(1000)).</summary>
    public const int DeactivateReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    protected MasterDataEntity()
    {
    }

    protected MasterDataEntity(Guid tenantId)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        Status = MasterDataStatus.Active;
    }

    /// <summary>Assigned on insert (sequential GUID, RR-DBD-001 section 1).</summary>
    public Guid Id { get; private set; }

    /// <summary>Security boundary; server-derived and immutable (D-11, DEC-PS1-013).</summary>
    public Guid TenantId { get; private set; }

    public MasterDataStatus Status { get; private set; }

    public string? DeactivateReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// ACTIVE -> INACTIVE with a mandatory reason (DEC-PS1-014). The
    /// "no active child" dependency guard (DEC-PS1-013) is an Application-layer
    /// query-time check and is not evaluated here.
    /// </summary>
    public void Deactivate(string reason)
    {
        if (Status == MasterDataStatus.Inactive)
        {
            throw new DomainRuleViolationException($"{GetType().Name} is already inactive.");
        }

        DeactivateReason = DomainGuard.RequiredText(reason, DeactivateReasonMaxLength, nameof(reason));
        Status = MasterDataStatus.Inactive;
    }
}
