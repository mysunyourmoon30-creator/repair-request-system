namespace RepairRequest.Domain.Sla;

/// <summary>The exact stop-reason codes stored in <c>sla_record.stop_reason</c> and written to SLA audit documents.</summary>
public static class SlaStopReasonCodes
{
    public const string RequestRejected = "REQUEST_REJECTED";
    public const string RequestCancelled = "REQUEST_CANCELLED";
    public const string WorkOrderClosed = "WO_CLOSED";
    public const string WorkOrderCancelled = "WO_CANCELLED";

    public static string ToCode(SlaStopReason reason) => reason switch
    {
        SlaStopReason.RequestRejected => RequestRejected,
        SlaStopReason.RequestCancelled => RequestCancelled,
        SlaStopReason.WoClosed => WorkOrderClosed,
        SlaStopReason.WoCancelled => WorkOrderCancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
