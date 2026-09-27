using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class CustomerAcceptanceConfiguration : IEntityTypeConfiguration<CustomerAcceptance>
{
    private const int DecisionMaxLength = 20;

    public void Configure(EntityTypeBuilder<CustomerAcceptance> builder)
    {
        builder.ToTable("customer_acceptance", table =>
        {
            table.HasCheckConstraint("CK_customer_acceptance_decision", SqlCheck.In<AcceptanceDecision>("decision"));
        });

        builder.HasKey(acceptance => acceptance.Id);

        builder.Property(acceptance => acceptance.Id).HasColumnName("acceptance_id");
        builder.Property(acceptance => acceptance.TenantId).HasColumnName("tenant_id");
        builder.Property(acceptance => acceptance.WorkOrderId).HasColumnName("work_order_id");
        builder.Property(acceptance => acceptance.AcceptanceRoundNo).HasColumnName("acceptance_round_no").HasColumnType("smallint");
        builder.Property(acceptance => acceptance.AcceptanceContactId).HasColumnName("acceptance_contact_id");

        builder.Property(acceptance => acceptance.Decision)
            .HasColumnName("decision")
            .HasConversion<UpperSnakeCaseEnumConverter<AcceptanceDecision>>()
            .HasMaxLength(DecisionMaxLength)
            .IsUnicode(false);

        builder.Property(acceptance => acceptance.DecisionReason)
            .HasColumnName("decision_reason")
            .HasMaxLength(CustomerAcceptance.DecisionReasonMaxLength);

        builder.Property(acceptance => acceptance.DecidedAt).HasColumnName("decided_at");

        // ACC-004 "Unique per WO"; docs/08 §3 "UNIQUE(customer_acceptance.work_order_id, acceptance_round_no)".
        builder.HasIndex(acceptance => new { acceptance.WorkOrderId, acceptance.AcceptanceRoundNo })
            .IsUnique()
            .HasDatabaseName("UQ_customer_acceptance_work_order_id_round_no");

        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(acceptance => acceptance.WorkOrderId)
            .HasPrincipalKey(workOrder => workOrder.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_customer_acceptance_work_order");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(acceptance => new { acceptance.TenantId, acceptance.AcceptanceContactId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_customer_acceptance_contact_user");
    }
}
