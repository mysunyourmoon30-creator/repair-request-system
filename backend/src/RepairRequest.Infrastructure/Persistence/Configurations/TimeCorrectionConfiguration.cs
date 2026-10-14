using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>time_correction</c> (RR-DD-001 TC-001..012; `docs/17`). Every foreign key is tenant-consistent, and the exact pause a
/// PAUSE_START/RESUME correction targets must belong to the same session (composite key). The database also enforces what the
/// domain enforces: a pause target exactly for PAUSE_START/RESUME, the decision fields of a decided request, no self-decision, SLA
/// never recalculated, and at most one PENDING request per logical target (session + type + pause) — two filtered unique indexes
/// because the pause part of the target is nullable.
/// </summary>
internal sealed class TimeCorrectionConfiguration : IEntityTypeConfiguration<TimeCorrection>
{
    private const int StatusCodeMaxLength = 30;
    private const int TimestampTypeCodeMaxLength = 30;

    public void Configure(EntityTypeBuilder<TimeCorrection> builder)
    {
        builder.ToTable("time_correction", table =>
        {
            table.HasCheckConstraint("CK_time_correction_status", SqlCheck.In<TimeCorrectionStatus>("status"));
            table.HasCheckConstraint("CK_time_correction_timestamp_type", SqlCheck.In<TimeCorrectionTimestampType>("timestamp_type"));
            table.HasCheckConstraint("CK_time_correction_reason_not_blank", "LEN(LTRIM(RTRIM([reason]))) > 0");
            table.HasCheckConstraint(
                "CK_time_correction_decision_reason_required_on_reject",
                "[status] <> 'REJECTED' OR ([decision_reason] IS NOT NULL AND LEN(LTRIM(RTRIM([decision_reason]))) > 0)");
            table.HasCheckConstraint(
                "CK_time_correction_pause_target",
                "([timestamp_type] IN ('PAUSE_START', 'RESUME') AND [work_session_pause_id] IS NOT NULL) "
                + "OR ([timestamp_type] IN ('CHECK_IN', 'CHECK_OUT') AND [work_session_pause_id] IS NULL)");
            table.HasCheckConstraint(
                "CK_time_correction_decision_fields",
                "([status] = 'PENDING' AND [decided_by] IS NULL AND [decided_at] IS NULL AND [decision_reason] IS NULL) "
                + "OR ([status] <> 'PENDING' AND [decided_by] IS NOT NULL AND [decided_at] IS NOT NULL)");
            table.HasCheckConstraint("CK_time_correction_no_self_decision", "[decided_by] IS NULL OR [decided_by] <> [requested_by]");
            table.HasCheckConstraint("CK_time_correction_sla_not_recalculated", "[sla_recalculated] = 0");
        });

        builder.HasKey(correction => correction.Id);

        builder.Property(correction => correction.Id).HasColumnName("time_correction_id");
        builder.Property(correction => correction.TenantId).HasColumnName("tenant_id");
        builder.Property(correction => correction.WorkSessionId).HasColumnName("work_session_id");
        builder.Property(correction => correction.WorkSessionPauseId).HasColumnName("work_session_pause_id");

        builder.Property(correction => correction.TimestampType)
            .HasColumnName("timestamp_type")
            .HasConversion<UpperSnakeCaseEnumConverter<TimeCorrectionTimestampType>>()
            .HasMaxLength(TimestampTypeCodeMaxLength)
            .IsUnicode(false);

        builder.Property(correction => correction.OldValue).HasColumnName("old_value");
        builder.Property(correction => correction.NewValue).HasColumnName("new_value");

        builder.Property(correction => correction.Reason)
            .HasColumnName("reason")
            .HasMaxLength(TimeCorrection.ReasonMaxLength)
            .IsRequired();

        builder.Property(correction => correction.RequestedBy).HasColumnName("requested_by");

        builder.Property(correction => correction.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<TimeCorrectionStatus>>()
            .HasMaxLength(StatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(correction => correction.DecidedBy).HasColumnName("decided_by");

        builder.Property(correction => correction.DecisionReason)
            .HasColumnName("decision_reason")
            .HasMaxLength(TimeCorrection.ReasonMaxLength);

        builder.Property(correction => correction.DecidedAt).HasColumnName("decided_at");
        builder.Property(correction => correction.SlaRecalculated).HasColumnName("sla_recalculated");

        builder.Property(correction => correction.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();

        // At most one PENDING correction per logical target, enforced atomically by the database. The pause is part of the target
        // only when it exists, and a NULL would make a single index treat every CHECK_IN request as distinct, hence two indexes.
        builder.HasIndex(correction => new { correction.WorkSessionId, correction.TimestampType })
            .IsUnique()
            .HasFilter("[status] = 'PENDING' AND [work_session_pause_id] IS NULL")
            .HasDatabaseName("UQ_time_correction_pending_session_type");

        builder.HasIndex(correction => new { correction.WorkSessionId, correction.TimestampType, correction.WorkSessionPauseId })
            .IsUnique()
            .HasFilter("[status] = 'PENDING' AND [work_session_pause_id] IS NOT NULL")
            .HasDatabaseName("UQ_time_correction_pending_session_type_pause");

        // Supervisor's pending list (tenant + status) and per-session history.
        builder.HasIndex(correction => new { correction.TenantId, correction.Status }).HasDatabaseName("IX_time_correction_tenant_id_status");

        builder.HasOne<WorkSession>()
            .WithMany()
            .HasForeignKey(correction => new { correction.TenantId, correction.WorkSessionId })
            .HasPrincipalKey(session => new { session.TenantId, session.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_time_correction_work_session");

        // The named pause must be a pause row of the same tenant AND the same session. A NULL pause id (CHECK_IN/CHECK_OUT) skips the key.
        builder.HasOne<WorkSessionPause>()
            .WithMany()
            .HasForeignKey(correction => new { correction.TenantId, correction.WorkSessionId, correction.WorkSessionPauseId })
            .HasPrincipalKey(pause => new { pause.TenantId, pause.WorkSessionId, pause.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_time_correction_work_session_pause");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(correction => new { correction.TenantId, correction.RequestedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_time_correction_requested_by_user");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(correction => new { correction.TenantId, correction.DecidedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_time_correction_decided_by_user");
    }
}
