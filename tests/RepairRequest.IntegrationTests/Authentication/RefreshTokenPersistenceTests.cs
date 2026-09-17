using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Authentication;

/// <summary>refresh_token and login-identifier constraints against the migrated LocalDB schema.</summary>
[Collection(PersistenceDatabaseCollection.Name)]
public class RefreshTokenPersistenceTests
{
    private const int UniqueIndexViolation = 2601;
    private const int ConstraintViolation = 547;

    private readonly PersistenceDatabaseFixture _database;

    public RefreshTokenPersistenceTests(PersistenceDatabaseFixture database)
    {
        _database = database;
    }

    private static ApplicationUser NewUser(Guid tenantId, string? email = null)
    {
        var userName = $"user-{Guid.NewGuid():N}";
        return new ApplicationUser
        {
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    private static RefreshToken NewToken(ApplicationUser user, byte[]? hash = null, Guid? tenantId = null)
    {
        var createdAt = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc);
        return new RefreshToken(
            tenantId ?? user.TenantId,
            user.Id,
            Guid.NewGuid(),
            hash ?? RandomNumberGenerator.GetBytes(RefreshToken.TokenHashLength),
            createdAt,
            createdAt.AddDays(7));
    }

    private static async Task AssertSaveRejectedAsync(RepairRequestDbContext context, int sqlErrorNumber, string constraintName)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var sqlException = Assert.IsType<SqlException>(exception.InnerException);
        Assert.Equal(sqlErrorNumber, sqlException.Number);
        Assert.Contains(constraintName, sqlException.Message);
    }

    [Fact]
    public async Task ValidToken_Persists()
    {
        await using var context = _database.CreateContext();
        var user = NewUser(Guid.NewGuid());
        context.Users.Add(user);
        context.RefreshTokens.Add(NewToken(user));

        Assert.Equal(2, await context.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateTokenHash_IsRejected()
    {
        await using var context = _database.CreateContext();
        var user = NewUser(Guid.NewGuid());
        var hash = RandomNumberGenerator.GetBytes(RefreshToken.TokenHashLength);
        context.Users.Add(user);
        context.RefreshTokens.Add(NewToken(user, hash));
        await context.SaveChangesAsync();

        context.RefreshTokens.Add(NewToken(user, (byte[])hash.Clone()));

        await AssertSaveRejectedAsync(context, UniqueIndexViolation, "UQ_refresh_token_token_hash");
    }

    [Fact]
    public async Task TokenForUserOfAnotherTenant_IsRejected()
    {
        await using var context = _database.CreateContext();
        var user = NewUser(Guid.NewGuid());
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.RefreshTokens.Add(NewToken(user, tenantId: Guid.NewGuid()));

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_refresh_token_user");
    }

    [Fact]
    public async Task ExpiryNotAfterCreation_IsRejectedByDatabase()
    {
        await using var context = _database.CreateContext();
        var user = NewUser(Guid.NewGuid());
        context.Users.Add(user);
        var token = NewToken(user);
        context.RefreshTokens.Add(token);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlAsync(
            $"UPDATE [refresh_token] SET [expires_at] = [created_at] WHERE [refresh_token_id] = {token.Id}"));

        Assert.Equal(ConstraintViolation, exception.Number);
        Assert.Contains("CK_refresh_token_expiry", exception.Message);
    }

    [Fact]
    public async Task DuplicateNormalizedEmail_IsRejectedByUniqueLoginIdentifierIndex()
    {
        await using var context = _database.CreateContext();
        var email = $"dup-{Guid.NewGuid():N}@example.test";
        context.Users.Add(NewUser(Guid.NewGuid(), email));
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(Guid.NewGuid(), email.ToUpperInvariant()));

        await AssertSaveRejectedAsync(context, UniqueIndexViolation, "EmailIndex");
    }

    [Fact]
    public void Constructor_RejectsInvalidHashLength_NonUtcTimes_AndNonPositiveLifetime()
    {
        var user = NewUser(Guid.NewGuid());
        var utc = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentException>(() => new RefreshToken(user.TenantId, Guid.NewGuid(), Guid.NewGuid(), new byte[16], utc, utc.AddDays(7)));
        Assert.Throws<ArgumentException>(() => new RefreshToken(user.TenantId, Guid.NewGuid(), Guid.NewGuid(), new byte[32], DateTime.Now, DateTime.Now.AddDays(7)));
        Assert.Throws<ArgumentException>(() => new RefreshToken(user.TenantId, Guid.NewGuid(), Guid.NewGuid(), new byte[32], utc, utc));
    }
}
