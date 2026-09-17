using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.Auditing;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class AuditHistoryConfiguration : IEntityTypeConfiguration<AuditHistory>
{
    public void Configure(EntityTypeBuilder<AuditHistory> builder)
    {
        builder.ToTable("audit_history", table =>
        {
            table.HasCheckConstraint("CK_audit_history_old_value_json", SqlCheck.NullOrJson("old_value_json"));
            table.HasCheckConstraint("CK_audit_history_new_value_json", SqlCheck.NullOrJson("new_value_json"));
        });

        builder.HasKey(audit => audit.Id);

        builder.Property(audit => audit.Id).HasColumnName("audit_history_id");
        builder.Property(audit => audit.TenantId).HasColumnName("tenant_id");

        builder.Property(audit => audit.EntityType)
            .HasColumnName("entity_type")
            .HasMaxLength(AuditHistory.EntityTypeMaxLength)
            .IsUnicode(false);

        builder.Property(audit => audit.EntityId).HasColumnName("entity_id");

        builder.Property(audit => audit.ActionCode)
            .HasColumnName("action_code")
            .HasMaxLength(AuditHistory.ActionCodeMaxLength)
            .IsUnicode(false);

        builder.Property(audit => audit.FromState)
            .HasColumnName("from_state")
            .HasMaxLength(AuditHistory.StateMaxLength)
            .IsUnicode(false);

        builder.Property(audit => audit.ToState)
            .HasColumnName("to_state")
            .HasMaxLength(AuditHistory.StateMaxLength)
            .IsUnicode(false);

        // RR-DD-001 AUD-008/009 are json documents of unbounded shape; nvarchar(max) with an
        // ISJSON check keeps compatibility with SQL Server LocalDB and Azure SQL.
        builder.Property(audit => audit.OldValueJson).HasColumnName("old_value_json");
        builder.Property(audit => audit.NewValueJson).HasColumnName("new_value_json");

        builder.Property(audit => audit.Reason)
            .HasColumnName("reason")
            .HasMaxLength(AuditHistory.ReasonMaxLength);

        builder.Property(audit => audit.ActorId).HasColumnName("actor_id");
        builder.Property(audit => audit.OccurredAt).HasColumnName("occurred_at");
        builder.Property(audit => audit.CorrelationId).HasColumnName("correlation_id");

        // RR-DBD-001 section 5 "Audit timeline": tenant_id + entity_type + entity_id + occurred_at DESC.
        builder.HasIndex(audit => new { audit.TenantId, audit.EntityType, audit.EntityId, audit.OccurredAt })
            .IsDescending(false, false, false, true)
            .HasDatabaseName("IX_audit_history_timeline");
    }
}
