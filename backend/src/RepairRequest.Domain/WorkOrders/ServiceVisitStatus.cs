namespace RepairRequest.Domain.WorkOrders;

/// <summary>Service Visit lifecycle states (RR-STS-001 section 1; ST-SV-001..009; RR-DD-001 SV-005).</summary>
public enum ServiceVisitStatus
{
    Scheduled,
    Rescheduled,
    InProgress,
    Completed,
    Missed,
    Cancelled
}
