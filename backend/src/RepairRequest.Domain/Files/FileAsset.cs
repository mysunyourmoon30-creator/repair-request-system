using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.Files;

/// <summary>
/// Shared private file metadata (RR-DD-001 FAS-001..FAS-010; RR-ERD-001 ERD-DR-04).
/// The binary lives in private object storage; only a non-public reference is stored.
/// Upload allowlist / size validation belongs to the upload use case.
/// </summary>
public sealed class FileAsset
{
    public const int FileNameMaxLength = 255;
    public const int MimeTypeMaxLength = 100;
    public const int ContentHashLength = 64;
    public const int StorageReferenceMaxLength = 500;

    private FileAsset()
    {
        FileName = null!;
        MimeType = null!;
        ContentHash = null!;
        StorageReference = null!;
    }

    public FileAsset(
        Guid tenantId,
        string fileName,
        string mimeType,
        long sizeBytes,
        string contentHash,
        string storageReference,
        Guid uploadedBy,
        DateTime uploadedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

        if (contentHash is null || contentHash.Length != ContentHashLength || !contentHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Content hash must be a 64-character hexadecimal SHA-256 value.", nameof(contentHash));
        }

        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        FileName = DomainGuard.RequiredText(fileName, FileNameMaxLength, nameof(fileName));
        MimeType = DomainGuard.RequiredText(mimeType, MimeTypeMaxLength, nameof(mimeType));
        SizeBytes = sizeBytes;
        ContentHash = contentHash;
        StorageReference = DomainGuard.RequiredText(storageReference, StorageReferenceMaxLength, nameof(storageReference));
        MalwareScanStatus = MalwareScanStatus.Pending;
        UploadedBy = DomainGuard.NotEmpty(uploadedBy, nameof(uploadedBy));
        UploadedAt = DomainGuard.Utc(uploadedAt, nameof(uploadedAt));
    }

    /// <summary>FAS-001.</summary>
    public Guid Id { get; private set; }

    /// <summary>FAS-002.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>FAS-003. Sanitized display name.</summary>
    public string FileName { get; private set; }

    /// <summary>FAS-004.</summary>
    public string MimeType { get; private set; }

    /// <summary>FAS-005.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>FAS-006. SHA-256.</summary>
    public string ContentHash { get; private set; }

    /// <summary>FAS-007. Private object reference, never a public URL.</summary>
    public string StorageReference { get; private set; }

    /// <summary>FAS-008. Every new upload starts PENDING (RR-ARCH-001 section 11).</summary>
    public MalwareScanStatus MalwareScanStatus { get; private set; }

    /// <summary>FAS-009. Derived actor.</summary>
    public Guid UploadedBy { get; private set; }

    /// <summary>FAS-010. UTC; immutable.</summary>
    public DateTime UploadedAt { get; private set; }

    /// <summary>PENDING -> CLEAN: the file may be served and used as evidence (RR-ARCH-001 section 11).</summary>
    public void MarkClean() => CompleteScan(MalwareScanStatus.Clean);

    /// <summary>PENDING -> FAILED: the file is rejected and never usable (RR-UI-001 section 1 "FAILED rejected").</summary>
    public void MarkFailed() => CompleteScan(MalwareScanStatus.Failed);

    private void CompleteScan(MalwareScanStatus result)
    {
        if (MalwareScanStatus != MalwareScanStatus.Pending)
        {
            throw new DomainRuleViolationException("Only a PENDING file can receive a malware scan result.");
        }

        MalwareScanStatus = result;
    }
}
