using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class WorkSummaryConfiguration : IEntityTypeConfiguration<WorkSummary>
{
    private const int RepairOutcomeCodeMaxLength = 30;

    public void Configure(EntityTypeBuilder<WorkSummary> builder)
    {
        builder.ToTable("work_summary", table =>
        {
            // Closed six-value allowlist (`docs/13` §4.15 Decision, Portfolio Project Owner directive) — the
            // same code list feeds this constraint and the C# converter below, so they cannot drift apart.
            table.HasCheckConstraint("CK_work_summary_repair_outcome_code", SqlCheck.In<RepairOutcomeCode>("repair_outcome_code"));
        });

        builder.HasKey(summary => summary.Id);

        builder.Property(summary => summary.Id).HasColumnName("work_summary_id");
        builder.Property(summary => summary.TenantId).HasColumnName("tenant_id");
        builder.Property(summary => summary.WorkOrderId).HasColumnName("work_order_id");
        builder.Property(summary => summary.ServiceVisitId).HasColumnName("service_visit_id");
        builder.Property(summary => summary.RevisionNo).HasColumnName("revision_no");

        builder.Property(summary => summary.SummaryText)
            .HasColumnName("summary_text")
            .HasMaxLength(WorkSummary.SummaryTextMaxLength);

        builder.Property(summary => summary.RepairOutcomeCode)
            .HasColumnName("repair_outcome_code")
            .HasConversion<UpperSnakeCaseEnumConverter<RepairOutcomeCode>>()
            .HasMaxLength(RepairOutcomeCodeMaxLength)
            .IsUnicode(false);

        builder.Property(summary => summary.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        // WSM-005: unique per Visit revision — always 1 in this ticket's scope, so this also backstops a second
        // concurrent Submit (the domain guard on WorkOrder.Status is the primary defense).
        builder.HasIndex(summary => new { summary.ServiceVisitId, summary.RevisionNo })
            .IsUnique()
            .HasDatabaseName("UQ_work_summary_service_visit_id_revision_no");

        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(summary => summary.WorkOrderId)
            .HasPrincipalKey(workOrder => workOrder.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_summary_work_order");

        builder.HasOne<ServiceVisit>()
            .WithMany()
            .HasForeignKey(summary => summary.ServiceVisitId)
            .HasPrincipalKey(visit => visit.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_summary_service_visit");
    }
}
