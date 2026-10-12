using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;

namespace RepairRequest.Application.Tests.WorkEvidence;

/// <summary>
/// In-memory Work Order evidence port. The "transaction" commits staged rows only when the command succeeds, mirroring the rollback of the SQL
/// implementation; locking, scan-status races and SQL scope are covered by the SQL Server integration tests.
/// </summary>
internal sealed class FakeWorkEvidenceStore : IWorkEvidenceStore
{
    public WorkEvidenceUploadTarget? Target { get; set; }

    public bool LockGranted { get; set; } = true;

    public int LockCalls { get; private set; }

    /// <summary>
    /// Every upload the fake "database" holds, with its file's scan status and bound flag. <see cref="LoadSelectionAsync"/> applies the store
    /// contract to it: only rows of exactly the asked tenant, Work Order and Visit whose id was requested are returned.
    /// </summary>
    public List<WorkEvidenceSelectionRow> SelectionRows { get; } = [];

    public int SelectionCalls { get; private set; }

    public Guid? LastTenantId { get; private set; }

    public Guid? LastWorkOrderId { get; private set; }

    public Guid? LastVisitId { get; private set; }

    public IReadOnlyCollection<Guid>? LastRequestedIds { get; private set; }

    public List<FileAsset> Files { get; } = [];

    public List<WorkEvidenceUpload> Uploads { get; } = [];

    public List<WorkSummaryEvidence> Bindings { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public int Commits { get; private set; }

    public bool FailSave { get; set; }

    public (ReportAudience Audience, IReadOnlyList<WorkEvidenceItemDto> Items)? ListResult { get; set; }

    public WorkEvidenceDownloadInfo? DownloadInfo { get; set; }

    private readonly List<FileAsset> _stagedFiles = [];
    private readonly List<WorkEvidenceUpload> _stagedUploads = [];
    private readonly List<AuditHistory> _stagedAudits = [];

    private bool _inTransaction;

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        _inTransaction = true;
        CommandResult<T> result;
        try
        {
            result = await command();
        }
        finally
        {
            _inTransaction = false;
        }

        if (result.Succeeded)
        {
            Files.AddRange(_stagedFiles);
            Uploads.AddRange(_stagedUploads);
            Audits.AddRange(_stagedAudits);
            Commits++;
        }

        _stagedFiles.Clear();
        _stagedUploads.Clear();
        _stagedAudits.Clear();
        return result;
    }

    public Task<WorkEvidenceUploadTarget?> FindUploadTargetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(Target);

    public Task<bool> TryLockWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        LockCalls++;
        return Task.FromResult(LockGranted);
    }

    public Task<IReadOnlyList<WorkEvidenceSelectionRow>> LoadSelectionAsync(
        Guid tenantId, Guid workOrderId, Guid serviceVisitId, IReadOnlyCollection<Guid> evidenceIds, CancellationToken cancellationToken)
    {
        SelectionCalls++;
        LastTenantId = tenantId;
        LastWorkOrderId = workOrderId;
        LastVisitId = serviceVisitId;
        LastRequestedIds = evidenceIds;

        IReadOnlyList<WorkEvidenceSelectionRow> rows = SelectionRows
            .Where(row => row.Upload.TenantId == tenantId
                          && row.Upload.WorkOrderId == workOrderId
                          && row.Upload.ServiceVisitId == serviceVisitId
                          && evidenceIds.Contains(row.Upload.Id))
            .ToList();
        return Task.FromResult(rows);
    }

    public void AddFileAsset(FileAsset fileAsset)
    {
        // The Id is assigned on insert by EF (sequential GUID) and has a private setter; a persisted file always has one and the
        // evidence row requires it — the same test-only idiom the attachment fake uses.
        typeof(FileAsset).GetProperty(nameof(FileAsset.Id))!.SetValue(fileAsset, Guid.NewGuid());
        _stagedFiles.Add(fileAsset);
    }

    public void AddUpload(WorkEvidenceUpload upload) => _stagedUploads.Add(upload);

    public void AddBinding(WorkSummaryEvidence binding) => Bindings.Add(binding);

    public void AddAudit(AuditHistory audit)
    {
        // Outside a transaction (a download) the audit is saved by the next SaveChanges directly.
        (_inTransaction ? _stagedAudits : Audits).Add(audit);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        FailSave ? throw new InvalidOperationException("Simulated database failure.") : Task.CompletedTask;

    public Task<ReportAudience> ResolveAudienceAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(ListResult?.Audience ?? DownloadInfo?.Audience ?? ReportAudience.None);

    public Task<(ReportAudience Audience, IReadOnlyList<WorkEvidenceItemDto> Items)?> ListAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(ListResult);

    public Task<WorkEvidenceDownloadInfo?> FindDownloadAsync(CurrentUser user, Guid workOrderId, Guid evidenceId, CancellationToken cancellationToken) =>
        Task.FromResult(DownloadInfo);
}
