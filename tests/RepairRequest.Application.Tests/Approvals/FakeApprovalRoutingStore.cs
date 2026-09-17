using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.Approvals;

/// <summary>
/// In-memory routing port. Pending approvals and audits are committed only when the save succeeds; SQL locking, route and
/// eligibility queries are covered by integration tests.
/// </summary>
internal sealed class FakeApprovalRoutingStore : IApprovalRoutingStore
{
    public static readonly byte[] InitialRowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
    public static readonly byte[] RoutedRowVersion = [0, 0, 0, 0, 0, 0, 0, 2];

    private readonly List<RepairRequestApproval> _pendingApprovals = [];
    private readonly List<RepairRequestApproval> _pendingRemovals = [];
    private readonly List<AuditHistory> _pendingAudits = [];

    public List<RepairRequestAggregate> Requests { get; } = [];

    public List<RepairRequestApproval> Approvals { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public List<RouteCandidate> ActiveRoutes { get; } = [];

    /// <summary>(user, Site) pairs of APPROVER users with business Site scope.</summary>
    public HashSet<(Guid UserId, Guid SiteId)> EligibleApprovers { get; } = [];

    public RepairRequestSaveOutcome SaveOutcome { get; set; } = RepairRequestSaveOutcome.Saved;

    public int Commits { get; private set; }

    public int RouteQueries { get; private set; }

    public static void Set<T>(T target, string property, object? value) =>
        typeof(T).GetProperty(property)!.SetValue(target, value);

    public RepairRequestAggregate SeedSubmitted(Guid tenantId, Guid createdBy, Guid siteId, RepairRequestStatus status = RepairRequestStatus.Submitted)
    {
        var start = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var request = RepairRequestAggregate.CreateDraft(tenantId, createdBy);
        request.EditDraft(siteId, null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", start, start.AddHours(2));
        request.Submit("RR-2026-000001", createdBy, new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc), null);
        Set(request, nameof(RepairRequestAggregate.Id), Guid.NewGuid());
        Set(request, nameof(RepairRequestAggregate.RowVersion), InitialRowVersion);
        if (status != RepairRequestStatus.Submitted)
        {
            Set(request, nameof(RepairRequestAggregate.Status), status);
        }

        Requests.Add(request);
        return request;
    }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        var result = await command();
        if (result.Succeeded)
        {
            Commits++;
        }

        return result;
    }

    public Task<RepairRequestAggregate?> LockRequestAsync(Guid tenantId, Guid repairRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(Requests.SingleOrDefault(request => request.Id == repairRequestId && request.TenantId == tenantId));

    public Task<RepairRequestApproval?> FindStepApprovalAsync(Guid tenantId, Guid repairRequestId, short stepNo, CancellationToken cancellationToken) =>
        Task.FromResult(Approvals
            .Where(approval => approval.TenantId == tenantId && approval.RepairRequestId == repairRequestId && approval.ApprovalStepNo == stepNo)
            .OrderByDescending(approval => approval.ApprovalCycleNo)
            .FirstOrDefault());

    public Task<IReadOnlyList<RouteCandidate>> ListActiveRoutesAsync(
        Guid tenantId,
        string requestCategoryCode,
        Guid siteId,
        CancellationToken cancellationToken)
    {
        RouteQueries++;
        return Task.FromResult<IReadOnlyList<RouteCandidate>>(ActiveRoutes.ToList());
    }

    public Task<bool> IsEligibleApproverAsync(Guid tenantId, Guid userId, Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(EligibleApprovers.Contains((userId, siteId)));

    public Task<IReadOnlyList<Guid>> FindEligibleApproversAsync(
        Guid tenantId,
        Guid siteId,
        Guid excludedUserId,
        int take,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(EligibleApprovers
            .Where(pair => pair.SiteId == siteId && pair.UserId != excludedUserId)
            .Select(pair => pair.UserId)
            .Order()
            .Take(take)
            .ToList());

    public void AddApproval(RepairRequestApproval approval)
    {
        Set(approval, nameof(RepairRequestApproval.Id), Guid.NewGuid());
        _pendingApprovals.Add(approval);
    }

    public void RemoveApproval(RepairRequestApproval approval) => _pendingRemovals.Add(approval);

    public void AddAudit(AuditHistory audit) => _pendingAudits.Add(audit);

    public Task<RepairRequestSaveOutcome> SaveChangesAsync(
        RepairRequestAggregate request,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        if (SaveOutcome != RepairRequestSaveOutcome.Saved || !request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            DiscardPendingChanges();
            return Task.FromResult(RepairRequestSaveOutcome.ConcurrencyConflict);
        }

        Approvals.RemoveAll(approval => _pendingRemovals.Contains(approval));
        Approvals.AddRange(_pendingApprovals);
        Audits.AddRange(_pendingAudits);
        DiscardPendingChanges();

        if (request.Status == RepairRequestStatus.UnderReview)
        {
            Set(request, nameof(RepairRequestAggregate.RowVersion), RoutedRowVersion);
        }

        return Task.FromResult(RepairRequestSaveOutcome.Saved);
    }

    public void DiscardPendingChanges()
    {
        _pendingApprovals.Clear();
        _pendingRemovals.Clear();
        _pendingAudits.Clear();
    }

    public Task<PagedResult<RoutingIssueDto>> ListRoutingIssuesAsync(CurrentUser user, PageRequest paging, CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<RoutingIssueDto>([], paging.Page, paging.PageSize, 0));
}
