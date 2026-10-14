using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Time Correction projection (RR-DD-001 TC-001..012; UC-TIME-001; `docs/17`): one request, its Work Session / Visit / Work
/// Order summary, the exact pause it targets (PAUSE_START/RESUME only) and its decision, if any. Flat, so it serves the
/// Request/Approve/Reject responses and the Supervisor's pending list alike.
/// </summary>
public sealed record TimeCorrectionDto(
    Guid TimeCorrectionId,
    Guid WorkSessionId,
    Guid? WorkSessionPauseId,
    Guid ServiceVisitId,
    Guid WorkOrderId,
    string WorkOrderNo,
    string? SiteCode,
    string? EquipmentCode,
    TimeCorrectionTimestampType TimestampType,
    DateTime OldValue,
    DateTime NewValue,
    string Reason,
    Guid RequestedBy,
    TimeCorrectionStatus Status,
    Guid? DecidedBy,
    string? DecisionReason,
    DateTime? DecidedAt,
    byte[] RowVersion);

/// <summary>Pending Time Corrections list query: paging only — always the caller's own Site-scoped PENDING requests, oldest first.</summary>
public sealed record PendingTimeCorrectionsQuery(PageRequest Paging);

/// <summary>What a Request needs, read fresh under the Work Order lock: the (untracked) session, all its pause rows and the Work Order status.</summary>
public sealed record TimeCorrectionRequestContext(WorkSession Session, IReadOnlyList<WorkSessionPause> Pauses, WorkOrderStatus WorkOrderStatus);

/// <summary>
/// What an Approve needs, all loaded and locked in one transaction: the Correction, the tracked Work Session, its tracked pause
/// rows, the tracked Visit, how many sessions the Visit has, and the Work Order status read under the Work Order lock.
/// </summary>
public sealed record TimeCorrectionApprovalContext(
    TimeCorrection Correction,
    WorkSession Session,
    IReadOnlyList<WorkSessionPause> Pauses,
    ServiceVisit Visit,
    int VisitSessionCount,
    WorkOrderStatus WorkOrderStatus);

/// <summary>Outcome of inserting a new Request: the unique filtered index of (session, type, pause) rejects a second PENDING request for the same target.</summary>
public enum TimeCorrectionInsertOutcome
{
    Saved,
    DuplicatePending
}

/// <summary>Canonical upper-snake Time Correction status codes (RR-DD-001 TC-008; RR-STS-001 section 1).</summary>
public static class TimeCorrectionStatusCodes
{
    public static string ToCode(TimeCorrectionStatus status) => status switch
    {
        TimeCorrectionStatus.Pending => "PENDING",
        TimeCorrectionStatus.Approved => "APPROVED",
        TimeCorrectionStatus.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}

/// <summary>Canonical upper-snake Time Correction timestamp-type codes (RR-DD-001 TC-003).</summary>
public static class TimeCorrectionTimestampTypeCodes
{
    public static string ToCode(TimeCorrectionTimestampType type) => type switch
    {
        TimeCorrectionTimestampType.CheckIn => "CHECK_IN",
        TimeCorrectionTimestampType.PauseStart => "PAUSE_START",
        TimeCorrectionTimestampType.Resume => "RESUME",
        TimeCorrectionTimestampType.CheckOut => "CHECK_OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static bool TryParse(string? code, out TimeCorrectionTimestampType type)
    {
        switch (code)
        {
            case "CHECK_IN": type = TimeCorrectionTimestampType.CheckIn; return true;
            case "PAUSE_START": type = TimeCorrectionTimestampType.PauseStart; return true;
            case "RESUME": type = TimeCorrectionTimestampType.Resume; return true;
            case "CHECK_OUT": type = TimeCorrectionTimestampType.CheckOut; return true;
            default: type = default; return false;
        }
    }
}

/// <summary>API field names used as keys of Time Correction actions' 422 errors.</summary>
public static class TimeCorrectionFields
{
    public const string TimestampType = "timestampType";
    public const string WorkSessionPauseId = "workSessionPauseId";
    public const string NewValue = "newValue";
    public const string Reason = "reason";
    public const string DecisionReason = "decisionReason";
}
