using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class CostSummaryConfiguration : IEntityTypeConfiguration<CostSummary>
{
    public void Configure(EntityTypeBuilder<CostSummary> builder)
    {
        builder.ToTable("cost_summary");

        builder.HasKey(summary => summary.Id);

        builder.Property(summary => summary.Id).HasColumnName("cost_summary_id");
        builder.Property(summary => summary.TenantId).HasColumnName("tenant_id");
        builder.Property(summary => summary.WorkOrderId).HasColumnName("work_order_id");

        builder.Property(summary => summary.TotalAmount)
            .HasColumnName("total_amount")
            .HasColumnType("decimal(18,2)");

        builder.Property(summary => summary.CurrencyCode)
            .HasColumnName("currency_code")
            .HasMaxLength(CostSummary.CurrencyCodeLength)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(summary => summary.Note)
            .HasColumnName("note")
            .HasMaxLength(CostSummary.NoteMaxLength);

        builder.Property(summary => summary.PreparedBy).HasColumnName("prepared_by");
        builder.Property(summary => summary.PreparedAt).HasColumnName("prepared_at");
        builder.Property(summary => summary.ReviewedBy).HasColumnName("reviewed_by");
        builder.Property(summary => summary.ReviewedAt).HasColumnName("reviewed_at");

        builder.Property(summary => summary.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        // CST-003 "unique current summary"; docs/07 ER "WorkOrder 1:0..1 CostSummary".
        builder.HasIndex(summary => summary.WorkOrderId)
            .IsUnique()
            .HasDatabaseName("UQ_cost_summary_work_order_id");

        // `docs/13` §4.19: the Supervisor pending-review queue filters exactly this predicate — a filtered index
        // stays small regardless of table growth, since most rows are eventually reviewed (mirrors the existing
        // IX_work_order_list_search precedent: a candidate list-query index added when a new list is introduced).
        builder.HasIndex(summary => summary.ReviewedAt)
            .HasDatabaseName("IX_cost_summary_pending_review")
            .HasFilter("[reviewed_at] IS NULL");

        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(summary => summary.WorkOrderId)
            .HasPrincipalKey(workOrder => workOrder.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_cost_summary_work_order");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(summary => new { summary.TenantId, summary.PreparedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_cost_summary_prepared_by_user");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(summary => new { summary.TenantId, summary.ReviewedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_cost_summary_reviewed_by_user");
    }
}
