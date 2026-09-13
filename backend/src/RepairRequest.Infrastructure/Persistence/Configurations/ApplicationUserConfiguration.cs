using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Infrastructure.Identity;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>
/// Extends the default ASP.NET Core Identity user mapping (AspNetUsers) with the
/// company data scope. Applied after IdentityDbContext has configured its schema.
/// </summary>
internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.TenantId)
            .IsRequired();

        // Principal key for tenant-consistent actor foreign keys (created_by, submitted_by,
        // uploaded_by, user_site_scope). Also makes TenantId immutable once persisted.
        builder.HasAlternateKey(user => new { user.TenantId, user.Id })
            .HasName("AK_AspNetUsers_TenantId_Id");
    }
}
