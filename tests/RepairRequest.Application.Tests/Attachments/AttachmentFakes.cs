using System.Text;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Attachments;

internal static class TestFiles
{
    public const string InfectedMarker = "INFECTED-TEST-MARKER";

    public static byte[] Png(int extraBytes = 64) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[extraBytes]];

    public static byte[] Jpeg(int extraBytes = 64) => [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[extraBytes]];

    public static byte[] Pdf(int extraBytes = 64) => [.. "%PDF-1.7\n"u8.ToArray(), .. new byte[extraBytes]];

    public static byte[] Gif() => [.. "GIF89a"u8.ToArray(), .. new byte[32]];

    public static byte[] InfectedPng() => [.. Png(8), .. Encoding.ASCII.GetBytes(InfectedMarker)];

    public static AttachmentUpload Upload(string? fileName, string? contentType, byte[] content, long? declaredLength = null) =>
        new(fileName, contentType, declaredLength ?? content.Length, () => new MemoryStream(content, writable: false));

    public static CommandContext Requester(Guid tenantId, Guid userId) =>
        new(new CurrentUser(userId, tenantId, [RoleCodes.Requester]), Guid.NewGuid());
}

/// <summary>In-memory attachment port. Scope is reduced to owner/tenant; SQL scope is covered by integration/API tests.</summary>
internal sealed class FakeAttachmentStore : IAttachmentStore
{
    private readonly List<object> _pending = [];

    public Dictionary<Guid, (Guid OwnerId, Guid TenantId, RepairRequestStatus Status)> Requests { get; } = new();

    public List<FileAsset> Files { get; } = [];

    public List<RepairRequestAttachment> Attachments { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public int SaveCount { get; private set; }

    public bool FailSave { get; set; }

    public bool LoseScanRace { get; set; }

    public Guid AddRequest(Guid ownerId, Guid tenantId, RepairRequestStatus status = RepairRequestStatus.Draft)
    {
        var id = Guid.NewGuid();
        Requests[id] = (ownerId, tenantId, status);
        return id;
    }

    public FileAsset SeedFile(Guid tenantId, Guid repairRequestId, string storageKey, MalwareScanStatus status = MalwareScanStatus.Pending)
    {
        var file = new FileAsset(tenantId, "photo.png", "image/png", 72, new string('b', 64), storageKey, Guid.NewGuid(), DateTime.UtcNow);
        SetId(file, Guid.NewGuid());
        if (status == MalwareScanStatus.Clean)
        {
            file.MarkClean();
        }
        else if (status == MalwareScanStatus.Failed)
        {
            file.MarkFailed();
        }

        Files.Add(file);
        var attachment = new RepairRequestAttachment(repairRequestId, file.Id, AttachmentFileRules.AccessScopeCode);
        SetId(attachment, Guid.NewGuid());
        Attachments.Add(attachment);
        return file;
    }

    public Task<RepairRequestStatus?> GetOwnRequestStatusAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        Task.FromResult<RepairRequestStatus?>(
            Requests.TryGetValue(repairRequestId, out var request) && request.OwnerId == user.UserId && request.TenantId == user.TenantId
                ? request.Status
                : null);

    public Task<AttachmentDownloadInfo?> FindDownloadAsync(CurrentUser user, Guid fileAssetId, CancellationToken cancellationToken)
    {
        var match = (from attachment in Attachments
                     join file in Files on attachment.FileAssetId equals file.Id
                     where file.Id == fileAssetId && file.TenantId == user.TenantId
                     select new AttachmentDownloadInfo(attachment.RepairRequestId, file.TenantId, file.Id, file.FileName, file.MimeType, file.SizeBytes, file.StorageReference, file.MalwareScanStatus))
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<PagedResult<AttachmentDto>?> ListAsync(CurrentUser user, Guid repairRequestId, PageRequest paging, CancellationToken cancellationToken)
    {
        if (!Requests.TryGetValue(repairRequestId, out var request) || request.TenantId != user.TenantId)
        {
            return Task.FromResult<PagedResult<AttachmentDto>?>(null);
        }

        var all = (from attachment in Attachments
                   join file in Files on attachment.FileAssetId equals file.Id
                   where attachment.RepairRequestId == repairRequestId && file.TenantId == user.TenantId
                   select new AttachmentDto(attachment.Id, file.Id, file.FileName, file.MimeType, file.SizeBytes, file.MalwareScanStatus, file.UploadedAt))
            .ToList();
        var items = all.Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize).ToList();

        return Task.FromResult<PagedResult<AttachmentDto>?>(new PagedResult<AttachmentDto>(items, paging.Page, paging.PageSize, all.Count));
    }

    public void AddFileAsset(FileAsset fileAsset)
    {
        SetId(fileAsset, Guid.NewGuid());
        _pending.Add(fileAsset);
    }

    public void AddAttachment(RepairRequestAttachment attachment)
    {
        SetId(attachment, Guid.NewGuid());
        _pending.Add(attachment);
    }

    public void AddAudit(AuditHistory audit) => _pending.Add(audit);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        if (FailSave)
        {
            _pending.Clear();
            throw new InvalidOperationException("Simulated database failure.");
        }

        foreach (var item in _pending)
        {
            switch (item)
            {
                case FileAsset file:
                    Files.Add(file);
                    break;
                case RepairRequestAttachment attachment:
                    Attachments.Add(attachment);
                    break;
                case AuditHistory audit:
                    Audits.Add(audit);
                    break;
            }
        }

        _pending.Clear();
        return Task.CompletedTask;
    }

    public Task<FileAsset?> FindPendingFileAsync(Guid fileAssetId, CancellationToken cancellationToken)
    {
        var stored = Files.SingleOrDefault(file => file.Id == fileAssetId && file.MalwareScanStatus == MalwareScanStatus.Pending);
        if (stored is null)
        {
            return Task.FromResult<FileAsset?>(null);
        }

        // Untracked copy, like the EF implementation.
        var copy = new FileAsset(stored.TenantId, stored.FileName, stored.MimeType, stored.SizeBytes, stored.ContentHash, stored.StorageReference, stored.UploadedBy, stored.UploadedAt);
        SetId(copy, stored.Id);
        return Task.FromResult<FileAsset?>(copy);
    }

    public Task<bool> TryCompleteScanAsync(FileAsset scannedFile, AuditHistory audit, CancellationToken cancellationToken)
    {
        var stored = Files.Single(file => file.Id == scannedFile.Id);
        if (LoseScanRace || stored.MalwareScanStatus != MalwareScanStatus.Pending)
        {
            return Task.FromResult(false);
        }

        if (scannedFile.MalwareScanStatus == MalwareScanStatus.Clean)
        {
            stored.MarkClean();
        }
        else
        {
            stored.MarkFailed();
        }

        Audits.Add(audit);
        return Task.FromResult(true);
    }

    private static void SetId(object entity, Guid id) => entity.GetType().GetProperty("Id")!.SetValue(entity, id);
}

internal sealed class FakeFileStorage : IFileStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new();

    public List<string> Deleted { get; } = [];

    public bool FailSave { get; set; }

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken)
    {
        if (FailSave)
        {
            throw new IOException("Simulated storage failure.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Objects.Add(storageKey, buffer.ToArray());
    }

    public int OpenCount { get; private set; }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        OpenCount++;
        return Objects.TryGetValue(storageKey, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException("Simulated missing object.");
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        Deleted.Add(storageKey);
        Objects.Remove(storageKey);
        return Task.CompletedTask;
    }
}

internal sealed class FakeMalwareScanner : IFileMalwareScanner
{
    public MalwareScanOutcome? FixedOutcome { get; set; }

    public bool Throw { get; set; }

    public int Calls { get; private set; }

    public async Task<MalwareScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        Calls++;
        if (Throw)
        {
            throw new InvalidOperationException("Simulated scanner failure.");
        }

        if (FixedOutcome is { } outcome)
        {
            return outcome;
        }

        using var reader = new StreamReader(content, Encoding.ASCII);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return text.Contains(TestFiles.InfectedMarker, StringComparison.Ordinal) ? MalwareScanOutcome.Infected : MalwareScanOutcome.Clean;
    }
}
