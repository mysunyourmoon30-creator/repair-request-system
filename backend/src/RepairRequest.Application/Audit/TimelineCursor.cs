using System.Buffers.Text;

namespace RepairRequest.Application.Audit;

/// <summary>
/// Issues and verifies the opaque keyset cursor of the Work Order timeline (docs/15 §5). The implementation signs the
/// position with a key derived from server secrets and binds it to the tenant, the normalized entity type and the route
/// Work Order id — values taken from the authenticated server context and the route, never stored in or read from the token.
/// </summary>
public interface ITimelineCursorProtector
{
    /// <summary>URL-safe Base64 (no padding) of the 57-byte cursor for <paramref name="position"/>.</summary>
    string Protect(Guid tenantId, string normalizedEntityType, Guid workOrderId, TimelinePosition position);

    /// <summary>
    /// Verifies the tag first (constant-time), and only for an authentic payload checks the version, the ticks range and the
    /// non-empty audit id. Needs no database access and no Work Order lookup.
    /// </summary>
    bool TryUnprotect(Guid tenantId, string normalizedEntityType, Guid workOrderId, ReadOnlySpan<byte> cursorBytes, out TimelinePosition position);
}

/// <summary>Cursor layout shared by the protector and the syntax check (docs/15 §5).</summary>
public static class TimelineCursorFormat
{
    public const byte Version = 0x01;
    public const int VersionLength = 1;
    public const int TicksLength = 8;
    public const int AuditIdLength = 16;

    /// <summary>version + ticks + audit id: the signed payload.</summary>
    public const int PayloadLength = VersionLength + TicksLength + AuditIdLength;

    public const int TagLength = 32;
    public const int TotalLength = PayloadLength + TagLength;

    /// <summary>A 57-byte cursor encodes to 76 Base64url characters; anything longer than this is rejected before decoding.</summary>
    public const int MaxTextLength = 128;
}

/// <summary>Step 3 of the validation order (docs/15 §5): cursor syntax only — no key, no database.</summary>
public static class TimelineCursorSyntax
{
    /// <summary>True when <paramref name="text"/> is Base64url (no padding) of exactly 57 bytes and at most 128 characters.</summary>
    public static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text) || text.Length > TimelineCursorFormat.MaxTextLength)
        {
            return false;
        }

        try
        {
            var decoded = Base64Url.DecodeFromChars(text);
            if (decoded.Length != TimelineCursorFormat.TotalLength)
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
