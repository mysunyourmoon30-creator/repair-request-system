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
        CustomerCode = ValidCode(customerCode, nameof(customerCode));
    }

    public string CustomerCode { get; private set; }

    /// <summary>Uniqueness within the Tenant is checked by the Application layer and the database.</summary>
    public void ChangeCode(string customerCode) =>
        CustomerCode = ValidCode(customerCode, nameof(customerCode));
}
