namespace RepairRequest.Domain.WorkEvidence;

/// <summary>
/// Owner-approved minimal V1 evidence catalog (`docs/16` OD1; baseline EVD-004 has no value list). Persisted as the exact
/// upper-snake-case codes <c>BEFORE_WORK</c>, <c>AFTER_WORK</c> and <c>OTHER</c>. Any value, including
/// <see cref="Other"/>, may satisfy the CLEAN minimum.
/// </summary>
public enum WorkEvidenceType
{
    BeforeWork,
    AfterWork,
    Other
}

/// <summary>The closed code list of <see cref="WorkEvidenceType"/>: parsing is exact after trimming and upper-casing, never open-ended.</summary>
public static class WorkEvidenceTypeCodes
{
    public const string BeforeWork = "BEFORE_WORK";
    public const string AfterWork = "AFTER_WORK";
    public const string Other = "OTHER";

    public static IReadOnlyList<string> All { get; } = [BeforeWork, AfterWork, Other];

    public static string ToCode(WorkEvidenceType type) => type switch
    {
        WorkEvidenceType.BeforeWork => BeforeWork,
        WorkEvidenceType.AfterWork => AfterWork,
        WorkEvidenceType.Other => Other,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static bool TryParse(string? code, out WorkEvidenceType type)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case BeforeWork: type = WorkEvidenceType.BeforeWork; return true;
            case AfterWork: type = WorkEvidenceType.AfterWork; return true;
            case Other: type = WorkEvidenceType.Other; return true;
            default: type = default; return false;
        }
    }
}
