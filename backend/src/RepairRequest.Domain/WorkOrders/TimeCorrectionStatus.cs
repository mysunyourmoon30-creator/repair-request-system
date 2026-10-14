namespace RepairRequest.Domain.WorkOrders;

/// <summary>Time Correction lifecycle states (RR-STS-001 section 1; ST-TC-001..003; RR-DD-001 TC-008).</summary>
public enum TimeCorrectionStatus
{
    Pending,
    Approved,
    Rejected
}
