using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        MasterDataMapping.MapCommon(builder, "customer", "customer_id");

        builder.Property(customer => customer.CustomerCode)
            .HasColumnName("customer_code")
            .HasMaxLength(MasterDataEntity.CodeMaxLength)
            .IsUnicode(false);

        // Principal key for tenant-consistent composite foreign keys from site.
        builder.HasAlternateKey(customer => new { customer.TenantId, customer.Id })
            .HasName("AK_customer_tenant_id_customer_id");

        // DEC-PS1-015: customer_code unique within Tenant.
        builder.HasIndex(customer => new { customer.TenantId, customer.CustomerCode })
            .IsUnique()
            .HasDatabaseName("UQ_customer_tenant_id_customer_code");
    }
}
