using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Auditing;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.RepairRequests;

/// <summary>
/// In-memory Submit port. The "transaction" counts duplicates first and commits the counter and audit only when the save
/// succeeds, mirroring the rollback semantics of the SQL implementation; the duplicate-key lock, allocation concurrency and
/// locking are covered by integration tests.
/// </summary>
internal sealed class FakeRepairRequestSubmitStore : IRepairRequestSubmitStore
{
    public HashSet<Guid> RequestsWithCleanPhoto { get; } = [];

    public int DuplicateCount { get; set; }

    public List<DuplicateQuery> DuplicateQueries { get; } = [];

    public int PhotoQueries { get; private set; }

    public int SubmitCalls { get; private set; }

    public Dictionary<(Guid TenantId, int Year), int> Counters { get; } = new();

    public List<AuditHistory> Audits { get; } = [];

    public RepairRequestSaveOutcome SaveOutcome { get; set; } = RepairRequestSaveOutcome.Saved;

    public Task<bool> HasCleanPhotoAsync(Guid tenantId, Guid repairRequestId, CancellationToken cancellationToken)
    {
        PhotoQueries++;
        return Task.FromResult(RequestsWithCleanPhoto.Contains(repairRequestId));
    }

    public Task<int> CountDuplicatesAsync(DuplicateQuery query, CancellationToken cancellationToken)
    {
        DuplicateQueries.Add(query);
        return Task.FromResult(DuplicateCount);
    }

    public async Task<RepairRequestSubmitResult> SubmitAsync(
        RepairRequestAggregate request,
        byte[] expectedRowVersion,
        DuplicateQuery duplicateQuery,
        int requestYear,
        bool hasContinuationReason,
        Func<int, int, AuditHistory> applySubmit,
        CancellationToken cancellationToken)
    {
        SubmitCalls++;

        var duplicateCount = await CountDuplicatesAsync(duplicateQuery, cancellationToken);
        if (duplicateCount > 0 && !hasContinuationReason)
        {
            return new RepairRequestSubmitResult(RepairRequestSubmitStatus.DuplicateWarning, duplicateCount);
        }

        var key = (request.TenantId, requestYear);
        var next = Counters.GetValueOrDefault(key) + 1;
        var audit = applySubmit(next, duplicateCount);

        if (SaveOutcome != RepairRequestSaveOutcome.Saved || !request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return new RepairRequestSubmitResult(RepairRequestSubmitStatus.ConcurrencyConflict, duplicateCount);
        }

        Counters[key] = next;
        Audits.Add(audit);
        FakeRepairRequestDraftStore.ForceRowVersion(request, FakeRepairRequestDraftStore.SavedRowVersion);
        return new RepairRequestSubmitResult(RepairRequestSubmitStatus.Saved, duplicateCount);
    }
}
