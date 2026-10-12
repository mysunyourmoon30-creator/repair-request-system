namespace RepairRequest.Domain.WorkEvidence;

/// <summary>`docs/16` OD1: each submitted Work Summary carries at least one and at most ten bound CLEAN evidence items.</summary>
public static class WorkEvidenceRules
{
    public const int MinCleanPerSubmission = 1;

    /// <summary>Also the cap on active (PENDING or CLEAN, not yet bound) candidates of one submission round.</summary>
    public const int MaxPerSubmission = 10;
}
