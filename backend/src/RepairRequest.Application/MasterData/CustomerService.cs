using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>
/// Customer master use cases (DEC-PS1-001/013/014/015; S1-004 decisions D1, D3). Role capability is enforced by
/// the API policy before these run; every lookup here is tenant/site scoped through <see cref="IMasterDataStore"/>.
/// </summary>
public sealed class CustomerService
{
    private readonly IMasterDataStore _store;
    private readonly TimeProvider _clock;

    public CustomerService(IMasterDataStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<PagedResult<CustomerDto>> ListAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken) =>
        _store.ListCustomersAsync(user, query, cancellationToken);

    public Task<CustomerDto?> GetAsync(CurrentUser user, Guid customerId, CancellationToken cancellationToken) =>
        _store.GetCustomerAsync(user, customerId, cancellationToken);

    public async Task<CommandResult<CustomerDto>> CreateAsync(CommandContext context, string? customerCode, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var code = MasterDataValidation.Code(customerCode, MasterDataFields.CustomerCode, errors);
        if (code is null)
        {
            return CommandError.Validation(errors);
        }

        // Tenant is always the caller's database-resolved tenant (D-11 / BR-16), never client input.
        var tenantId = context.User.TenantId;
        if (await _store.CustomerCodeExistsAsync(tenantId, code, Guid.Empty, cancellationToken))
        {
            return MasterDataCommandSteps.DuplicateCode(MasterDataFields.CustomerCode);
        }

        var customer = new Customer(tenantId, code);
        _store.Add(customer);
        _store.AddAudit(MasterDataAudit.Created(
            context,
            customer,
            new Dictionary<string, object?> { [MasterDataFields.CustomerCode] = code },
            MasterDataCommandSteps.UtcNow(_clock)));

        return await MasterDataCommandSteps.SaveAsync(_store, customer, null, MasterDataFields.CustomerCode, ToDto, cancellationToken);
    }

    public async Task<CommandResult<CustomerDto>> UpdateAsync(
        CommandContext context,
        Guid customerId,
        byte[] expectedRowVersion,
        string? customerCode,
        CancellationToken cancellationToken)
    {
        var customer = await _store.FindCustomerAsync(context.User, customerId, cancellationToken);

        return await MasterDataCommandSteps.UpdateCodeAsync(
            _store,
            _clock,
            context,
            customer,
            expectedRowVersion,
            customerCode,
            MasterDataFields.CustomerCode,
            entity => entity.CustomerCode,
            (entity, code) => entity.ChangeCode(code),
            (entity, code) => _store.CustomerCodeExistsAsync(entity.TenantId, code, entity.Id, cancellationToken),
            ToDto,
            cancellationToken);
    }

    public async Task<CommandResult<CustomerDto>> ActivateAsync(
        CommandContext context,
        Guid customerId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var customer = await _store.FindCustomerAsync(context.User, customerId, cancellationToken);

        return await MasterDataCommandSteps.ActivateAsync(
            _store, _clock, context, customer, expectedRowVersion, null, null, null, ToDto, cancellationToken);
    }

    /// <summary>DEC-PS1-013: denied while an active Site exists; guard and change share one SERIALIZABLE transaction.</summary>
    public Task<CommandResult<CustomerDto>> DeactivateAsync(
        CommandContext context,
        Guid customerId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken) =>
        _store.RunSerializableAsync<CustomerDto>(
            async () =>
            {
                var customer = await _store.FindCustomerAsync(context.User, customerId, cancellationToken);

                return await MasterDataCommandSteps.DeactivateAsync(
                    _store,
                    _clock,
                    context,
                    customer,
                    expectedRowVersion,
                    reason,
                    entity => _store.CountActiveSitesAsync(entity.TenantId, entity.Id, cancellationToken),
                    "Site(s)",
                    ToDto,
                    cancellationToken);
            },
            cancellationToken);

    private static CustomerDto ToDto(Customer customer) =>
        new(customer.Id, customer.CustomerCode, customer.Status, customer.DeactivateReason, customer.RowVersion);
}
