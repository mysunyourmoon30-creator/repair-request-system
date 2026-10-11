using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.Sla;
using RepairRequest.Infrastructure.Persistence.Conversions;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>sla_record</c> (`docs/16` D5-D7; baseline SLA-001..012): one row per Repair Request, written once at the first Submit and
/// stopped at most once. There is no RowVersion: the start is guarded by the first-Submit rule and the unique request index, the
/// stop is an atomic <c>stopped_at IS NULL</c> conditional update, and no client mutation API exists. The time rules are
/// enforced by the database as well as by the domain.
/// </summary>
internal sealed class SlaRecordConfiguration : IEntityTypeConfiguration<SlaRecord>
{
    private const int StopReasonMaxLength = 30;

    public void Configure(EntityTypeBuilder<SlaRecord> builder)
    {
        builder.ToTable("sla_record", table =>
        {
            // SLA-004 policy v1: due = start + 24 h; risk = due - 4 h (24/7 elapsed UTC time).
            table.HasCheckConstraint("CK_sla_record_due_after_start", "[original_due_at] = DATEADD(hour, 24, [started_at])");
            table.HasCheckConstraint("CK_sla_record_risk_before_due", "[risk_at] = DATEADD(hour, -4, [original_due_at])");

            // A stop never precedes the start; stopped_at and stop_reason are set together or not at all.
            table.HasCheckConstraint("CK_sla_record_stopped_after_start", "[stopped_at] IS NULL OR [stopped_at] >= [started_at]");
            table.HasCheckConstraint(
                "CK_sla_record_stop_pair",
                "([stopped_at] IS NULL AND [stop_reason] IS NULL) OR ([stopped_at] IS NOT NULL AND [stop_reason] IS NOT NULL)");
            table.HasCheckConstraint("CK_sla_record_stop_reason", $"[stop_reason] IS NULL OR {SqlCheck.In<SlaStopReason>("stop_reason")}");
        });

        builder.HasKey(record => record.Id);

        builder.Property(record => record.Id).HasColumnName("sla_record_id").ValueGeneratedNever();
        builder.Property(record => record.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(record => record.RepairRequestId).HasColumnName("repair_request_id").IsRequired();

        builder.Property(record => record.SlaPolicyVersion)
            .HasColumnName("sla_policy_version")
            .HasMaxLength(SlaPolicy.VersionMaxLength)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(record => record.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(record => record.OriginalDueAt).HasColumnName("original_due_at").IsRequired();
        builder.Property(record => record.RiskAt).HasColumnName("risk_at").IsRequired();
        builder.Property(record => record.StoppedAt).HasColumnName("stopped_at");

        builder.Property(record => record.StopReason)
            .HasColumnName("stop_reason")
            .HasConversion<UpperSnakeCaseEnumConverter<SlaStopReason>>()
            .HasMaxLength(StopReasonMaxLength)
            .IsUnicode(false);

        // At most one SLA record per Repair Request; also the lookup and conditional-update seek path.
        builder.HasIndex(record => record.RepairRequestId)
            .IsUnique()
            .HasDatabaseName("UQ_sla_record_repair_request_id");

        // The record's tenant must be the tenant of the Repair Request it references (tenant-consistent composite FK).
        builder.HasOne<RepairRequestAggregate>()
            .WithMany()
            .HasForeignKey(record => new { record.TenantId, record.RepairRequestId })
            .HasPrincipalKey(request => new { request.TenantId, request.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_sla_record_repair_request");
    }
}
