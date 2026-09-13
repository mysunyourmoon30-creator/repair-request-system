using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class EquipmentConfiguration : IEntityTypeConfiguration<Equipment>
{
    public void Configure(EntityTypeBuilder<Equipment> builder)
    {
        MasterDataMapping.MapCommon(builder, "equipment", "equipment_id");

        builder.Property(equipment => equipment.SiteId)
            .HasColumnName("site_id");

        builder.Property(equipment => equipment.EquipmentCode)
            .HasColumnName("equipment_code")
            .HasMaxLength(MasterDataEntity.CodeMaxLength)
            .IsUnicode(false);

        // Principal key that lets repair_request enforce "Equipment belongs to Site" (RR-DD-001 RR-006).
        builder.HasAlternateKey(equipment => new { equipment.SiteId, equipment.Id })
            .HasName("AK_equipment_site_id_equipment_id");

        // DEC-PS1-015: equipment_code unique within Site. Also serves the
        // "active Equipment under Site" deactivation guard lookup (DEC-PS1-013).
        builder.HasIndex(equipment => new { equipment.SiteId, equipment.EquipmentCode })
            .IsUnique()
            .HasDatabaseName("UQ_equipment_site_id_equipment_code");

        // DEC-PS1-013: Equipment belongs to a Site of the same Tenant. No cascade.
        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(equipment => new { equipment.TenantId, equipment.SiteId })
            .HasPrincipalKey(site => new { site.TenantId, site.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_equipment_site");
    }
}
