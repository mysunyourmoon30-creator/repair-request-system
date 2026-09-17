using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace RepairRequest.Infrastructure.Authentication;

/// <summary>
/// Refresh token material: 32 bytes from the OS CSPRNG, transported as Base64Url and stored
/// only as SHA-256 of the decoded bytes. A fast unsalted hash is appropriate because the
/// token itself carries 256 bits of entropy (unlike a password).
/// </summary>
internal static class RefreshTokenCrypto
{
    private const int TokenByteLength = 32;

    private static readonly int EncodedLength = Base64Url.GetEncodedLength(TokenByteLength);

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));

    public static byte[] Hash(string token) =>
        TryHash(token, out var hash)
            ? hash
            : throw new ArgumentException("Refresh token is not well formed.", nameof(token));

    /// <summary>Rejects malformed input without touching the database.</summary>
    public static bool TryHash(string? token, [NotNullWhen(true)] out byte[]? hash)
    {
        hash = null;

        if (token is null || token.Length != EncodedLength)
        {
            return false;
        }

        Span<byte> tokenBytes = stackalloc byte[TokenByteLength];
        var status = Base64Url.DecodeFromChars(token, tokenBytes, out _, out var bytesWritten);
        if (status != OperationStatus.Done || bytesWritten != TokenByteLength)
        {
            return false;
        }

        hash = SHA256.HashData(tokenBytes);
        return true;
    }
}
