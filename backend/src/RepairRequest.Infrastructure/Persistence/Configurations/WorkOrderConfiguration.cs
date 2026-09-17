using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    private const int StatusCodeMaxLength = 30;

    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("work_order", table =>
        {
            table.HasCheckConstraint("CK_work_order_status", SqlCheck.In<WorkOrderStatus>("status"));
        });

        builder.HasKey(workOrder => workOrder.Id);

        builder.Property(workOrder => workOrder.Id).HasColumnName("work_order_id");
        builder.Property(workOrder => workOrder.TenantId).HasColumnName("tenant_id");

        builder.Property(workOrder => workOrder.WorkOrderNo)
            .HasColumnName("work_order_no")
            .HasMaxLength(WorkOrder.WorkOrderNoMaxLength)
            .IsUnicode(false);

        builder.Property(workOrder => workOrder.RepairRequestId).HasColumnName("repair_request_id");

        builder.Property(workOrder => workOrder.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<WorkOrderStatus>>()
            .HasMaxLength(StatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(workOrder => workOrder.OwnerTeamId).HasColumnName("owner_team_id");
        builder.Property(workOrder => workOrder.TeamLeadId).HasColumnName("team_lead_id");
        builder.Property(workOrder => workOrder.AcceptanceContactId).HasColumnName("acceptance_contact_id");

        builder.Property(workOrder => workOrder.AcceptanceContactSnapshot)
            .HasColumnName("acceptance_contact_snapshot")
            .HasColumnType("nvarchar(max)");

        builder.Property(workOrder => workOrder.CancelReason)
            .HasColumnName("cancel_reason")
            .HasMaxLength(WorkOrder.ReasonMaxLength);

        builder.Property(workOrder => workOrder.ClosedBy).HasColumnName("closed_by");
        builder.Property(workOrder => workOrder.ClosedAt).HasColumnName("closed_at");

        builder.Property(workOrder => workOrder.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        builder.Property(workOrder => workOrder.CreatedAt).HasColumnName("created_at");

        // WO-004 / BR-03: exactly one Work Order per Repair Request.
        builder.HasIndex(workOrder => workOrder.RepairRequestId)
            .IsUnique()
            .HasDatabaseName("UQ_work_order_repair_request_id");

        builder.HasIndex(workOrder => new { workOrder.TenantId, workOrder.WorkOrderNo })
            .IsUnique()
            .HasDatabaseName("UQ_work_order_tenant_id_work_order_no");

        // Candidate list-query index (no baseline-named index exists for Work Order List, docs/08 section 5).
        builder.HasIndex(workOrder => new { workOrder.TenantId, workOrder.Status, workOrder.CreatedAt })
            .HasDatabaseName("IX_work_order_list_search");

        builder.HasOne<RepairRequestAggregate>()
            .WithMany()
            .HasForeignKey(workOrder => workOrder.RepairRequestId)
            .HasPrincipalKey(request => request.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_order_repair_request");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(workOrder => new { workOrder.TenantId, workOrder.TeamLeadId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_order_team_lead_user");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(workOrder => new { workOrder.TenantId, workOrder.AcceptanceContactId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_order_acceptance_contact_user");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(workOrder => new { workOrder.TenantId, workOrder.ClosedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_order_closed_by_user");
    }
}
