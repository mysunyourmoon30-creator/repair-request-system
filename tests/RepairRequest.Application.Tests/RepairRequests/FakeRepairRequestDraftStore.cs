using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.RepairRequests;

/// <summary>
/// In-memory Draft port for Application-rule tests. Site scope is modelled as an explicit set of Sites the caller may
/// select, lookups as case-insensitive code dictionaries and contact eligibility as (contact, Site) pairs; SQL scope
/// behaviour is covered by the integration and API tests.
/// </summary>
internal sealed class FakeRepairRequestDraftStore : IRepairRequestDraftStore
{
    public static readonly byte[] InitialRowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
    public static readonly byte[] SavedRowVersion = [0, 0, 0, 0, 0, 0, 0, 2];

    private readonly List<RepairRequestAggregate> _pending = [];
    private readonly List<AuditHistory> _pendingAudits = [];

    public List<RepairRequestAggregate> Requests { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public Dictionary<Guid, MasterDataStatus> SitesInScope { get; } = new();

    /// <summary>Equipment id -> (Site id, status).</summary>
    public Dictionary<Guid, (Guid SiteId, MasterDataStatus Status)> Equipment { get; } = new();

    public Dictionary<string, MasterDataStatus> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MasterDataStatus> Priorities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<(Guid ContactId, Guid SiteId)> EligibleContacts { get; } = [];

    public int SaveCount { get; private set; }

    public int SelectionQueries { get; private set; }

    public RepairRequestSaveOutcome SaveOutcome { get; set; } = RepairRequestSaveOutcome.Saved;

    public static CommandContext RequesterContext(Guid tenantId, Guid? userId = null) =>
        new(new CurrentUser(userId ?? Guid.NewGuid(), tenantId, [RoleCodes.Requester]), Guid.NewGuid());

    public Guid AddSite(MasterDataStatus status = MasterDataStatus.Active)
    {
        var siteId = Guid.NewGuid();
        SitesInScope[siteId] = status;
        return siteId;
    }

    public Guid AddEquipment(Guid siteId, MasterDataStatus status = MasterDataStatus.Active)
    {
        var equipmentId = Guid.NewGuid();
        Equipment[equipmentId] = (siteId, status);
        return equipmentId;
    }

    public Guid AddEligibleContact(Guid siteId)
    {
        var contactId = Guid.NewGuid();
        EligibleContacts.Add((contactId, siteId));
        return contactId;
    }

    public RepairRequestAggregate SeedDraft(
        CommandContext owner,
        Guid? siteId = null,
        Guid? equipmentId = null,
        string? description = null,
        string? categoryCode = null,
        string? priorityCode = null,
        Guid? contactId = null,
        DateTime? preferredStartAt = null,
        DateTime? preferredEndAt = null)
    {
        var draft = RepairRequestAggregate.CreateDraft(owner.User.TenantId, owner.User.UserId);
        draft.EditDraft(siteId, equipmentId, categoryCode, priorityCode, contactId, description, preferredStartAt, preferredEndAt);
        Set(draft, nameof(RepairRequestAggregate.Id), Guid.NewGuid());
        Set(draft, nameof(RepairRequestAggregate.RowVersion), InitialRowVersion);
        Requests.Add(draft);
        return draft;
    }

    public static void ForceStatus(RepairRequestAggregate request, RepairRequestStatus status) =>
        Set(request, nameof(RepairRequestAggregate.Status), status);

    public static void ForceRowVersion(RepairRequestAggregate request, byte[] rowVersion) =>
        Set(request, nameof(RepairRequestAggregate.RowVersion), rowVersion);

    /// <summary>When set, <see cref="GetAsync"/> (the final-state read-back) throws it; other reads are unaffected.</summary>
    public Exception? ThrowOnGet { get; set; }

    public Task<RepairRequestDraftDto?> GetAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        ThrowOnGet is not null ? Task.FromException<RepairRequestDraftDto?>(ThrowOnGet) : Task.FromResult(Requests
            .Where(request => request.Id == repairRequestId && request.TenantId == user.TenantId)
            .Select(RepairRequestDraftDto.From)
            .SingleOrDefault());

    public Task<PagedResult<RepairRequestDraftDto>> ListAsync(CurrentUser user, RepairRequestListQuery query, CancellationToken cancellationToken)
    {
        var source = Requests.Where(request => request.TenantId == user.TenantId);
        if (query.Status is { } status)
        {
            source = source.Where(request => request.Status == status);
        }

        var ordered = source.OrderByDescending(request => request.SubmittedAt).ThenByDescending(request => request.Id).ToList();
        var items = ordered
            .Skip((query.Paging.Page - 1) * query.Paging.PageSize)
            .Take(query.Paging.PageSize)
            .Select(RepairRequestDraftDto.From)
            .ToList();

        return Task.FromResult(new PagedResult<RepairRequestDraftDto>(items, query.Paging.Page, query.Paging.PageSize, ordered.Count));
    }

    public Task<RepairRequestAggregate?> FindOwnAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(Requests.SingleOrDefault(request =>
            request.Id == repairRequestId && request.TenantId == user.TenantId && request.CreatedBy == user.UserId));

    public Task<DraftSelection> GetDraftSelectionAsync(CurrentUser user, DraftSelectionQuery query, CancellationToken cancellationToken)
    {
        SelectionQueries++;

        MasterDataStatus? siteStatus = query.SiteId is { } siteId && SitesInScope.TryGetValue(siteId, out var status) ? status : null;

        MasterDataStatus? equipmentStatus =
            query.EquipmentId is { } equipmentId && Equipment.TryGetValue(equipmentId, out var equipment) && equipment.SiteId == query.SiteId
                ? equipment.Status
                : null;

        var contactEligible = query.RequestContactId is { } contactId
                              && query.SiteId is { } contactSite
                              && EligibleContacts.Contains((contactId, contactSite));

        return Task.FromResult(new DraftSelection(
            siteStatus,
            equipmentStatus,
            Lookup(Categories, query.RequestCategoryCode),
            Lookup(Priorities, query.PriorityCode),
            contactEligible));
    }

    public void Add(RepairRequestAggregate repairRequest)
    {
        Set(repairRequest, nameof(RepairRequestAggregate.Id), Guid.NewGuid());
        _pending.Add(repairRequest);
    }

    public void AddAudit(AuditHistory audit) => _pendingAudits.Add(audit);

    public Task<RepairRequestSaveOutcome> SaveChangesAsync(RepairRequestAggregate repairRequest, byte[]? expectedRowVersion, CancellationToken cancellationToken)
    {
        SaveCount++;

        if (SaveOutcome != RepairRequestSaveOutcome.Saved)
        {
            _pending.Clear();
            _pendingAudits.Clear();
            return Task.FromResult(SaveOutcome);
        }

        foreach (var pending in _pending)
        {
            Set(pending, nameof(RepairRequestAggregate.RowVersion), InitialRowVersion);
            Requests.Add(pending);
        }

        if (expectedRowVersion is not null)
        {
            Set(repairRequest, nameof(RepairRequestAggregate.RowVersion), SavedRowVersion);
        }

        Audits.AddRange(_pendingAudits);
        _pending.Clear();
        _pendingAudits.Clear();
        return Task.FromResult(RepairRequestSaveOutcome.Saved);
    }

    private static LookupSelection? Lookup(Dictionary<string, MasterDataStatus> lookups, string? code)
    {
        if (code is null || !lookups.TryGetValue(code, out var status))
        {
            return null;
        }

        var canonical = lookups.Keys.First(key => string.Equals(key, code, StringComparison.OrdinalIgnoreCase));
        return new LookupSelection(canonical, status);
    }

    private static void Set(RepairRequestAggregate request, string property, object value) =>
        typeof(RepairRequestAggregate).GetProperty(property)!.SetValue(request, value);
}
