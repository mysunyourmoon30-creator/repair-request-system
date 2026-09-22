using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class WorkSessionPauseConfiguration : IEntityTypeConfiguration<WorkSessionPause>
{
    public void Configure(EntityTypeBuilder<WorkSessionPause> builder)
    {
        builder.ToTable("work_session_pause", table =>
        {
            // DB backstops behind the domain guards: a reason is never blank, and a period never ends before it starts.
            table.HasCheckConstraint("CK_work_session_pause_reason_not_blank", "LEN(LTRIM(RTRIM([pause_reason]))) > 0");
            table.HasCheckConstraint("CK_work_session_pause_period", "[resumed_at] IS NULL OR [resumed_at] >= [paused_at]");
        });

        builder.HasKey(pause => pause.Id);

        builder.Property(pause => pause.Id).HasColumnName("work_session_pause_id");
        builder.Property(pause => pause.TenantId).HasColumnName("tenant_id");
        builder.Property(pause => pause.WorkSessionId).HasColumnName("work_session_id");
        builder.Property(pause => pause.PausedAt).HasColumnName("paused_at");

        builder.Property(pause => pause.PauseReason)
            .HasColumnName("pause_reason")
            .HasMaxLength(WorkSessionPause.ReasonMaxLength)
            .IsRequired();

        builder.Property(pause => pause.ResumedAt).HasColumnName("resumed_at");

        // Every pause is its own row (history is never replaced). A session has at most one OPEN pause, enforced
        // by the database as the backstop for two truly concurrent Pause requests.
        builder.HasIndex(pause => pause.WorkSessionId)
            .HasFilter("[resumed_at] IS NULL")
            .IsUnique()
            .HasDatabaseName("IX_work_session_pause_open");

        // History reads (a session's pauses, newest first).
        builder.HasIndex(pause => new { pause.WorkSessionId, pause.PausedAt }).HasDatabaseName("IX_work_session_pause_work_session_id_paused_at");

        builder.HasOne<WorkSession>()
            .WithMany()
            .HasForeignKey(pause => pause.WorkSessionId)
            .HasPrincipalKey(session => session.Id)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_session_pause_work_session");
    }
}
