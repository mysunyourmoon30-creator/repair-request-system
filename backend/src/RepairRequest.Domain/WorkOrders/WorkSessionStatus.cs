namespace RepairRequest.Domain.WorkOrders;

/// <summary>Work Session lifecycle states (RR-STS-001 section 1; ST-WS-001..004; RR-DD-001 WS-005).</summary>
public enum WorkSessionStatus
{
    CheckedIn,
    Paused,
    CheckedOut
}
