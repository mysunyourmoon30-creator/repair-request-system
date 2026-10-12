using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>work_evidence_upload</c> (`docs/13` §4.28): evidence files uploaded for the next Work Summary round of a Work Order, anchored
/// to the Service Visit whose summary they are prepared for. Every foreign key is tenant-consistent: the Work Order and the file
/// must carry the row's own tenant, and the Visit must belong to the Work Order. One file asset is one evidence item.
/// </summary>
internal sealed class WorkEvidenceUploadConfiguration : IEntityTypeConfiguration<WorkEvidenceUpload>
{
    private const int EvidenceTypeMaxLength = 30;

    public void Configure(EntityTypeBuilder<WorkEvidenceUpload> builder)
    {
        builder.ToTable("work_evidence_upload", table =>
        {
            table.HasCheckConstraint("CK_work_evidence_upload_evidence_type", SqlCheck.In<WorkEvidenceType>("evidence_type_code"));
        });

        builder.HasKey(upload => upload.Id);

        builder.Property(upload => upload.Id).HasColumnName("work_evidence_id").ValueGeneratedNever();
        builder.Property(upload => upload.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(upload => upload.WorkOrderId).HasColumnName("work_order_id").IsRequired();
        builder.Property(upload => upload.ServiceVisitId).HasColumnName("service_visit_id").IsRequired();
        builder.Property(upload => upload.FileAssetId).HasColumnName("file_asset_id").IsRequired();

        builder.Property(upload => upload.EvidenceType)
            .HasColumnName("evidence_type_code")
            .HasConversion<UpperSnakeCaseEnumConverter<WorkEvidenceType>>()
            .HasMaxLength(EvidenceTypeMaxLength)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(upload => upload.CreatedAt).HasColumnName("created_at").IsRequired();

        // Principal key of everything a bound snapshot row must agree with: the upload itself, its tenant, Work Order, Visit, file and type.
        // work_summary_evidence references it with one composite foreign key, so the database refuses a snapshot built from an unrelated upload.
        builder.HasAlternateKey(upload => new
        {
            upload.Id,
            upload.TenantId,
            upload.WorkOrderId,
            upload.ServiceVisitId,
            upload.FileAssetId,
            upload.EvidenceType
        }).HasName("AK_work_evidence_upload_snapshot");

        // Strictly increasing upload order, assigned by the database: identical timestamps (or random GUID order) never reorder slots.
        builder.Property(upload => upload.UploadSequence)
            .HasColumnName("upload_seq")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn();

        // One file asset is exactly one evidence item.
        builder.HasIndex(upload => upload.FileAssetId)
            .IsUnique()
            .HasDatabaseName("UQ_work_evidence_upload_file_asset_id");

        // Candidate count, list and Submit selection of one round: tenant is enforced by the composite foreign key.
        builder.HasIndex(upload => new { upload.WorkOrderId, upload.ServiceVisitId, upload.UploadSequence })
            .HasDatabaseName("IX_work_evidence_upload_work_order_visit");

        builder.HasOne<WorkOrder>()
            .WithMany()
            .HasForeignKey(upload => new { upload.TenantId, upload.WorkOrderId })
            .HasPrincipalKey(workOrder => new { workOrder.TenantId, workOrder.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_evidence_upload_work_order");

        builder.HasOne<ServiceVisit>()
            .WithMany()
            .HasForeignKey(upload => new { upload.WorkOrderId, upload.ServiceVisitId })
            .HasPrincipalKey(visit => new { visit.WorkOrderId, visit.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_evidence_upload_service_visit");

        builder.HasOne<FileAsset>()
            .WithMany()
            .HasForeignKey(upload => new { upload.TenantId, upload.FileAssetId })
            .HasPrincipalKey(file => new { file.TenantId, file.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_evidence_upload_file_asset");
    }
}

/// <summary>
/// <c>work_summary_evidence</c> (baseline EVD-001/003/004/013): the immutable snapshot of the CLEAN evidence bound to one submitted
/// Work Summary. The primary key is the bound upload's id (so each upload is bound at most once) and <c>slot_no</c> 1..10 unique per
/// summary makes the maximum of ten a database rule. Two composite foreign keys make the snapshot self-proving: one to the Work Summary
/// round (tenant, Work Order, Visit, summary) and one to the original upload (tenant, Work Order, Visit, file and evidence type must all
/// equal the upload's), so no row can freeze an unrelated upload or another round's file. Rows are insert-only.
/// </summary>
internal sealed class WorkSummaryEvidenceConfiguration : IEntityTypeConfiguration<WorkSummaryEvidence>
{
    private const int EvidenceTypeMaxLength = 30;

    public void Configure(EntityTypeBuilder<WorkSummaryEvidence> builder)
    {
        builder.ToTable("work_summary_evidence", table =>
        {
            table.HasCheckConstraint("CK_work_summary_evidence_evidence_type", SqlCheck.In<WorkEvidenceType>("evidence_type_code"));
            table.HasCheckConstraint(
                "CK_work_summary_evidence_slot_no",
                $"[slot_no] BETWEEN 1 AND {WorkEvidenceRules.MaxPerSubmission}");
        });

        builder.HasKey(evidence => evidence.Id);

        builder.Property(evidence => evidence.Id).HasColumnName("evidence_id").ValueGeneratedNever();
        builder.Property(evidence => evidence.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(evidence => evidence.WorkOrderId).HasColumnName("work_order_id").IsRequired();
        builder.Property(evidence => evidence.ServiceVisitId).HasColumnName("service_visit_id").IsRequired();
        builder.Property(evidence => evidence.WorkSummaryId).HasColumnName("work_summary_id").IsRequired();

        builder.Property(evidence => evidence.EvidenceType)
            .HasColumnName("evidence_type_code")
            .HasConversion<UpperSnakeCaseEnumConverter<WorkEvidenceType>>()
            .HasMaxLength(EvidenceTypeMaxLength)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(evidence => evidence.FileAssetId).HasColumnName("file_asset_id").IsRequired();
        builder.Property(evidence => evidence.SlotNo).HasColumnName("slot_no").HasColumnType("tinyint").IsRequired();
        builder.Property(evidence => evidence.BoundAt).HasColumnName("bound_at").IsRequired();

        builder.HasIndex(evidence => new { evidence.WorkSummaryId, evidence.SlotNo })
            .IsUnique()
            .HasDatabaseName("UQ_work_summary_evidence_work_summary_id_slot_no");

        // Named supports of the two composite foreign keys below (parent-side restrict checks); short names instead of generated ones.
        builder.HasIndex(evidence => new { evidence.WorkSummaryId, evidence.TenantId, evidence.WorkOrderId, evidence.ServiceVisitId })
            .HasDatabaseName("IX_work_summary_evidence_round");

        builder.HasIndex(evidence => new
        {
            evidence.Id,
            evidence.TenantId,
            evidence.WorkOrderId,
            evidence.ServiceVisitId,
            evidence.FileAssetId,
            evidence.EvidenceType
        }).HasDatabaseName("IX_work_summary_evidence_upload");

        // The Work Summary round: the summary must belong to the same tenant, Work Order and Visit as the snapshot row.
        builder.HasOne<WorkSummary>()
            .WithMany()
            .HasForeignKey(evidence => new { evidence.WorkSummaryId, evidence.TenantId, evidence.WorkOrderId, evidence.ServiceVisitId })
            .HasPrincipalKey(summary => new { summary.Id, summary.TenantId, summary.WorkOrderId, summary.ServiceVisitId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_summary_evidence_work_summary");

        // The original upload: same id, tenant, Work Order, Visit, file and evidence type. The file (and its tenant) is therefore the
        // upload's own file, which the upload's foreign key already ties to a real file asset of the same tenant.
        builder.HasOne<WorkEvidenceUpload>()
            .WithMany()
            .HasForeignKey(evidence => new
            {
                evidence.Id,
                evidence.TenantId,
                evidence.WorkOrderId,
                evidence.ServiceVisitId,
                evidence.FileAssetId,
                evidence.EvidenceType
            })
            .HasPrincipalKey(upload => new
            {
                upload.Id,
                upload.TenantId,
                upload.WorkOrderId,
                upload.ServiceVisitId,
                upload.FileAssetId,
                upload.EvidenceType
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_work_summary_evidence_upload");
    }
}
