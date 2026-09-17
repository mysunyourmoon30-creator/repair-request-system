namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// Per-tenant, per-UTC-year Request No sequence (DEC-PRE-S1-007-11). Mapped for the schema only: rows are created and
/// incremented exclusively by the atomic MERGE in <see cref="RepairRequestSubmitStore"/> inside the Submit transaction.
/// </summary>
public sealed class RequestNumberCounter
{
    private RequestNumberCounter()
    {
    }

    public Guid TenantId { get; private set; }

    public short RequestYear { get; private set; }

    public int LastValue { get; private set; }
}
