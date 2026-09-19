using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class WorkSessionConfiguration : IEntityTypeConfiguration<WorkSession>
{
    private const int StatusCodeMaxLength = 30;

    public void Configure(EntityTypeBuilder<WorkSession> builder)
    {
        builder.ToTable("work_session", table =>
        {
            table.HasCheckConstraint("CK_work_session_status", SqlCheck.In<WorkSessionStatus>("status"));
        });

        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id).HasColumnName("work_session_id");
        builder.Property(session => session.TenantId).HasColumnName("tenant_id");
        builder.Property(session => session.ServiceVisitId).HasColumnName("service_visit_id");
        builder.Property(session => session.TechnicianId).HasColumnName("technician_id");

        builder.Property(session => session.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<WorkSessionStatus>>()
            .HasMaxLength(StatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(session => session.CheckInAt).HasColumnName("check_in_at");
        builder.Property(session => session.PauseStartAt).HasColumnName("pause_start_at");
        builder.Property(session => session.ResumeAt).HasColumnName("resume_at");
        builder.Property(session => session.CheckOutAt).HasColumnName("check_out_at");

        builder.Property(session => session.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        // WS-003: a Service Visit has at most one active (non-CHECKED_OUT) session at a time — enforced by the
        // store's read-then-write guard for the common path; this index is the DB-level backstop for two truly
        // concurrent Check-in requests (BR-05 "no active overlap"): two requests for the same technician on two
        // different Visits can both pass the read-then-write guard before either commits.
        builder.HasIndex(session => session.ServiceVisitId).HasDatabaseName("IX_work_session_service_visit_id");

        builder.HasIndex(session => new { session.TenantId, session.TechnicianId })
            .HasFilter("[status] <> 'CHECKED_OUT'")
            .IsUnique()
            .HasDatabaseName("IX_work_session_tenant_id_technician_id_active");

        builder.HasOne<ServiceVisit>()
            .WithMany()
            .HasForeignKey(session => session.ServiceVisitId)
            .HasPrincipalKey(visit => visit.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_session_service_visit");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(session => new { session.TenantId, session.TechnicianId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_session_technician_user");
    }
}
