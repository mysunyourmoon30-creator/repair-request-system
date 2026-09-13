using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>
/// Site master use cases (DEC-PS1-002/013/014/015; S1-004 decisions D1-D3). A Site is created under a Customer
/// that is in the caller's scope and ACTIVE; its Customer never changes afterwards.
/// </summary>
public sealed class SiteService
{
    private readonly IMasterDataStore _store;
    private readonly TimeProvider _clock;

    public SiteService(IMasterDataStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <summary>Sites of a Customer within scope, or null when the Customer is not in scope (404).</summary>
    public Task<PagedResult<SiteDto>?> ListAsync(CurrentUser user, Guid customerId, MasterDataListQuery query, CancellationToken cancellationToken) =>
        _store.ListSitesAsync(user, customerId, query, cancellationToken);

    public Task<SiteDto?> GetAsync(CurrentUser user, Guid siteId, CancellationToken cancellationToken) =>
        _store.GetSiteAsync(user, siteId, cancellationToken);

    /// <summary>DEC-PS1-002: the Customer must be ACTIVE; the check and the insert share one SERIALIZABLE transaction.</summary>
    public Task<CommandResult<SiteDto>> CreateAsync(
        CommandContext context,
        Guid customerId,
        string? siteCode,
        CancellationToken cancellationToken) =>
        _store.RunSerializableAsync<SiteDto>(
            async () =>
            {
                var customerStatus = await _store.GetCustomerStatusAsync(context.User, customerId, cancellationToken);
                if (customerStatus is null)
                {
                    return CommandError.NotFound;
                }

                var errors = new Dictionary<string, string[]>();
                var code = MasterDataValidation.Code(siteCode, MasterDataFields.SiteCode, errors);
                if (customerStatus != MasterDataStatus.Active)
                {
                    errors[MasterDataFields.CustomerId] = [string.Format(MasterDataCommandSteps.InactiveParentMessage, "Customer")];
                }

                if (code is null || errors.Count > 0)
                {
                    return CommandError.Validation(errors);
                }

                var tenantId = context.User.TenantId;
                if (await _store.SiteCodeExistsAsync(tenantId, customerId, code, Guid.Empty, cancellationToken))
                {
                    return MasterDataCommandSteps.DuplicateCode(MasterDataFields.SiteCode);
                }

                var site = new Site(tenantId, customerId, code);
                _store.Add(site);
                _store.AddAudit(MasterDataAudit.Created(
                    context,
                    site,
                    new Dictionary<string, object?>
                    {
                        [MasterDataFields.CustomerId] = customerId,
                        [MasterDataFields.SiteCode] = code
                    },
                    MasterDataCommandSteps.UtcNow(_clock)));

                return await MasterDataCommandSteps.SaveAsync(_store, site, null, MasterDataFields.SiteCode, ToDto, cancellationToken);
            },
            cancellationToken);

    public async Task<CommandResult<SiteDto>> UpdateAsync(
        CommandContext context,
        Guid siteId,
        byte[] expectedRowVersion,
        string? siteCode,
        CancellationToken cancellationToken)
    {
        var site = await _store.FindSiteAsync(context.User, siteId, cancellationToken);

        return await MasterDataCommandSteps.UpdateCodeAsync(
            _store,
            _clock,
            context,
            site,
            expectedRowVersion,
            siteCode,
            MasterDataFields.SiteCode,
            entity => entity.SiteCode,
            (entity, code) => entity.ChangeCode(code),
            (entity, code) => _store.SiteCodeExistsAsync(entity.TenantId, entity.CustomerId, code, entity.Id, cancellationToken),
            ToDto,
            cancellationToken);
    }

    /// <summary>Decision D2: the Customer must be ACTIVE; checked in the same SERIALIZABLE transaction.</summary>
    public Task<CommandResult<SiteDto>> ActivateAsync(
        CommandContext context,
        Guid siteId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken) =>
        _store.RunSerializableAsync<SiteDto>(
            async () =>
            {
                var site = await _store.FindSiteAsync(context.User, siteId, cancellationToken);

                return await MasterDataCommandSteps.ActivateAsync(
                    _store,
                    _clock,
                    context,
                    site,
                    expectedRowVersion,
                    entity => _store.GetCustomerStatusAsync(context.User, entity.CustomerId, cancellationToken),
                    MasterDataFields.CustomerId,
                    "Customer",
                    ToDto,
                    cancellationToken);
            },
            cancellationToken);

    /// <summary>DEC-PS1-013: denied while active Equipment exists; guard and change share one SERIALIZABLE transaction.</summary>
    public Task<CommandResult<SiteDto>> DeactivateAsync(
        CommandContext context,
        Guid siteId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken) =>
        _store.RunSerializableAsync<SiteDto>(
            async () =>
            {
                var site = await _store.FindSiteAsync(context.User, siteId, cancellationToken);

                return await MasterDataCommandSteps.DeactivateAsync(
                    _store,
                    _clock,
                    context,
                    site,
                    expectedRowVersion,
                    reason,
                    entity => _store.CountActiveEquipmentAsync(entity.TenantId, entity.Id, cancellationToken),
                    "Equipment",
                    ToDto,
                    cancellationToken);
            },
            cancellationToken);

    private static SiteDto ToDto(Site site) =>
        new(site.Id, site.CustomerId, site.SiteCode, site.Status, site.DeactivateReason, site.RowVersion);
}
