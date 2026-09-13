using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// EF Core implementation of the Draft port. Every read starts from <see cref="IDataScope"/>, so tenant/site/ownership
/// scope is part of the same SQL statement: detail and the owned load are one bounded primary-key query each, and the
/// Site/Equipment selection check is one query (Site primary key + user_site_scope EXISTS + Equipment alternate key
/// (site_id, equipment_id)). No navigation graphs are loaded and no index is added.
/// </summary>
internal sealed class RepairRequestDraftStore : IRepairRequestDraftStore
{
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
                request.Description,
                request.PreferredStartAt,
                request.PreferredEndAt,
                request.CreatedBy,
                request.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<RepairRequestAggregate?> FindOwnAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        var userId = user.UserId;
        return _scope.RepairRequests(user)
            .SingleOrDefaultAsync(request => request.Id == repairRequestId && request.CreatedBy == userId, cancellationToken);
    }

    // Business Site scope, never the master-data configuration scope: ADMINISTRATOR does not widen the Sites a
    // Requester may select (S1-003 decision 2).
    public Task<DraftSiteSelection?> GetSiteSelectionAsync(CurrentUser user, Guid siteId, Guid? equipmentId, CancellationToken cancellationToken) =>
        _scope.BusinessSites(user)
            .Where(site => site.Id == siteId)
            .Select(site => new DraftSiteSelection(
                site.Status,
                _db.Equipment
                    .Where(item => item.SiteId == site.Id && item.Id == equipmentId && item.TenantId == site.TenantId)
                    .Select(item => (MasterDataStatus?)item.Status)
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(cancellationToken);

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
