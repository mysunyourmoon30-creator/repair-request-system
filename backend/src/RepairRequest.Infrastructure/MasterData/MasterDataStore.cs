using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.MasterData;

/// <summary>
/// EF Core implementation of the master-data port. Caller-facing reads and tracked loads always start from
/// <see cref="IDataScope"/>, so tenant/site scope is part of the same SQL statement. Lists are projected to DTOs
/// with AsNoTracking and paged server-side after scope is applied (RR-API-001 section 9; RR-ARCH-001 section 9).
/// Sort and uniqueness/guard lookups follow the existing scoped unique indexes; no index is added here.
/// </summary>
internal sealed class MasterDataStore : IMasterDataStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public MasterDataStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public Task<PagedResult<CustomerDto>> ListCustomersAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken) =>
        PageAsync(
            _scope.Customers(user).AsNoTracking(),
            query,
            customers => customers
                .OrderBy(customer => customer.CustomerCode)
                .ThenBy(customer => customer.Id)
                .Select(customer => new CustomerDto(customer.Id, customer.CustomerCode, customer.Status, customer.DeactivateReason, customer.RowVersion)),
            cancellationToken);

    public Task<CustomerDto?> GetCustomerAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken) =>
        _scope.Customers(user)
            .AsNoTracking()
            .Where(customer => customer.Id == customerId)
            .Select(customer => new CustomerDto(customer.Id, customer.CustomerCode, customer.Status, customer.DeactivateReason, customer.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<SiteDto>?> ListSitesAsync(CurrentUser user, Guid customerId, MasterDataListQuery query, CancellationToken cancellationToken)
    {
        if (!await _scope.Customers(user).AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return null;
        }

        return await PageAsync(
            _scope.Sites(user).AsNoTracking().Where(site => site.CustomerId == customerId),
            query,
            sites => sites
                .OrderBy(site => site.SiteCode)
                .ThenBy(site => site.Id)
                .Select(site => new SiteDto(site.Id, site.CustomerId, site.SiteCode, site.Status, site.DeactivateReason, site.RowVersion)),
            cancellationToken);
    }

    public Task<SiteDto?> GetSiteAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        _scope.Sites(user)
            .AsNoTracking()
            .Where(site => site.Id == siteId)
            .Select(site => new SiteDto(site.Id, site.CustomerId, site.SiteCode, site.Status, site.DeactivateReason, site.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<EquipmentDto>?> ListEquipmentAsync(CurrentUser user, Guid siteId, MasterDataListQuery query, CancellationToken cancellationToken)
    {
        if (!await _scope.Sites(user).AnyAsync(site => site.Id == siteId, cancellationToken))
        {
            return null;
        }

        return await PageAsync(
            _scope.Equipment(user).AsNoTracking().Where(equipment => equipment.SiteId == siteId),
            query,
            equipment => equipment
                .OrderBy(item => item.EquipmentCode)
                .ThenBy(item => item.Id)
                .Select(item => new EquipmentDto(item.Id, item.SiteId, item.EquipmentCode, item.Status, item.DeactivateReason, item.RowVersion)),
            cancellationToken);
    }

    public Task<EquipmentDto?> GetEquipmentAsync(CurrentUser user, Guid equipmentId, CancellationToken cancellationToken) =>
        _scope.Equipment(user)
            .AsNoTracking()
            .Where(item => item.Id == equipmentId)
            .Select(item => new EquipmentDto(item.Id, item.SiteId, item.EquipmentCode, item.Status, item.DeactivateReason, item.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Customer?> FindCustomerAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken) =>
        _scope.Customers(user).SingleOrDefaultAsync(customer => customer.Id == customerId, cancellationToken);

    public Task<Site?> FindSiteAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        _scope.Sites(user).SingleOrDefaultAsync(site => site.Id == siteId, cancellationToken);

    public Task<Equipment?> FindEquipmentAsync(CurrentUser user, Guid equipmentId, CancellationToken cancellationToken) =>
        _scope.Equipment(user).SingleOrDefaultAsync(item => item.Id == equipmentId, cancellationToken);

    public Task<MasterDataStatus?> GetCustomerStatusAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken) =>
        _scope.Customers(user)
            .Where(customer => customer.Id == customerId)
            .Select(customer => (MasterDataStatus?)customer.Status)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<MasterDataStatus?> GetSiteStatusAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        _scope.Sites(user)
            .Where(site => site.Id == siteId)
            .Select(site => (MasterDataStatus?)site.Status)
            .SingleOrDefaultAsync(cancellationToken);

    // Uniqueness scopes of DEC-PS1-015, served by UQ_customer_tenant_id_customer_code,
    // UQ_site_customer_id_site_code and UQ_equipment_site_id_equipment_code.
    public Task<bool> CustomerCodeExistsAsync(Guid tenantId, string customerCode, Guid excludeId, CancellationToken cancellationToken) =>
        _db.Customers.AnyAsync(
            customer => customer.TenantId == tenantId && customer.CustomerCode == customerCode && customer.Id != excludeId,
            cancellationToken);

    public Task<bool> SiteCodeExistsAsync(Guid tenantId, Guid customerId, string siteCode, Guid excludeId, CancellationToken cancellationToken) =>
        _db.Sites.AnyAsync(
            site => site.CustomerId == customerId && site.SiteCode == siteCode && site.TenantId == tenantId && site.Id != excludeId,
            cancellationToken);

    public Task<bool> EquipmentCodeExistsAsync(Guid tenantId, Guid siteId, string equipmentCode, Guid excludeId, CancellationToken cancellationToken) =>
        _db.Equipment.AnyAsync(
            item => item.SiteId == siteId && item.EquipmentCode == equipmentCode && item.TenantId == tenantId && item.Id != excludeId,
            cancellationToken);

    // DEC-PS1-013 dependency guards, served by the same customer_id / site_id leading unique indexes.
    public Task<int> CountActiveSitesAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        _db.Sites.CountAsync(
            site => site.CustomerId == customerId && site.TenantId == tenantId && site.Status == MasterDataStatus.Active,
            cancellationToken);

    public Task<int> CountActiveEquipmentAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken) =>
        _db.Equipment.CountAsync(
            item => item.SiteId == siteId && item.TenantId == tenantId && item.Status == MasterDataStatus.Active,
            cancellationToken);

    public void Add(MasterDataEntity entity) => _db.Add(entity);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<MasterDataSaveOutcome> SaveChangesAsync(MasterDataEntity? entity, byte[]? expectedRowVersion, CancellationToken cancellationToken)
    {
        if (entity is not null && expectedRowVersion is not null)
        {
            // The UPDATE applies only WHERE row_version = the client's If-Match token.
            _db.Entry(entity).Property(nameof(MasterDataEntity.RowVersion)).OriginalValue = expectedRowVersion;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return MasterDataSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return MasterDataSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation)
        {
            _db.ChangeTracker.Clear();
            return MasterDataSaveOutcome.DuplicateCode;
        }
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return MasterDataSaveOutcome.ConcurrencyConflict;
        }
    }

    public async Task<MasterDataResult<T>> RunSerializableAsync<T>(Func<Task<MasterDataResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
            // Disposing without commit rolls back, including after the server has already aborted a deadlock victim.
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var result = await command();
            if (result.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return MasterDataError.ConcurrencyConflict;
        }
    }

    private static async Task<PagedResult<TDto>> PageAsync<TEntity, TDto>(
        IQueryable<TEntity> source,
        MasterDataListQuery query,
        Func<IQueryable<TEntity>, IQueryable<TDto>> orderAndProject,
        CancellationToken cancellationToken)
        where TEntity : MasterDataEntity
    {
        if (query.Status is { } status)
        {
            source = source.Where(entity => entity.Status == status);
        }

        var paging = query.Paging;
        var totalCount = await source.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;

        IReadOnlyList<TDto> items = skip >= totalCount
            ? []
            : await orderAndProject(source).Skip((int)skip).Take(paging.PageSize).ToListAsync(cancellationToken);

        return new PagedResult<TDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
