using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;

namespace RepairRequest.Application.WorkEvidence;

/// <summary>
/// The outcome of resolving a Work Summary Submit's <c>evidenceIds</c>: the exact uploads to bind in request order, a field validation
/// message for field <see cref="WorkEvidenceFields.EvidenceIds"/>, or <see cref="AlreadyBound"/> (a state conflict, not a validation error).
/// </summary>
public sealed record WorkEvidenceSelection(IReadOnlyList<WorkEvidenceUpload> Uploads, string? Error, bool AlreadyBound)
{
    public static WorkEvidenceSelection Invalid(string error) => new([], error, AlreadyBound: false);
}

/// <summary>
/// The CLEAN evidence guard of Work Summary Submit and Resubmit (`docs/16` D3, OD1; `docs/13` §4.28). The client names the evidence it
/// wants frozen with <c>evidenceIds</c> — untrusted resource references, never binding data. After the Work Order evidence lock is held,
/// inside the Submit transaction, every id is resolved by the server against the authenticated tenant, the route Work Order and the exact
/// current Visit (the submission round); the file's persisted scan status must be CLEAN and the upload must not be bound yet. Exactly
/// the selected uploads are bound, in request order (slot 1..10); an unselected CLEAN upload is never bound automatically.
/// </summary>
public static class WorkEvidenceGuard
{
    public static readonly string MissingMessage =
        $"Select at least {WorkEvidenceRules.MinCleanPerSubmission} CLEAN evidence item to submit the Work Summary.";

    public static readonly string TooManyMessage =
        $"At most {WorkEvidenceRules.MaxPerSubmission} evidence items can be bound to one Work Summary.";

    public static readonly string DuplicateMessage = "Each evidence item can be selected only once.";

    /// <summary>
    /// The one answer for every id that cannot be used — unknown, another tenant's, Work Order's or Visit's, PENDING, FAILED or
    /// malware-detected. It never says which, so a caller cannot probe where an evidence id lives.
    /// </summary>
    public static readonly string InvalidSetMessage =
        "The selected evidence is not valid for this Work Summary. Select only CLEAN evidence uploaded for the current Visit.";

    /// <summary>Shape check that needs no database: 1..10 distinct, non-empty ids. Null means valid.</summary>
    public static string? ValidateShape(IReadOnlyList<Guid>? evidenceIds)
    {
        if (evidenceIds is null || evidenceIds.Count < WorkEvidenceRules.MinCleanPerSubmission)
        {
            return MissingMessage;
        }

        if (evidenceIds.Count > WorkEvidenceRules.MaxPerSubmission)
        {
            return TooManyMessage;
        }

        if (evidenceIds.Any(id => id == Guid.Empty))
        {
            return InvalidSetMessage;
        }

        return evidenceIds.Distinct().Count() != evidenceIds.Count ? DuplicateMessage : null;
    }

    public static async Task<WorkEvidenceSelection> SelectAsync(
        IWorkEvidenceStore store,
        Guid tenantId,
        Guid workOrderId,
        Guid serviceVisitId,
        IReadOnlyList<Guid>? evidenceIds,
        CancellationToken cancellationToken)
    {
        var shapeError = ValidateShape(evidenceIds);
        if (shapeError is not null)
        {
            return WorkEvidenceSelection.Invalid(shapeError);
        }

        // At most ten rows: bounded by the shape check above.
        var rows = await store.LoadSelectionAsync(tenantId, workOrderId, serviceVisitId, evidenceIds!, cancellationToken);
        var byId = rows.ToDictionary(row => row.Upload.Id);

        var selected = new List<WorkEvidenceUpload>(evidenceIds!.Count);
        var invalid = false;
        var alreadyBound = false;
        foreach (var id in evidenceIds)
        {
            if (!byId.TryGetValue(id, out var row))
            {
                invalid = true;
            }
            else if (row.Bound)
            {
                alreadyBound = true;
            }
            else if (row.ScanStatus != MalwareScanStatus.Clean)
            {
                invalid = true;
            }
            else
            {
                selected.Add(row.Upload);
            }
        }

        if (invalid)
        {
            return WorkEvidenceSelection.Invalid(InvalidSetMessage);
        }

        return alreadyBound ? new WorkEvidenceSelection([], null, AlreadyBound: true) : new WorkEvidenceSelection(selected, null, AlreadyBound: false);
    }

    /// <summary>Freezes the selected set into the round of <paramref name="workSummaryId"/>; slot numbers follow the request order.</summary>
    public static IReadOnlyList<WorkSummaryEvidence> Bind(IReadOnlyList<WorkEvidenceUpload> selected, Guid workSummaryId, DateTime boundAt) =>
        selected.Select((upload, index) => WorkSummaryEvidence.Bind(upload, workSummaryId, index + 1, boundAt)).ToList();
}
