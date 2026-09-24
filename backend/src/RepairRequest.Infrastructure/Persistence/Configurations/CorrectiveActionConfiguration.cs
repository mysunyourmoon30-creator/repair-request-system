using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class CorrectiveActionConfiguration : IEntityTypeConfiguration<CorrectiveAction>
{
    private const int StatusMaxLength = 30;

    public void Configure(EntityTypeBuilder<CorrectiveAction> builder)
    {
        builder.ToTable("corrective_action", table =>
        {
            table.HasCheckConstraint("CK_corrective_action_status", SqlCheck.In<CorrectiveActionStatus>("status"));
        });

        builder.HasKey(action => action.Id);

        builder.Property(action => action.Id).HasColumnName("corrective_action_id");
        builder.Property(action => action.TenantId).HasColumnName("tenant_id");
        builder.Property(action => action.WorkOrderId).HasColumnName("work_order_id");
        builder.Property(action => action.AcceptanceId).HasColumnName("acceptance_id");
        builder.Property(action => action.CycleNo).HasColumnName("cycle_no").HasColumnType("smallint");

        builder.Property(action => action.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<CorrectiveActionStatus>>()
            .HasMaxLength(StatusMaxLength)
            .IsUnicode(false);

        builder.Property(action => action.OwnerTeamLeadId).HasColumnName("owner_team_lead_id");

        builder.Property(action => action.PlanText)
            .HasColumnName("plan_text")
            .HasMaxLength(CorrectiveAction.PlanTextMaxLength);

        builder.Property(action => action.PlanFileAssetId).HasColumnName("plan_file_asset_id");
        builder.Property(action => action.ApprovedBy).HasColumnName("approved_by");
        builder.Property(action => action.ApprovedAt).HasColumnName("approved_at");
        builder.Property(action => action.CorrectiveServiceVisitId).HasColumnName("corrective_service_visit_id");

        // CA-005 "Unique per WO"; docs/08 §3 "UNIQUE(corrective_action.work_order_id, cycle_no)".
        builder.HasIndex(action => new { action.WorkOrderId, action.CycleNo })
            .IsUnique()
            .HasDatabaseName("UQ_corrective_action_work_order_id_cycle_no");

        // docs/07 §3 "CustomerAcceptance 1:0..1 CorrectiveAction".
        builder.HasIndex(action => action.AcceptanceId)
            .IsUnique()
            .HasDatabaseName("UQ_corrective_action_acceptance_id");

        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(action => action.WorkOrderId)
            .HasPrincipalKey(workOrder => workOrder.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_corrective_action_work_order");

        builder.HasOne<CustomerAcceptance>()
            .WithMany()
            .HasForeignKey(action => action.AcceptanceId)
            .HasPrincipalKey(acceptance => acceptance.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_corrective_action_acceptance");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(action => new { action.TenantId, action.OwnerTeamLeadId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_corrective_action_owner_team_lead_user");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(action => new { action.TenantId, action.ApprovedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_corrective_action_approved_by_user");

        // CA-012 "FK corrective Visit" — nullable, unset in this ticket's scope (see class remarks).
        builder.HasOne<ServiceVisit>()
            .WithMany()
            .HasForeignKey(action => action.CorrectiveServiceVisitId)
            .HasPrincipalKey(visit => visit.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_corrective_action_corrective_service_visit");
    }
}
