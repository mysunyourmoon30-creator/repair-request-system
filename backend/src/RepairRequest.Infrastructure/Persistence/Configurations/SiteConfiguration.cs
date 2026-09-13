using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        MasterDataMapping.MapCommon(builder, "site", "site_id");

        builder.Property(site => site.CustomerId)
            .HasColumnName("customer_id");

        builder.Property(site => site.SiteCode)
            .HasColumnName("site_code")
            .HasMaxLength(MasterDataEntity.CodeMaxLength)
            .IsUnicode(false);

        // Principal key for tenant-consistent composite foreign keys from equipment,
        // repair_request and user_site_scope.
        builder.HasAlternateKey(site => new { site.TenantId, site.Id })
            .HasName("AK_site_tenant_id_site_id");

        // DEC-PS1-015: site_code unique within Customer. Also serves the
        // "active Site under Customer" deactivation guard lookup (DEC-PS1-013).
        builder.HasIndex(site => new { site.CustomerId, site.SiteCode })
            .IsUnique()
            .HasDatabaseName("UQ_site_customer_id_site_code");

        // DEC-PS1-013: Site belongs to a Customer of the same Tenant. No cascade.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(site => new { site.TenantId, site.CustomerId })
            .HasPrincipalKey(customer => new { customer.TenantId, customer.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_site_customer");
    }
}
