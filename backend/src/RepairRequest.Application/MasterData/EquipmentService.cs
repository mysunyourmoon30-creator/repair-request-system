using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>
/// Equipment master use cases (DEC-PS1-003/013/014/015; S1-004 decisions D1-D3). Equipment is created under a Site
/// that is in the caller's scope and ACTIVE; its Site never changes afterwards.
/// </summary>
public sealed class EquipmentService
{
    private readonly IMasterDataStore _store;
    private readonly TimeProvider _clock;

    public EquipmentService(IMasterDataStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <summary>Equipment of a Site within scope, or null when the Site is not in scope (404).</summary>
    public Task<PagedResult<EquipmentDto>?> ListAsync(CurrentUser user, Guid siteId, MasterDataListQuery query, CancellationToken cancellationToken) =>
        _store.ListEquipmentAsync(user, siteId, query, cancellationToken);

    public Task<EquipmentDto?> GetAsync(CurrentUser user, Guid equipmentId, CancellationToken cancellationToken) =>
        _store.GetEquipmentAsync(user, equipmentId, cancellationToken);

    /// <summary>DEC-PS1-003: the Site must be ACTIVE; the check and the insert share one SERIALIZABLE transaction.</summary>
    public Task<MasterDataResult<EquipmentDto>> CreateAsync(
        MasterDataCommandContext context,
        Guid siteId,
        string? equipmentCode,
        CancellationToken cancellationToken) =>
        _store.RunSerializableAsync<EquipmentDto>(
            async () =>
            {
                var siteStatus = await _store.GetSiteStatusAsync(context.User, siteId, cancellationToken);
                if (siteStatus is null)
                {
                    return MasterDataError.NotFound;
                }

                var errors = new Dictionary<string, string[]>();
                var code = MasterDataValidation.Code(equipmentCode, MasterDataFields.EquipmentCode, errors);
                if (siteStatus != MasterDataStatus.Active)
                {
                    errors[MasterDataFields.SiteId] = [string.Format(MasterDataCommandSteps.InactiveParentMessage, "Site")];
                }

                if (code is null || errors.Count > 0)
                {
                    return MasterDataError.Validation(errors);
                }

                var tenantId = context.User.TenantId;
                if (await _store.EquipmentCodeExistsAsync(tenantId, siteId, code, Guid.Empty, cancellationToken))
                {
                    return MasterDataCommandSteps.DuplicateCode(MasterDataFields.EquipmentCode);
                }

                var equipment = new Equipment(tenantId, siteId, code);
                _store.Add(equipment);
                _store.AddAudit(MasterDataAudit.Created(
                    context,
                    equipment,
                    new Dictionary<string, object?>
                    {
                        [MasterDataFields.SiteId] = siteId,
                        [MasterDataFields.EquipmentCode] = code
                    },
                    MasterDataCommandSteps.UtcNow(_clock)));

                return await MasterDataCommandSteps.SaveAsync(_store, equipment, null, MasterDataFields.EquipmentCode, ToDto, cancellationToken);
            },
            cancellationToken);

    public async Task<MasterDataResult<EquipmentDto>> UpdateAsync(
        MasterDataCommandContext context,
        Guid equipmentId,
        byte[] expectedRowVersion,
        string? equipmentCode,
        CancellationToken cancellationToken)
    {
        var equipment = await _store.FindEquipmentAsync(context.User, equipmentId, cancellationToken);

        return await MasterDataCommandSteps.UpdateCodeAsync(
            _store,
            _clock,
            context,
            equipment,
            expectedRowVersion,
            equipmentCode,
            MasterDataFields.EquipmentCode,
            entity => entity.EquipmentCode,
            (entity, code) => entity.ChangeCode(code),
            (entity, code) => _store.EquipmentCodeExistsAsync(entity.TenantId, entity.SiteId, code, entity.Id, cancellationToken),
            ToDto,
            cancellationToken);
    }

    /// <summary>Decision D2: the Site must be ACTIVE; checked in the same SERIALIZABLE transaction.</summary>
    public Task<MasterDataResult<EquipmentDto>> ActivateAsync(
        MasterDataCommandContext context,
        Guid equipmentId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken) =>
        _store.RunSerializableAsync<EquipmentDto>(
            async () =>
            {
                var equipment = await _store.FindEquipmentAsync(context.User, equipmentId, cancellationToken);

                return await MasterDataCommandSteps.ActivateAsync(
                    _store,
                    _clock,
                    context,
                    equipment,
                    expectedRowVersion,
                    entity => _store.GetSiteStatusAsync(context.User, entity.SiteId, cancellationToken),
                    MasterDataFields.SiteId,
                    "Site",
                    ToDto,
                    cancellationToken);
            },
            cancellationToken);

    /// <summary>Equipment has no child master, so no dependency guard applies (DEC-PS1-013).</summary>
    public async Task<MasterDataResult<EquipmentDto>> DeactivateAsync(
        MasterDataCommandContext context,
        Guid equipmentId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken)
    {
        var equipment = await _store.FindEquipmentAsync(context.User, equipmentId, cancellationToken);

        return await MasterDataCommandSteps.DeactivateAsync(
            _store, _clock, context, equipment, expectedRowVersion, reason, null, null, ToDto, cancellationToken);
    }

    private static EquipmentDto ToDto(Equipment equipment) =>
        new(equipment.Id, equipment.SiteId, equipment.EquipmentCode, equipment.Status, equipment.DeactivateReason, equipment.RowVersion);
}
