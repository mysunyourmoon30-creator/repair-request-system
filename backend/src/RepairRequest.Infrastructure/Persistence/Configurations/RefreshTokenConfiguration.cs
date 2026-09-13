using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RepairRequest.Infrastructure.Identity;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_token", table =>
            table.HasCheckConstraint("CK_refresh_token_expiry", "[expires_at] > [created_at]"));

        builder.HasKey(token => token.Id);

        builder.Property(token => token.Id).HasColumnName("refresh_token_id");
        builder.Property(token => token.TenantId).HasColumnName("tenant_id");
        builder.Property(token => token.UserId).HasColumnName("user_id");
        builder.Property(token => token.FamilyId).HasColumnName("family_id");

        builder.Property(token => token.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(RefreshToken.TokenHashLength)
            .IsFixedLength();

        builder.Property(token => token.CreatedAt).HasColumnName("created_at");
        builder.Property(token => token.ExpiresAt).HasColumnName("expires_at");
        builder.Property(token => token.RevokedAt).HasColumnName("revoked_at");

        // Rotation link only; no self foreign key so no unused index is created for it.
        builder.Property(token => token.ReplacedByTokenId).HasColumnName("replaced_by_token_id");

        // Refresh / revoke lookup: single-row seek by hash.
        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("UQ_refresh_token_token_hash");

        // Family revocation on reuse detection and on revoke.
        builder.HasIndex(token => token.FamilyId)
            .HasDatabaseName("IX_refresh_token_family_id");

        // Token owner must be a user of the same tenant. The (tenant_id, user_id) index is
        // created by convention to support this foreign key.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(token => new { token.TenantId, token.UserId })
            .HasPrincipalKey(user => new { user.TenantId, user.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_refresh_token_user");
    }
}
