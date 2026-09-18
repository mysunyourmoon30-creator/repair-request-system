namespace RepairRequest.Domain.WorkOrders;

/// <summary>D-15 Missed Decision outcomes (RR-DD-001 SV-017; ST-SV-009).</summary>
public enum MissedVisitDecisionCode
{
    Reschedule,
    FollowUp,
    Reassign,
    NoFollowUp
}
