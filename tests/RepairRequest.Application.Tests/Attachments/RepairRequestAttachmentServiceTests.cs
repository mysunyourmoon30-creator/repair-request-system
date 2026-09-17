using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.Tests.Attachments;

/// <summary>
/// Upload/download rules (UC-RR-001; TC-RR-002; RR-ARCH-001 section 11): owner + DRAFT, allowlisted type with matching
/// content type and signature, 10 MB limit, sanitized name, opaque storage, PENDING, audit, compensation, CLEAN-only download.
/// </summary>
public class RepairRequestAttachmentServiceTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, 250, TimeSpan.Zero).AddTicks(99);

    private readonly FakeAttachmentStore _store = new();
    private readonly FakeFileStorage _storage = new();
    private readonly RepairRequestAttachmentService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    public RepairRequestAttachmentServiceTests()
    {
        _service = new RepairRequestAttachmentService(_store, _storage, new FixedClock(Now));
    }

    private CommandContext Owner() => TestFiles.Requester(_tenantId, _ownerId);

    private void AssertNothingPersisted()
    {
        Assert.Empty(_storage.Objects);
        Assert.Empty(_store.Files);
        Assert.Empty(_store.Attachments);
        Assert.Empty(_store.Audits);
    }

    // ---------------- Upload ----------------

    [Fact]
    public async Task Upload_ValidPng_StoresOpaquelyAndPersistsPendingMetadataWithAudit()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var content = TestFiles.Png();
        var context = Owner();

        var result = await _service.UploadAsync(context, requestId, TestFiles.Upload("../../site photo.png", "image/png", content), CancellationToken.None);

        Assert.True(result.Succeeded);
        var file = Assert.Single(_store.Files);
        Assert.Equal(_tenantId, file.TenantId);
        Assert.Equal("site photo.png", file.FileName);
        Assert.Equal("image/png", file.MimeType);
        Assert.Equal(content.Length, file.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), file.ContentHash);
        Assert.Equal(MalwareScanStatus.Pending, file.MalwareScanStatus);
        Assert.Equal(_ownerId, file.UploadedBy);
        Assert.Equal(new DateTime(2026, 9, 14, 12, 0, 0, 250, DateTimeKind.Utc), file.UploadedAt);

        Assert.Matches(new Regex($"^{_tenantId:N}/[0-9a-f]{{32}}$"), file.StorageReference);
        Assert.DoesNotContain("photo", file.StorageReference, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, _storage.Objects[file.StorageReference]);

        var attachment = Assert.Single(_store.Attachments);
        Assert.Equal(requestId, attachment.RepairRequestId);
        Assert.Equal(file.Id, attachment.FileAssetId);
        Assert.Equal("REPAIR_REQUEST", attachment.AccessScopeCode);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal("REPAIR_REQUEST", audit.EntityType);
        Assert.Equal(requestId, audit.EntityId);
        Assert.Equal(AttachmentAudit.AttachmentAddedAction, audit.ActionCode);
        Assert.Equal(context.User.UserId, audit.ActorId);
        Assert.Equal(context.CorrelationId, audit.CorrelationId);
        Assert.Contains(file.ContentHash, audit.NewValueJson);
        Assert.DoesNotContain(file.StorageReference, audit.NewValueJson);

        Assert.Equal(MalwareScanStatus.Pending, result.Value!.MalwareScanStatus);
        Assert.Equal(file.Id, result.Value.FileAssetId);
    }

    [Theory]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("scan.jpeg", "image/jpeg")]
    [InlineData("quote.pdf", "application/pdf")]
    public async Task Upload_OtherAllowedTypes_Succeed(string fileName, string contentType)
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var content = contentType == "application/pdf" ? TestFiles.Pdf() : TestFiles.Jpeg();

        var result = await _service.UploadAsync(Owner(), requestId, TestFiles.Upload(fileName, contentType, content), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(contentType, Assert.Single(_store.Files).MimeType);
    }

    [Fact]
    public async Task Upload_ToAnotherUsersOrUnknownRequest_ReturnsNotFound_WithoutStoringAnything()
    {
        var otherUsersRequest = _store.AddRequest(Guid.NewGuid(), _tenantId);
        var otherTenantRequest = _store.AddRequest(_ownerId, Guid.NewGuid());

        var other = await _service.UploadAsync(Owner(), otherUsersRequest, TestFiles.Upload("a.png", "image/png", TestFiles.Png()), CancellationToken.None);
        var tenant = await _service.UploadAsync(Owner(), otherTenantRequest, TestFiles.Upload("a.png", "image/png", TestFiles.Png()), CancellationToken.None);
        var unknown = await _service.UploadAsync(Owner(), Guid.NewGuid(), TestFiles.Upload("a.png", "image/png", TestFiles.Png()), CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, other.Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, tenant.Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, unknown.Error?.Failure);
        AssertNothingPersisted();
    }

    [Theory]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Cancelled)]
    public async Task Upload_WhenRequestIsNotDraft_ReturnsStateConflict(RepairRequestStatus status)
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId, status);

        var result = await _service.UploadAsync(Owner(), requestId, TestFiles.Upload("a.png", "image/png", TestFiles.Png()), CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        AssertNothingPersisted();
    }

    public static TheoryData<string?, string?, byte[]> RejectedFiles => new()
    {
        { "malware.exe", "application/octet-stream", TestFiles.Png() },
        { "animation.gif", "image/gif", TestFiles.Gif() },
        { "vector.svg", "image/svg+xml", "<svg/>"u8.ToArray() },
        { "fake.pdf", "application/pdf", TestFiles.Png() },
        { "photo.png", "application/pdf", TestFiles.Png() },
        { "photo.png", "image/png", TestFiles.Jpeg() },
        { "photo.png", null, TestFiles.Png() },
        { "empty.png", "image/png", Array.Empty<byte>() },
        { "../", "image/png", TestFiles.Png() },
        { null, "image/png", TestFiles.Png() }
    };

    [Theory]
    [MemberData(nameof(RejectedFiles))]
    public async Task Upload_InvalidFile_Returns422OnFileField_WithoutStoringAnything(string? fileName, string? contentType, byte[] content)
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);

        var result = await _service.UploadAsync(Owner(), requestId, TestFiles.Upload(fileName, contentType, content), CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(AttachmentFileRules.FileField));
        AssertNothingPersisted();
    }

    [Fact]
    public async Task Upload_DeclaredLargerThan10MB_IsRejectedBeforeReading()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var upload = new AttachmentUpload("big.png", "image/png", AttachmentFileRules.MaxSizeBytes + 1, () => throw new InvalidOperationException("Must not be read."));

        var result = await _service.UploadAsync(Owner(), requestId, upload, CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        AssertNothingPersisted();
    }

    [Fact]
    public async Task Upload_ContentLongerThan10MBDespiteSmallDeclaredLength_IsRejectedWhileStreaming()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var content = TestFiles.Png((int)AttachmentFileRules.MaxSizeBytes);

        var result = await _service.UploadAsync(Owner(), requestId, TestFiles.Upload("big.png", "image/png", content, declaredLength: 100), CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        AssertNothingPersisted();
    }

    [Fact]
    public async Task Upload_ExactlyAtLimit_IsAccepted()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var content = TestFiles.Png((int)AttachmentFileRules.MaxSizeBytes - 8);

        var result = await _service.UploadAsync(Owner(), requestId, TestFiles.Upload("max.png", "image/png", content), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AttachmentFileRules.MaxSizeBytes, result.Value!.SizeBytes);
    }

    [Fact]
    public async Task Upload_StorageFailure_PersistsNoMetadata()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        _storage.FailSave = true;

        await Assert.ThrowsAsync<IOException>(() =>
            _service.UploadAsync(Owner(), requestId, TestFiles.Upload("a.png", "image/png", TestFiles.Png()), CancellationToken.None));

        Assert.Equal(0, _store.SaveCount);
        AssertNothingPersisted();
    }

    [Fact]
    public async Task Upload_DatabaseFailure_DeletesTheStoredBinary()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        _store.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadAsync(Owner(), requestId, TestFiles.Upload("a.png", "image/png", TestFiles.Png()), CancellationToken.None));

        Assert.Single(_storage.Deleted);
        AssertNothingPersisted();
    }

    // ---------------- List ----------------

    [Fact]
    public async Task List_OutsideScope_ReturnsNull_AndInScopeReturnsOneBoundedPageWithoutOpeningStorage()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        for (var index = 0; index < 5; index++)
        {
            _store.SeedFile(_tenantId, requestId, AttachmentFileRules.NewStorageKey(_tenantId));
        }

        var outside = await _service.ListAsync(TestFiles.Requester(Guid.NewGuid(), _ownerId).User, requestId, new PageRequest(1, 10), CancellationToken.None);
        var page = await _service.ListAsync(Owner().User, requestId, new PageRequest(2, 2), CancellationToken.None);

        Assert.Null(outside);
        Assert.Equal(5, page!.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(0, _storage.OpenCount);
    }

    // ---------------- Download ----------------

    [Fact]
    public async Task Download_CleanFile_ReturnsContent_AndAuditsAccess()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var key = AttachmentFileRules.NewStorageKey(_tenantId);
        _storage.Objects[key] = TestFiles.Png();
        var file = _store.SeedFile(_tenantId, requestId, key, MalwareScanStatus.Clean);
        var context = Owner();

        var result = await _service.DownloadAsync(context, file.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var content = result.Value!.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        Assert.Equal(TestFiles.Png(), copy.ToArray());
        Assert.Equal("image/png", result.Value.MimeType);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(AttachmentAudit.AttachmentDownloadedAction, audit.ActionCode);
        Assert.Equal(requestId, audit.EntityId);
        Assert.Equal(context.User.UserId, audit.ActorId);
    }

    [Theory]
    [InlineData(MalwareScanStatus.Pending)]
    [InlineData(MalwareScanStatus.Failed)]
    public async Task Download_FileNotClean_ReturnsStateConflict_WithoutAudit(MalwareScanStatus status)
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var file = _store.SeedFile(_tenantId, requestId, AttachmentFileRules.NewStorageKey(_tenantId), status);

        var result = await _service.DownloadAsync(Owner(), file.Id, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Download_UnknownOrOtherTenantFile_ReturnsNotFound()
    {
        var requestId = _store.AddRequest(_ownerId, _tenantId);
        var file = _store.SeedFile(_tenantId, requestId, AttachmentFileRules.NewStorageKey(_tenantId), MalwareScanStatus.Clean);

        var unknown = await _service.DownloadAsync(Owner(), Guid.NewGuid(), CancellationToken.None);
        var otherTenant = await _service.DownloadAsync(TestFiles.Requester(Guid.NewGuid(), _ownerId), file.Id, CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, unknown.Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, otherTenant.Error?.Failure);
    }
}
