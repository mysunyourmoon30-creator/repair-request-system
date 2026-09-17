namespace RepairRequest.Domain.WorkOrders;

/// <summary>Work Order lifecycle states (RR-STS-001 section 3, ST-WO-001..011).</summary>
public enum WorkOrderStatus
{
    Open,
    Scheduled,
    InProgress,
    AwaitingSupervisorReview,
    AwaitingCustomerAcceptance,
    Completed,
    CorrectiveActionRequired,
    CorrectivePlanPending,
    CorrectivePlanApproved,
    Closed,
    Cancelled
}
