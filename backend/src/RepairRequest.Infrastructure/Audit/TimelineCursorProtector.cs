using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RepairRequest.Application.Audit;
using RepairRequest.Infrastructure.Authentication;

namespace RepairRequest.Infrastructure.Audit;

/// <summary>
/// HMAC-SHA256 timeline cursor (docs/15 §5). Layout: version (1) ‖ occurred_at UTC ticks (8, big-endian) ‖ audit id (16)
/// ‖ tag (32), URL-safe Base64 without padding. The tag is <c>HMAC-SHA256(key, context ‖ payload)</c> with
/// <c>context = "AUD-TL-v1|" + tenantId + "|" + normalizedEntityType + "|" + workOrderId</c>; none of the three values is
/// stored in the token. The key is derived by HKDF-SHA256 from the existing <c>Jwt:SigningKey</c> with a fixed purpose
/// string, so no new secret exists and the key is separated from token signing. The tag is compared in constant time; the
/// version, ticks range and audit id are checked only for an authentic payload. Nothing here logs a cursor, a key or a payload.
/// </summary>
internal sealed class TimelineCursorProtector : ITimelineCursorProtector
{
    private const string Purpose = "rr:audit-timeline-cursor:v1";
    private const string ContextPrefix = "AUD-TL-v1|";

    private readonly byte[] _key;

    public TimelineCursorProtector(IOptions<JwtOptions> jwt)
    {
        if (!JwtTokenValidation.TryDecodeSigningKey(jwt.Value.SigningKey, out var signingKey))
        {
            throw new InvalidOperationException($"{JwtOptions.SectionName}:SigningKey is not configured correctly.");
        }

        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, signingKey, outputLength: 32, salt: [], info: Encoding.UTF8.GetBytes(Purpose));
    }

    public string Protect(Guid tenantId, string normalizedEntityType, Guid workOrderId, TimelinePosition position)
    {
        Span<byte> cursor = stackalloc byte[TimelineCursorFormat.TotalLength];
        cursor[0] = TimelineCursorFormat.Version;
        BinaryPrimitives.WriteInt64BigEndian(cursor.Slice(TimelineCursorFormat.VersionLength, TimelineCursorFormat.TicksLength), position.OccurredAt.Ticks);
        position.AuditId.TryWriteBytes(cursor.Slice(TimelineCursorFormat.VersionLength + TimelineCursorFormat.TicksLength, TimelineCursorFormat.AuditIdLength));

        Tag(tenantId, normalizedEntityType, workOrderId, cursor[..TimelineCursorFormat.PayloadLength]).CopyTo(cursor[TimelineCursorFormat.PayloadLength..]);
        return Base64Url.EncodeToString(cursor);
    }

    public bool TryUnprotect(Guid tenantId, string normalizedEntityType, Guid workOrderId, ReadOnlySpan<byte> cursorBytes, out TimelinePosition position)
    {
        position = default;
        if (cursorBytes.Length != TimelineCursorFormat.TotalLength)
        {
            return false;
        }

        var payload = cursorBytes[..TimelineCursorFormat.PayloadLength];
        var expected = Tag(tenantId, normalizedEntityType, workOrderId, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, cursorBytes[TimelineCursorFormat.PayloadLength..]))
        {
            return false;
        }

        // Authentic payload only from here on.
        if (payload[0] != TimelineCursorFormat.Version)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(TimelineCursorFormat.VersionLength, TimelineCursorFormat.TicksLength));
        if (ticks is < 0 or > 3_155_378_975_999_999_999L)
        {
            return false;
        }

        var auditId = new Guid(payload.Slice(TimelineCursorFormat.VersionLength + TimelineCursorFormat.TicksLength, TimelineCursorFormat.AuditIdLength));
        if (auditId == Guid.Empty)
        {
            return false;
        }

        position = new TimelinePosition(new DateTime(ticks, DateTimeKind.Utc), auditId);
        return true;
    }

    private byte[] Tag(Guid tenantId, string normalizedEntityType, Guid workOrderId, ReadOnlySpan<byte> payload)
    {
        var context = Encoding.UTF8.GetBytes($"{ContextPrefix}{tenantId:D}|{normalizedEntityType}|{workOrderId:D}");
        var message = new byte[context.Length + payload.Length];
        context.CopyTo(message, 0);
        payload.CopyTo(message.AsSpan(context.Length));
        return HMACSHA256.HashData(_key, message);
    }
}
