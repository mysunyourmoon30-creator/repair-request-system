namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Closed allowlist of Work Summary repair outcomes (WSM-007; `docs/13` §4.15 Decision — Portfolio Project
/// Owner directive, since RR-DD-001 names this "Active Outcome" but defines no master-data table or value list
/// anywhere in the baseline). Six values only; no seventh value may be added without another Portfolio Project
/// Owner directive.
/// </summary>
public enum RepairOutcomeCode
{
    /// <summary>Repair completed successfully and the equipment is back in service.</summary>
    Repaired,

    /// <summary>A temporary fix was applied; usable but further action is still required.</summary>
    TemporaryFix,

    /// <summary>Work cannot continue until parts arrive.</summary>
    PartsRequired,

    /// <summary>Investigated but the reported fault could not be found or reproduced.</summary>
    NoFaultFound,

    /// <summary>The equipment cannot be repaired.</summary>
    NotRepairable,

    /// <summary>A further appointment or inspection is required.</summary>
    FollowUpRequired
}
