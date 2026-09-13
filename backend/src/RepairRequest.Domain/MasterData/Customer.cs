using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.MasterData;

/// <summary>
/// Customer master (DEC-PS1-001). Belongs to a Tenant; customer_code is unique
/// within the Tenant (DEC-PS1-015).
/// </summary>
public sealed class Customer : MasterDataEntity
{
    private Customer()
    {
        CustomerCode = null!;
    }

    public Customer(Guid tenantId, string customerCode)
        : base(tenantId)
    {
        CustomerCode = DomainGuard.RequiredText(customerCode, CodeMaxLength, nameof(customerCode));
    }

    public string CustomerCode { get; private set; }
}
