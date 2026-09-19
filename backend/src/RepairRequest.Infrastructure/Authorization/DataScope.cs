using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Authorization;

/// <summary>
/// Tenant/site scope predicates translated into the same SQL statement as the caller's query.
/// Site membership is an EXISTS against the user_site_scope primary key (tenant_id, user_id, site_id);
/// no scope list is loaded into memory. Site status is not considered: deactivation blocks future
/// selection but does not hide history (RR-DBD-001 section 6).
/// </summary>
internal sealed class DataScope : IDataScope
{
    private readonly RepairRequestDbContext _db;

    public DataScope(RepairRequestDbContext db)
    {
        _db = db;
    }

    public IQueryable<Customer> Customers(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;
        var userId = user.UserId;

        var customers = _db.Customers.Where(customer => customer.TenantId == tenantId);
        if (user.HasTenantWideConfigurationScope)
        {
            return customers;
        }

        return customers.Where(customer => _db.UserSiteScopes.Any(scope =>
            scope.TenantId == tenantId
            && scope.UserId == userId
            && _db.Sites.Any(site => site.Id == scope.SiteId && site.CustomerId == customer.Id)));
    }

    public IQueryable<Site> Sites(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;
        var userId = user.UserId;

        var sites = _db.Sites.Where(site => site.TenantId == tenantId);
        if (user.HasTenantWideConfigurationScope)
        {
            return sites;
        }

        return sites.Where(site => _db.UserSiteScopes.Any(scope =>
            scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == site.Id));
    }

    public IQueryable<Site> BusinessSites(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;
        var userId = user.UserId;

        var sites = _db.Sites.Where(site => site.TenantId == tenantId);

        // Configuration-only roles (ADMINISTRATOR) never contribute business Site scope (S1-003 decision 2).
        if (!user.HasBusinessRole)
        {
            return sites.Where(_ => false);
        }

        return sites.Where(site => _db.UserSiteScopes.Any(scope =>
            scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == site.Id));
    }

    public IQueryable<Equipment> Equipment(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;
        var userId = user.UserId;

        var equipment = _db.Equipment.Where(item => item.TenantId == tenantId);
        if (user.HasTenantWideConfigurationScope)
        {
            return equipment;
        }

        return equipment.Where(item => _db.UserSiteScopes.Any(scope =>
            scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == item.SiteId));
    }

    public IQueryable<RepairRequestAggregate> RepairRequests(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;
        var userId = user.UserId;

        var requests = _db.RepairRequests.Where(request => request.TenantId == tenantId);

        // ADMINISTRATOR alone does not impersonate a business actor (RR-REQ-001 section 3).
        if (!user.HasBusinessRole)
        {
            return requests.Where(_ => false);
        }

        if (user.HasSiteWideRequestScope)
        {
            return user.IsRequester
                ? requests.Where(request =>
                    (request.SiteId != null && _db.UserSiteScopes.Any(scope =>
                        scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == request.SiteId))
                    || (request.SiteId == null && request.CreatedBy == userId))
                : requests.Where(request =>
                    request.SiteId != null && _db.UserSiteScopes.Any(scope =>
                        scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == request.SiteId));
        }

        // REQUESTER only: own data (RR-REQ-001 section 3), still limited to assigned Sites.
        return requests.Where(request =>
            request.CreatedBy == userId
            && (request.SiteId == null || _db.UserSiteScopes.Any(scope =>
                scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == request.SiteId)));
    }

    public IQueryable<RepairRequestAggregate> RoutingRecoveryRequests(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;

        var requests = _db.RepairRequests.Where(request => request.TenantId == tenantId);

        // DEC-PRE-S1-007R-10: routing recovery only, and only for tenant-wide configuration scope (ADMINISTRATOR).
        return user.HasTenantWideConfigurationScope ? requests : requests.Where(_ => false);
    }

    public IQueryable<WorkOrder> WorkOrders(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;
        var userId = user.UserId;

        var workOrders = _db.WorkOrders.Where(workOrder => workOrder.TenantId == tenantId);

        // TECHNICIAN and ADMINISTRATOR hold no Work Order read scope (DEC-S2-001-03).
        if (!user.HasWorkOrderReadScope)
        {
            return workOrders.Where(_ => false);
        }

        if (user.HasSiteWideWorkOrderScope)
        {
            return workOrders.Where(workOrder => _db.RepairRequests.Any(request =>
                request.Id == workOrder.RepairRequestId
                && request.SiteId != null
                && _db.UserSiteScopes.Any(scope =>
                    scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == request.SiteId)));
        }

        // REQUESTER only: Work Orders of Repair Requests the caller created (DEC-S2-001-03).
        return workOrders.Where(workOrder => _db.RepairRequests.Any(request =>
            request.Id == workOrder.RepairRequestId && request.CreatedBy == userId));
    }

    public IQueryable<ServiceVisit> AssignedServiceVisits(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;
        var userId = user.UserId;

        var visits = _db.ServiceVisits.Where(visit => visit.TenantId == tenantId);

        // Only TECHNICIAN holds this scope (S3-001); every other role sees none.
        if (!user.IsTechnician)
        {
            return visits.Where(_ => false);
        }

        return visits.Where(visit =>
            visit.AssignedTechnicianId == userId
            && _db.WorkOrders.Any(workOrder => workOrder.Id == visit.WorkOrderId
                && _db.RepairRequests.Any(request => request.Id == workOrder.RepairRequestId
                    && request.SiteId != null
                    && _db.UserSiteScopes.Any(scope =>
                        scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == request.SiteId))));
    }

    public Task<bool> IsSiteInScopeAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        BusinessSites(user).AnyAsync(site => site.Id == siteId, cancellationToken);
}
