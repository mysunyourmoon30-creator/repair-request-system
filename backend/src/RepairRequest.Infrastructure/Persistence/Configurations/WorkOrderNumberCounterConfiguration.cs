using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Infrastructure.WorkOrders;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>Work Order No. sequence per tenant and UTC year (S2-002): primary key (tenant_id, work_order_year).</summary>
internal sealed class WorkOrderNumberCounterConfiguration : IEntityTypeConfiguration<WorkOrderNumberCounter>
{
    public void Configure(EntityTypeBuilder<WorkOrderNumberCounter> builder)
    {
        builder.ToTable("work_order_no_counter", table =>
        {
            table.HasCheckConstraint("CK_work_order_no_counter_last_value", "[last_value] >= 0");
            table.HasCheckConstraint("CK_work_order_no_counter_work_order_year", "[work_order_year] BETWEEN 1 AND 9999");
        });

        builder.HasKey(counter => new { counter.TenantId, counter.WorkOrderYear });

        builder.Property(counter => counter.TenantId).HasColumnName("tenant_id");
        builder.Property(counter => counter.WorkOrderYear).HasColumnName("work_order_year");
        builder.Property(counter => counter.LastValue).HasColumnName("last_value");
    }
}
