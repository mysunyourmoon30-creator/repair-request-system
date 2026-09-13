using RepairRequest.Domain.MasterData;
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

    /// <summary>Assigned Sites of the caller's tenant (all tenant Sites for ADMINISTRATOR). Inactive Sites remain visible.</summary>
    IQueryable<Site> Sites(CurrentUser user);

    /// <summary>Equipment at the caller's permitted Sites.</summary>
    IQueryable<Equipment> Equipment(CurrentUser user);

    /// <summary>
    /// Repair Requests visible to the caller's business roles: every request at assigned Sites for
    /// site-wide business roles; only own requests (including own Site-less drafts) for REQUESTER.
    /// ADMINISTRATOR alone sees none.
    /// </summary>
    IQueryable<RepairRequestAggregate> RepairRequests(CurrentUser user);

    /// <summary>Validates a client-supplied Site id against the caller's scope in a single query.</summary>
    Task<bool> IsSiteInScopeAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken);
}
