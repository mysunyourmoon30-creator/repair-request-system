using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class RepairRequestAttachmentConfiguration : IEntityTypeConfiguration<RepairRequestAttachment>
{
    public void Configure(EntityTypeBuilder<RepairRequestAttachment> builder)
    {
        // RR-DBD-001 section 2 table name: request_attachment.
        builder.ToTable("request_attachment");

        builder.HasKey(attachment => attachment.Id);

        builder.Property(attachment => attachment.Id).HasColumnName("attachment_id");
        builder.Property(attachment => attachment.RepairRequestId).HasColumnName("repair_request_id");
        builder.Property(attachment => attachment.FileAssetId).HasColumnName("file_asset_id");

        builder.Property(attachment => attachment.AccessScopeCode)
            .HasColumnName("access_scope_code")
            .HasMaxLength(RepairRequestAttachment.AccessScopeCodeMaxLength)
            .IsUnicode(false);

        // RR-ERD-001: RepairRequest 1:N RequestAttachment. Foreign-key indexes are created by convention.
        builder.HasOne<RepairRequestAggregate>()
            .WithMany()
            .HasForeignKey(attachment => attachment.RepairRequestId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_request_attachment_repair_request");

        builder.HasOne<FileAsset>()
            .WithMany()
            .HasForeignKey(attachment => attachment.FileAssetId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_request_attachment_file_asset");
    }
}
