using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.Files;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class FileAssetConfiguration : IEntityTypeConfiguration<FileAsset>
{
    private const int ScanStatusCodeMaxLength = 30;

    public void Configure(EntityTypeBuilder<FileAsset> builder)
    {
        builder.ToTable("file_asset", table =>
        {
            // RR-DBD-001 section 3: CHECK(file_asset.malware_scan_status IN ('PENDING','CLEAN','FAILED')).
            table.HasCheckConstraint("CK_file_asset_malware_scan_status", SqlCheck.In<MalwareScanStatus>("malware_scan_status"));
            table.HasCheckConstraint("CK_file_asset_size_bytes", "[size_bytes] >= 0");
        });

        builder.HasKey(file => file.Id);

        builder.Property(file => file.Id).HasColumnName("file_asset_id");
        builder.Property(file => file.TenantId).HasColumnName("tenant_id");

        builder.Property(file => file.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(FileAsset.FileNameMaxLength);

        builder.Property(file => file.MimeType)
            .HasColumnName("mime_type")
            .HasMaxLength(FileAsset.MimeTypeMaxLength)
            .IsUnicode(false);

        builder.Property(file => file.SizeBytes).HasColumnName("size_bytes");

        builder.Property(file => file.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(FileAsset.ContentHashLength)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(file => file.StorageReference)
            .HasColumnName("storage_reference")
            .HasMaxLength(FileAsset.StorageReferenceMaxLength);

        builder.Property(file => file.MalwareScanStatus)
            .HasColumnName("malware_scan_status")
            .HasConversion<UpperSnakeCaseEnumConverter<MalwareScanStatus>>()
            .HasMaxLength(ScanStatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(file => file.UploadedBy).HasColumnName("uploaded_by");
        builder.Property(file => file.UploadedAt).HasColumnName("uploaded_at");

        // Uploader must be a user of the file's Tenant.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(file => new { file.TenantId, file.UploadedBy })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_file_asset_uploaded_by_user");
    }
}
