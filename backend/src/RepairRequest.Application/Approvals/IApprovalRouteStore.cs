using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Persistence port for approval route configuration (DEC-PRE-S1-007R-04). Every caller-facing lookup is limited to the
/// caller's tenant and returns nothing unless the caller holds tenant-wide configuration scope (ADMINISTRATOR), so other
/// tenants' routes are indistinguishable from nonexistent ones.
/// </summary>
public interface IApprovalRouteStore
{
    Task<PagedResult<ApprovalRouteDto>> ListAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken);

    Task<ApprovalRouteDto?> GetAsync(CurrentUser user, Guid approvalRouteId, CancellationToken cancellationToken);

    /// <summary>Tracked, tenant-scoped load for a command.</summary>
    Task<ApprovalRoute?> FindAsync(CurrentUser user, Guid approvalRouteId, CancellationToken cancellationToken);

    Task<ApprovalRouteStep?> FindFirstStepAsync(Guid tenantId, Guid approvalRouteId, CancellationToken cancellationToken);

    Task<ApprovalRouteReferences> GetReferencesAsync(
        CurrentUser user,
        string requestCategoryCode,
        Guid? siteId,
        Guid? approverUserId,
        CancellationToken cancellationToken);

    /// <summary>An ACTIVE route other than <paramref name="excludeRouteId"/> exists for the same tenant + Category + Site (or default).</summary>
    Task<bool> OtherActiveRouteExistsAsync(
        Guid tenantId,
        string requestCategoryCode,
        Guid? siteId,
        Guid excludeRouteId,
        CancellationToken cancellationToken);

    /// <summary>Adds the route to the unit of work; its identifier is assigned immediately.</summary>
    void Add(ApprovalRoute route);

    void AddStep(ApprovalRouteStep step);

    void AddAudit(AuditHistory audit);

    Task<ApprovalRouteSaveOutcome> SaveChangesAsync(ApprovalRoute route, byte[]? expectedRowVersion, CancellationToken cancellationToken);
}
