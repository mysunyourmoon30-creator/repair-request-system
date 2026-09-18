using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;
using RepairRequest.Infrastructure.WorkOrders;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class ServiceVisitConfiguration : IEntityTypeConfiguration<ServiceVisit>
{
    private const int StatusCodeMaxLength = 30;
    private const int VisitTypeCodeMaxLength = 30;
    private const int MissedDecisionCodeMaxLength = 30;

    public void Configure(EntityTypeBuilder<ServiceVisit> builder)
    {
        builder.ToTable("service_visit", table =>
        {
            table.HasCheckConstraint("CK_service_visit_status", SqlCheck.In<ServiceVisitStatus>("status"));
            table.HasCheckConstraint("CK_service_visit_visit_type", SqlCheck.In<ServiceVisitType>("visit_type"));
            table.HasCheckConstraint(
                "CK_service_visit_missed_decision_code",
                $"[missed_decision_code] IS NULL OR {SqlCheck.In<MissedVisitDecisionCode>("missed_decision_code")}");
        });

        builder.HasKey(visit => visit.Id);

        builder.Property(visit => visit.Id).HasColumnName("service_visit_id");
        builder.Property(visit => visit.TenantId).HasColumnName("tenant_id");
        builder.Property(visit => visit.WorkOrderId).HasColumnName("work_order_id");

        builder.Property(visit => visit.VisitType)
            .HasColumnName("visit_type")
            .HasConversion<UpperSnakeCaseEnumConverter<ServiceVisitType>>()
            .HasMaxLength(VisitTypeCodeMaxLength)
            .IsUnicode(false);

        builder.Property(visit => visit.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<ServiceVisitStatus>>()
            .HasMaxLength(StatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(visit => visit.AssignedTeamId).HasColumnName("assigned_team_id");
        builder.Property(visit => visit.AssignedTechnicianId).HasColumnName("assigned_technician_id");
        builder.Property(visit => visit.ScheduledStartAt).HasColumnName("scheduled_start_at");
        builder.Property(visit => visit.ScheduledEndAt).HasColumnName("scheduled_end_at");

        builder.Property(visit => visit.RescheduleReason)
            .HasColumnName("reschedule_reason")
            .HasMaxLength(ServiceVisit.ReasonMaxLength);

        builder.Property(visit => visit.ReassignReason)
            .HasColumnName("reassign_reason")
            .HasMaxLength(ServiceVisit.ReasonMaxLength);

        builder.Property(visit => visit.CancelReason)
            .HasColumnName("cancel_reason")
            .HasMaxLength(ServiceVisit.ReasonMaxLength);

        builder.Property(visit => visit.MissedReason)
            .HasColumnName("missed_reason")
            .HasMaxLength(ServiceVisit.ReasonMaxLength);

        builder.Property(visit => visit.CompletedAt).HasColumnName("completed_at");

        builder.Property(visit => visit.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        builder.Property(visit => visit.SourceMissedVisitId).HasColumnName("source_missed_visit_id");

        builder.Property(visit => visit.MissedDecisionCode)
            .HasColumnName("missed_decision_code")
            .HasConversion<UpperSnakeCaseEnumConverter<MissedVisitDecisionCode>>()
            .HasMaxLength(MissedDecisionCodeMaxLength)
            .IsUnicode(false);

        builder.Property(visit => visit.MissedDecidedAt).HasColumnName("missed_decided_at");

        // SV-003: plain, non-unique FK — a Work Order has 0..N Service Visits.
        builder.HasIndex(visit => visit.WorkOrderId).HasDatabaseName("IX_service_visit_work_order_id");

        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(visit => visit.WorkOrderId)
            .HasPrincipalKey(workOrder => workOrder.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_service_visit_work_order");

        builder.HasOne<ServiceVisit>()
            .WithMany()
            .HasForeignKey(visit => visit.SourceMissedVisitId)
            .HasPrincipalKey(visit => visit.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_service_visit_source_missed_visit");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(visit => new { visit.TenantId, visit.AssignedTechnicianId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_service_visit_assigned_technician_user");
    }
}
