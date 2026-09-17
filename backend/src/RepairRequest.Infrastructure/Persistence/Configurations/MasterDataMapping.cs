using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.MasterData;
using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>Column mapping shared by the Customer / Site / Equipment master tables.</summary>
internal static class MasterDataMapping
{
    public const int StatusCodeMaxLength = 30;

    public static void MapCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string tableName, string idColumnName)
        where TEntity : MasterDataEntity
    {
        builder.ToTable(tableName, table =>
        {
            table.HasCheckConstraint($"CK_{tableName}_status", SqlCheck.In<MasterDataStatus>("status"));

            // DEC-PS1-014: an INACTIVE row must carry a non-blank deactivate_reason.
            table.HasCheckConstraint(
                $"CK_{tableName}_deactivate_reason",
                $"[status] <> '{SqlCheck.Code(MasterDataStatus.Inactive)}' " +
                "OR ([deactivate_reason] IS NOT NULL AND LEN(LTRIM(RTRIM([deactivate_reason]))) > 0)");
        });

        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
            .HasColumnName(idColumnName);

        builder.Property(entity => entity.TenantId)
            .HasColumnName("tenant_id");

        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasConversion<UpperSnakeCaseEnumConverter<MasterDataStatus>>()
            .HasMaxLength(StatusCodeMaxLength)
            .IsUnicode(false);

        builder.Property(entity => entity.DeactivateReason)
            .HasColumnName("deactivate_reason")
            .HasMaxLength(MasterDataEntity.DeactivateReasonMaxLength);

        builder.Property(entity => entity.RowVersion)
            .HasColumnName("row_version")
            .IsRowVersion();
    }
}
