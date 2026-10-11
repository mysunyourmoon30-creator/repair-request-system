namespace RepairRequest.Domain.Sla;

/// <summary>
/// Why an SLA record stopped (`docs/16` D6). Persisted as the exact upper-snake-case codes
/// <c>REQUEST_REJECTED</c>, <c>REQUEST_CANCELLED</c>, <c>WO_CLOSED</c> and <c>WO_CANCELLED</c>.
/// </summary>
public enum SlaStopReason
{
    RequestRejected,
    RequestCancelled,
    WoClosed,
    WoCancelled
}
