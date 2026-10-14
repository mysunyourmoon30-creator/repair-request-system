namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// The four Work Session timestamps a Time Correction can target (RR-DD-001 TC-003: "CHECK_IN/PAUSE_START/
/// RESUME/CHECK_OUT"). Listed in the Work Session's own lifecycle order, which <see cref="WorkSession"/>'s
/// chronology check relies on.
/// </summary>
public enum TimeCorrectionTimestampType
{
    CheckIn,
    PauseStart,
    Resume,
    CheckOut
}
