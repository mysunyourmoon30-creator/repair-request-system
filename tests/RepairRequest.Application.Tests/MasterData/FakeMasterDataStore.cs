using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.MasterData;

/// <summary>
/// In-memory port used to test Application-layer rules in isolation. Scope is reduced to the tenant boundary;
/// site-level scope and SQL behaviour are covered by the integration and API tests.
/// </summary>
internal sealed class FakeMasterDataStore : IMasterDataStore
{
    public static readonly byte[] InitialRowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
    public static readonly byte[] SavedRowVersion = [0, 0, 0, 0, 0, 0, 0, 2];

    private readonly List<MasterDataEntity> _pendingEntities = [];
    private readonly List<AuditHistory> _pendingAudits = [];

    public List<Customer> Customers { get; } = [];

    public List<Site> Sites { get; } = [];

    public List<Equipment> Equipment { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public int SaveCount { get; private set; }

    public int SerializableRuns { get; private set; }

    /// <summary>Outcome the next saves report; anything other than Saved discards pending changes.</summary>
    public MasterDataSaveOutcome SaveOutcome { get; set; } = MasterDataSaveOutcome.Saved;

    public static CommandContext AdministratorContext(Guid tenantId) =>
        new(new CurrentUser(Guid.NewGuid(), tenantId, [RoleCodes.Administrator]), Guid.NewGuid());

    public Customer SeedCustomer(Guid tenantId, string code, string? inactiveReason = null)
    {
        var customer = Seed(new Customer(tenantId, code), inactiveReason);
        Customers.Add(customer);
        return customer;
    }

    public Site SeedSite(Customer customer, string code, string? inactiveReason = null)
    {
        var site = Seed(new Site(customer.TenantId, customer.Id, code), inactiveReason);
        Sites.Add(site);
        return site;
    }

    public Equipment SeedEquipment(Site site, string code, string? inactiveReason = null)
    {
        var equipment = Seed(new Equipment(site.TenantId, site.Id, code), inactiveReason);
        Equipment.Add(equipment);
        return equipment;
    }

    public Task<PagedResult<CustomerDto>> ListCustomersAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Page(Customers.Where(c => c.TenantId == user.TenantId).Select(ToDto).ToList(), query));

    public Task<CustomerDto?> GetCustomerAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Customers, user, customerId) is { } customer ? ToDto(customer) : null);

    public Task<PagedResult<SiteDto>?> ListSitesAsync(CurrentUser user, Guid customerId, MasterDataListQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Customers, user, customerId) is null
            ? null
            : Page(Sites.Where(s => s.CustomerId == customerId).Select(ToDto).ToList(), query));

    public Task<SiteDto?> GetSiteAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Sites, user, siteId) is { } site ? ToDto(site) : null);

    public Task<PagedResult<EquipmentDto>?> ListEquipmentAsync(CurrentUser user, Guid siteId, MasterDataListQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Sites, user, siteId) is null
            ? null
            : Page(Equipment.Where(e => e.SiteId == siteId).Select(ToDto).ToList(), query));

    public Task<EquipmentDto?> GetEquipmentAsync(CurrentUser user, Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Equipment, user, equipmentId) is { } equipment ? ToDto(equipment) : null);

    public Task<Customer?> FindCustomerAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Customers, user, customerId));

    public Task<Site?> FindSiteAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Sites, user, siteId));

    public Task<Equipment?> FindEquipmentAsync(CurrentUser user, Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Equipment, user, equipmentId));

    public Task<MasterDataStatus?> GetCustomerStatusAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Customers, user, customerId)?.Status);

    public Task<MasterDataStatus?> GetSiteStatusAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(Visible(Sites, user, siteId)?.Status);

    // Case-insensitive like the default SQL Server collation behind the unique indexes.
    public Task<bool> CustomerCodeExistsAsync(Guid tenantId, string customerCode, Guid excludeId, CancellationToken cancellationToken) =>
        Task.FromResult(Customers.Any(c => c.TenantId == tenantId && SameCode(c.CustomerCode, customerCode) && c.Id != excludeId));

    public Task<bool> SiteCodeExistsAsync(Guid tenantId, Guid customerId, string siteCode, Guid excludeId, CancellationToken cancellationToken) =>
        Task.FromResult(Sites.Any(s => s.TenantId == tenantId && s.CustomerId == customerId && SameCode(s.SiteCode, siteCode) && s.Id != excludeId));

    public Task<bool> EquipmentCodeExistsAsync(Guid tenantId, Guid siteId, string equipmentCode, Guid excludeId, CancellationToken cancellationToken) =>
        Task.FromResult(Equipment.Any(e => e.TenantId == tenantId && e.SiteId == siteId && SameCode(e.EquipmentCode, equipmentCode) && e.Id != excludeId));

    public Task<int> CountActiveSitesAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Sites.Count(s => s.TenantId == tenantId && s.CustomerId == customerId && s.Status == MasterDataStatus.Active));

    public Task<int> CountActiveEquipmentAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(Equipment.Count(e => e.TenantId == tenantId && e.SiteId == siteId && e.Status == MasterDataStatus.Active));

    public void Add(MasterDataEntity entity)
    {
        SetProperty(entity, nameof(MasterDataEntity.Id), Guid.NewGuid());
        _pendingEntities.Add(entity);
    }

    public void AddAudit(AuditHistory audit) => _pendingAudits.Add(audit);

    public Task<MasterDataSaveOutcome> SaveChangesAsync(MasterDataEntity? entity, byte[]? expectedRowVersion, CancellationToken cancellationToken)
    {
        SaveCount++;

        if (SaveOutcome != MasterDataSaveOutcome.Saved)
        {
            _pendingEntities.Clear();
            _pendingAudits.Clear();
            return Task.FromResult(SaveOutcome);
        }

        foreach (var pending in _pendingEntities)
        {
            SetProperty(pending, nameof(MasterDataEntity.RowVersion), InitialRowVersion);
            switch (pending)
            {
                case Customer customer:
                    Customers.Add(customer);
                    break;
                case Site site:
                    Sites.Add(site);
                    break;
                case Equipment equipment:
                    Equipment.Add(equipment);
                    break;
            }
        }

        if (entity is not null && expectedRowVersion is not null)
        {
            SetProperty(entity, nameof(MasterDataEntity.RowVersion), SavedRowVersion);
        }

        Audits.AddRange(_pendingAudits);
        _pendingEntities.Clear();
        _pendingAudits.Clear();
        return Task.FromResult(MasterDataSaveOutcome.Saved);
    }

    public async Task<CommandResult<T>> RunSerializableAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        SerializableRuns++;
        return await command();
    }

    private static TEntity Seed<TEntity>(TEntity entity, string? inactiveReason)
        where TEntity : MasterDataEntity
    {
        SetProperty(entity, nameof(MasterDataEntity.Id), Guid.NewGuid());
        SetProperty(entity, nameof(MasterDataEntity.RowVersion), InitialRowVersion);
        if (inactiveReason is not null)
        {
            entity.Deactivate(inactiveReason);
        }

        return entity;
    }

    private static void SetProperty(MasterDataEntity entity, string name, object value) =>
        typeof(MasterDataEntity).GetProperty(name)!.SetValue(entity, value);

    private static TEntity? Visible<TEntity>(IEnumerable<TEntity> source, CurrentUser user, Guid id)
        where TEntity : MasterDataEntity =>
        source.SingleOrDefault(entity => entity.Id == id && entity.TenantId == user.TenantId);

    private static bool SameCode(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static PagedResult<T> Page<T>(List<T> items, MasterDataListQuery query) =>
        new(items, query.Paging.Page, query.Paging.PageSize, items.Count);

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.CustomerCode, c.Status, c.DeactivateReason, c.RowVersion);

    private static SiteDto ToDto(Site s) => new(s.Id, s.CustomerId, s.SiteCode, s.Status, s.DeactivateReason, s.RowVersion);

    private static EquipmentDto ToDto(Equipment e) => new(e.Id, e.SiteId, e.EquipmentCode, e.Status, e.DeactivateReason, e.RowVersion);
}

internal sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
