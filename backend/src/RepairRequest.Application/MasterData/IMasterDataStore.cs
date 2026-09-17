using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>Result of persisting a master-data change.</summary>
public enum MasterDataSaveOutcome
{
    Saved,

    /// <summary>A scoped unique constraint rejected the code (DEC-PS1-015), e.g. after a concurrent create.</summary>
    DuplicateCode,

    /// <summary>The row version no longer matched, or the transaction lost a lock conflict. Nothing was written.</summary>
    ConcurrencyConflict
}

/// <summary>
/// Focused persistence port for Customer / Site / Equipment use cases (RR-ARCH-001 section 5.2).
/// Every caller-facing lookup is restricted to the caller's tenant and Site scope (S1-003 <see cref="IDataScope"/>),
/// so out-of-scope and nonexistent ids are indistinguishable. Business rules stay in the Application services.
/// </summary>
public interface IMasterDataStore
{
    Task<PagedResult<CustomerDto>> ListCustomersAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken);

    Task<CustomerDto?> GetCustomerAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Sites of one Customer within scope, or null when the Customer itself is not in scope.</summary>
    Task<PagedResult<SiteDto>?> ListSitesAsync(CurrentUser user, Guid customerId, MasterDataListQuery query, CancellationToken cancellationToken);

    Task<SiteDto?> GetSiteAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken);

    /// <summary>Equipment of one Site within scope, or null when the Site itself is not in scope.</summary>
    Task<PagedResult<EquipmentDto>?> ListEquipmentAsync(CurrentUser user, Guid siteId, MasterDataListQuery query, CancellationToken cancellationToken);

    Task<EquipmentDto?> GetEquipmentAsync(CurrentUser user, Guid equipmentId, CancellationToken cancellationToken);

    /// <summary>Tracked, scoped load for a command.</summary>
    Task<Customer?> FindCustomerAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken);

    Task<Site?> FindSiteAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken);

    Task<Equipment?> FindEquipmentAsync(CurrentUser user, Guid equipmentId, CancellationToken cancellationToken);

    /// <summary>Status of a Customer within scope, or null when it is not in scope.</summary>
    Task<MasterDataStatus?> GetCustomerStatusAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Status of a Site within scope, or null when it is not in scope.</summary>
    Task<MasterDataStatus?> GetSiteStatusAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken);

    /// <summary>customer_code taken within the Tenant by a record other than <paramref name="excludeId"/>.</summary>
    Task<bool> CustomerCodeExistsAsync(Guid tenantId, string customerCode, Guid excludeId, CancellationToken cancellationToken);

    /// <summary>site_code taken within the Customer by a record other than <paramref name="excludeId"/>.</summary>
    Task<bool> SiteCodeExistsAsync(Guid tenantId, Guid customerId, string siteCode, Guid excludeId, CancellationToken cancellationToken);

    /// <summary>equipment_code taken within the Site by a record other than <paramref name="excludeId"/>.</summary>
    Task<bool> EquipmentCodeExistsAsync(Guid tenantId, Guid siteId, string equipmentCode, Guid excludeId, CancellationToken cancellationToken);

    Task<int> CountActiveSitesAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    Task<int> CountActiveEquipmentAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken);

    void Add(MasterDataEntity entity);

    void AddAudit(AuditHistory audit);

    /// <summary>
    /// Saves pending changes in one database transaction. When <paramref name="expectedRowVersion"/> is given,
    /// the update of <paramref name="entity"/> only applies while its row version still equals that token.
    /// </summary>
    Task<MasterDataSaveOutcome> SaveChangesAsync(MasterDataEntity? entity, byte[]? expectedRowVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a guarded command in one SERIALIZABLE transaction so dependency guards (DEC-PS1-013, decision D2) cannot
    /// be broken by a concurrent change. Commits only a successful result; a lock conflict becomes a concurrency conflict.
    /// </summary>
    Task<CommandResult<T>> RunSerializableAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);
}
