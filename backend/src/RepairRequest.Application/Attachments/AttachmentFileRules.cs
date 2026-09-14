using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RepairRequest.Application.Attachments;

/// <summary>One accepted attachment type: canonical MIME type and the leading bytes its content must start with.</summary>
public sealed record AttachmentFileType(string CanonicalMimeType, byte[] Signature);

/// <summary>
/// Attachment file policy (RR-REQ-001 NFR File; RR-DD-001 FAS-003/004/005; RR-ARCH-001 section 11): PDF/JPG/JPEG/PNG
/// only, at most 10 MB, and extension, declared content type and content signature must agree. Filenames are display
/// metadata only and are sanitized; they never influence storage.
/// </summary>
public static class AttachmentFileRules
{
    /// <summary>"10 MB" per file, applied as 10 × 1024 × 1024 bytes.</summary>
    public const long MaxSizeBytes = 10L * 1024 * 1024;

    /// <summary>Decision F4: attachment access derives entirely from the Repair Request's own scope.</summary>
    public const string AccessScopeCode = "REPAIR_REQUEST";

    public const string FileField = "file";

    private const int MaxFileNameLength = Domain.Files.FileAsset.FileNameMaxLength;

    private static readonly AttachmentFileType Pdf = new("application/pdf", "%PDF-"u8.ToArray());
    private static readonly AttachmentFileType Jpeg = new("image/jpeg", [0xFF, 0xD8, 0xFF]);
    private static readonly AttachmentFileType Png = new("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private static readonly Dictionary<string, AttachmentFileType> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Pdf,
        [".jpg"] = Jpeg,
        [".jpeg"] = Jpeg,
        [".png"] = Png
    };

    private static readonly char[] ForbiddenFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Keeps only the last path segment, removes control, format (e.g. right-to-left override) and reserved characters,
    /// trims trailing dots/spaces and limits the length while keeping the extension. Null when nothing usable remains.
    /// </summary>
    public static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var lastSeparator = fileName.LastIndexOfAny(['/', '\\']);
        var lastSegment = lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;

        var builder = new StringBuilder(lastSegment.Length);
        foreach (var character in lastSegment)
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character)
                || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || Array.IndexOf(ForbiddenFileNameChars, character) >= 0)
            {
                continue;
            }

            builder.Append(character);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        var extension = Path.GetExtension(cleaned);
        if (cleaned.Length == 0 || cleaned.Length == extension.Length)
        {
            return null;
        }

        if (cleaned.Length > MaxFileNameLength)
        {
            cleaned = cleaned[..(MaxFileNameLength - extension.Length)] + extension;
        }

        return cleaned;
    }

    /// <summary>The accepted type for a sanitized filename whose declared content type matches its extension; otherwise null.</summary>
    public static AttachmentFileType? TypeFor(string sanitizedFileName, string? declaredContentType)
    {
        if (!TypesByExtension.TryGetValue(Path.GetExtension(sanitizedFileName), out var type))
        {
            return null;
        }

        var mediaType = declaredContentType?.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, type.CanonicalMimeType, StringComparison.OrdinalIgnoreCase) ? type : null;
    }

    /// <summary>Reads the leading bytes and rewinds the stream. The stream must be seekable.</summary>
    public static async Task<bool> HasSignatureAsync(Stream content, AttachmentFileType type, CancellationToken cancellationToken)
    {
        if (!content.CanSeek)
        {
            throw new InvalidOperationException("Attachment content must be seekable for signature validation.");
        }

        var header = new byte[type.Signature.Length];
        var read = 0;
        while (read < header.Length)
        {
            var count = await content.ReadAsync(header.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        content.Position = 0;
        return read == header.Length && header.AsSpan().SequenceEqual(type.Signature);
    }

    /// <summary>A new opaque storage key: tenant partition plus a random object id; no client input.</summary>
    public static string NewStorageKey(Guid tenantId) =>
        $"{tenantId:N}/{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
}
