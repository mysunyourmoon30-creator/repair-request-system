using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// EF Core implementation of the Draft port. Every read starts from <see cref="IDataScope"/>, so tenant/site/ownership
/// scope is part of the same SQL statement: detail and the owned load are one bounded primary-key query each, and the
/// selection check is one query of key seeks (business Site scope, Equipment alternate key, lookup primary keys and the
/// contact's user/site-scope/role keys). No navigation graphs are loaded.
/// </summary>
internal sealed class RepairRequestDraftStore : IRepairRequestDraftStore
{
    // Contact eligibility requires business Site scope (S1-003 decision 2; DEC-PRE-S1-007-04): ADMINISTRATOR alone never qualifies.
    // The ACTIVE-user check is deferred (DEC-PRE-S1-007-04, REQ-FU-USR-001): there is no user status, and Identity lockout is not one.
    private static readonly string[] BusinessRoleCodes = RoleCodes.All.Where(code => code != RoleCodes.Administrator).ToArray();

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public RepairRequestDraftStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public Task<RepairRequestDraftDto?> GetAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        _scope.RepairRequests(user)
            .AsNoTracking()
            .Where(request => request.Id == repairRequestId)
            .Select(request => new RepairRequestDraftDto(
                request.Id,
                request.Status,
                request.RequestNo,
                request.SiteId,
                request.EquipmentId,
                request.RequestCategoryCode,
                request.PriorityCode,
                request.RequestContactId,
                request.Description,
                request.PreferredStartAt,
                request.PreferredEndAt,
                request.CreatedBy,
                request.SubmittedAt,
                request.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<RepairRequestDraftDto>> ListAsync(CurrentUser user, RepairRequestListQuery query, CancellationToken cancellationToken)
    {
        var source = _scope.RepairRequests(user).AsNoTracking();

        if (query.Status is { } status)
        {
            source = source.Where(request => request.Status == status);
        }

        var paging = query.Paging;
        var totalCount = await source.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;

        IReadOnlyList<RepairRequestDraftDto> items = skip >= totalCount
            ? []
            : await source
                // Newest-submitted-first: RepairRequest has no CreatedAt; every listed status has been submitted.
                .OrderByDescending(request => request.SubmittedAt)
                .ThenByDescending(request => request.Id)
                .Skip((int)skip)
                .Take(paging.PageSize)
                .Select(request => new RepairRequestDraftDto(
                    request.Id,
                    request.Status,
                    request.RequestNo,
                    request.SiteId,
                    request.EquipmentId,
                    request.RequestCategoryCode,
                    request.PriorityCode,
                    request.RequestContactId,
                    request.Description,
                    request.PreferredStartAt,
                    request.PreferredEndAt,
                    request.CreatedBy,
                    request.SubmittedAt,
                    request.RowVersion))
                .ToListAsync(cancellationToken);

        return new PagedResult<RepairRequestDraftDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    public Task<RepairRequestAggregate?> FindOwnAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        var userId = user.UserId;
        return _scope.RepairRequests(user)
            .SingleOrDefaultAsync(request => request.Id == repairRequestId && request.CreatedBy == userId, cancellationToken);
    }

    // Business Site scope, never the master-data configuration scope: ADMINISTRATOR does not widen the Sites a
    // Requester may select (S1-003 decision 2). Rooted on the caller's own user row so every check is one statement.
    public async Task<DraftSelection> GetDraftSelectionAsync(CurrentUser user, DraftSelectionQuery query, CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId;
        var userId = user.UserId;
        var siteId = query.SiteId;
        var equipmentId = query.EquipmentId;
        var categoryCode = query.RequestCategoryCode;
        var priorityCode = query.PriorityCode;
        var contactId = query.RequestContactId;
        var businessSites = _scope.BusinessSites(user);

        var row = await _db.Users
            .AsNoTracking()
            .Where(caller => caller.Id == userId && caller.TenantId == tenantId)
            .Select(caller => new
            {
                SiteStatus = businessSites
                    .Where(site => site.Id == siteId)
                    .Select(site => (MasterDataStatus?)site.Status)
                    .FirstOrDefault(),
                EquipmentStatus = _db.Equipment
                    .Where(item => item.TenantId == tenantId && item.SiteId == siteId && item.Id == equipmentId)
                    .Select(item => (MasterDataStatus?)item.Status)
                    .FirstOrDefault(),
                CategoryCode = _db.RequestCategories
                    .Where(category => category.TenantId == tenantId && category.Code == categoryCode)
                    .Select(category => category.Code)
                    .FirstOrDefault(),
                CategoryStatus = _db.RequestCategories
                    .Where(category => category.TenantId == tenantId && category.Code == categoryCode)
                    .Select(category => (MasterDataStatus?)category.Status)
                    .FirstOrDefault(),
                PriorityCode = _db.RequestPriorities
                    .Where(priority => priority.TenantId == tenantId && priority.Code == priorityCode)
                    .Select(priority => priority.Code)
                    .FirstOrDefault(),
                PriorityStatus = _db.RequestPriorities
                    .Where(priority => priority.TenantId == tenantId && priority.Code == priorityCode)
                    .Select(priority => (MasterDataStatus?)priority.Status)
                    .FirstOrDefault(),
                ContactEligible = _db.Users.Any(contact =>
                    contact.TenantId == tenantId
                    && contact.Id == contactId
                    && _db.UserSiteScopes.Any(scope => scope.TenantId == tenantId && scope.UserId == contact.Id && scope.SiteId == siteId)
                    && _db.UserRoles.Any(userRole =>
                        userRole.UserId == contact.Id
                        && _db.Roles.Any(role => role.Id == userRole.RoleId && BusinessRoleCodes.Contains(role.Name!))))
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return DraftSelection.Nothing;
        }

        return new DraftSelection(
            row.SiteStatus,
            row.EquipmentStatus,
            row.CategoryCode is null ? null : new LookupSelection(row.CategoryCode, row.CategoryStatus!.Value),
            row.PriorityCode is null ? null : new LookupSelection(row.PriorityCode, row.PriorityStatus!.Value),
            row.ContactEligible);
    }

    public void Add(RepairRequestAggregate repairRequest) => _db.RepairRequests.Add(repairRequest);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<RepairRequestSaveOutcome> SaveChangesAsync(
        RepairRequestAggregate repairRequest,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        if (expectedRowVersion is not null)
        {
            // The UPDATE applies only WHERE row_version = the client's If-Match token.
            _db.Entry(repairRequest).Property(request => request.RowVersion).OriginalValue = expectedRowVersion;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return RepairRequestSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return RepairRequestSaveOutcome.ConcurrencyConflict;
        }
    }
}
