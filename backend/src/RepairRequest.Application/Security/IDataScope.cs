using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.WorkOrders;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Security;

/// <summary>
/// Tenant/site data scope as composable database predicates (S1-003 decisions 1, 2 and 4).
/// Every query returned here is already restricted to the caller's own tenant and permitted Sites,
/// so a record lookup through it yields nothing for out-of-scope and nonexistent ids alike.
/// Callers compose further filters, projections and paging on top; scope is never loaded into memory.
/// </summary>
public interface IDataScope
{
    /// <summary>Customers of the caller's tenant with at least one assigned Site (all tenant Customers for ADMINISTRATOR).</summary>
    IQueryable<Customer> Customers(CurrentUser user);

    /// <summary>
    /// Master-data read/configuration Site scope: assigned Sites of the caller's tenant (all tenant Sites for
    /// ADMINISTRATOR). Inactive Sites remain visible. Never use this for Repair Request business operations; use
    /// <see cref="BusinessSites"/>.
    /// </summary>
    IQueryable<Site> Sites(CurrentUser user);

    /// <summary>
    /// Business Site scope for Repair Request operations: Sites of the caller's tenant assigned to the caller, and only
    /// when the caller holds a business role. Configuration-only roles contribute nothing, so ADMINISTRATOR never widens
    /// it (S1-003 decisions 1 and 2). Inactive Sites are included; active-master rules are the use case's concern.
    /// </summary>
    IQueryable<Site> BusinessSites(CurrentUser user);

    /// <summary>Equipment at the caller's permitted Sites.</summary>
    IQueryable<Equipment> Equipment(CurrentUser user);

    /// <summary>
    /// Repair Requests visible to the caller's business roles: every request at assigned Sites for
    /// site-wide business roles; only own requests (including own Site-less drafts) for REQUESTER.
    /// ADMINISTRATOR alone sees none.
    /// </summary>
    IQueryable<RepairRequestAggregate> RepairRequests(CurrentUser user);

    /// <summary>
    /// Narrow routing-operations exception (DEC-PRE-S1-007R-10): every Repair Request of the caller's tenant, but only for a
    /// caller with tenant-wide configuration scope (ADMINISTRATOR); empty for everyone else. Use only for routing recovery
    /// (routing-issue list and Retry Routing) with routing metadata projections. It is never a business read scope and never
    /// grants detail, Approve or Reject.
    /// </summary>
    IQueryable<RepairRequestAggregate> RoutingRecoveryRequests(CurrentUser user);

    /// <summary>
    /// Work Orders visible to the caller (S2-001 / DEC-S2-001-03), correlated through the originating Repair
    /// Request's Site since Work Order carries no Site of its own: every Work Order at assigned Sites for
    /// site-wide Work Order roles; only Work Orders linked to Repair Requests the caller created for REQUESTER.
    /// TECHNICIAN and ADMINISTRATOR see none.
    /// </summary>
    IQueryable<WorkOrder> WorkOrders(CurrentUser user);

    /// <summary>
    /// Service Visits assigned to a Technician caller ("My Visits", S3-001): every Visit whose
    /// <c>AssignedTechnicianId</c> is the caller's own id, correlated through the Visit's Work Order and Repair
    /// Request to confirm the caller's Site scope is still current — a defense-in-depth check beyond the
    /// assignment itself, which was only validated once at Schedule/Reassign time and could since have been
    /// revoked. Every non-Technician caller sees none; this is not a general Work Order/Visit read scope
    /// (contrast <see cref="WorkOrders"/>, which deliberately excludes TECHNICIAN per DEC-S2-001-03). "Primary
    /// team" plays no part here — no Team master-data entity exists (Portfolio Project Owner directive,
    /// confirmed again pre-S3-001).
    /// </summary>
    IQueryable<ServiceVisit> AssignedServiceVisits(CurrentUser user);

    /// <summary>
    /// Work Sessions owned by a Technician caller (S3-002): <c>technician_id</c> is the caller's own id, and the
    /// session's Visit is still assigned to the caller within their current Site scope (the same re-check as
    /// <see cref="AssignedServiceVisits"/>, applied at Pause time too). Every non-Technician caller sees none;
    /// another technician's, cross-tenant and nonexistent sessions are indistinguishable (non-leaking 404).
    /// </summary>
    IQueryable<WorkSession> OwnWorkSessions(CurrentUser user);

    /// <summary>Validates a client-supplied Site id against the caller's <see cref="BusinessSites"/> scope in a single query.</summary>
    Task<bool> IsSiteInScopeAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken);
}
