using RepairRequest.Domain.Files;

namespace RepairRequest.Application.Attachments;

/// <summary>An uploaded file as received by the API. Name, content type and length are client-reported and untrusted.</summary>
public sealed record AttachmentUpload(string? FileName, string? ContentType, long Length, Func<Stream> OpenReadStream);

public sealed record AttachmentDto(
    Guid AttachmentId,
    Guid FileAssetId,
    string FileName,
    string MimeType,
    long SizeBytes,
    MalwareScanStatus MalwareScanStatus,
    DateTime UploadedAt);

/// <summary>A CLEAN file ready to stream. The caller owns and disposes <see cref="Content"/>.</summary>
public sealed record AttachmentDownload(Stream Content, string FileName, string MimeType, long SizeBytes);

public enum FileScanResult
{
    /// <summary>The file does not exist or already has a scan result.</summary>
    NotPending,

    /// <summary>The scanner was unavailable or failed; the file remains PENDING and unusable (decision F2).</summary>
    StillPending,

    Clean,

    Failed
}

/// <summary>Canonical malware scan status codes (RR-DD-001 FAS-008).</summary>
public static class MalwareScanStatusCodes
{
    public static string ToCode(MalwareScanStatus status) => status switch
    {
        MalwareScanStatus.Pending => "PENDING",
        MalwareScanStatus.Clean => "CLEAN",
        MalwareScanStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
