using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Domain.MasterData;
using RepairRequest.Infrastructure.Identity;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class UserSiteScopeConfiguration : IEntityTypeConfiguration<UserSiteScope>
{
    public void Configure(EntityTypeBuilder<UserSiteScope> builder)
    {
        builder.ToTable("user_site_scope");

        // Leading (tenant_id, user_id) serves the per-request scope resolution lookup
        // and covers the user foreign key, so no extra index is needed for it.
        builder.HasKey(scope => new { scope.TenantId, scope.UserId, scope.SiteId });

        builder.Property(scope => scope.TenantId).HasColumnName("tenant_id");
        builder.Property(scope => scope.UserId).HasColumnName("user_id");
        builder.Property(scope => scope.SiteId).HasColumnName("site_id");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(scope => new { scope.TenantId, scope.UserId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_user_site_scope_user");

        builder.HasOne<Site>()
            .WithMany()
            .HasForeignKey(scope => new { scope.TenantId, scope.SiteId })
            .HasPrincipalKey(site => new { site.TenantId, site.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_user_site_scope_site");
    }
}
