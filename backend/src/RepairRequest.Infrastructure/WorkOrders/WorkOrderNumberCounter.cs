namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>
/// Per-tenant, per-UTC-year Work Order No. sequence (S2-002, mirrors <c>RequestNumberCounter</c>). Mapped for the
/// schema only: rows are created and incremented exclusively by the atomic MERGE in
/// <see cref="RepairRequestConvertStore"/> inside the Convert transaction.
/// </summary>
public sealed class WorkOrderNumberCounter
{
    private WorkOrderNumberCounter()
    {
    }

    public Guid TenantId { get; private set; }

    public short WorkOrderYear { get; private set; }

    public int LastValue { get; private set; }
}
