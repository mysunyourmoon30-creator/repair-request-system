using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Files;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Attachments;

internal sealed class TestMalwareScanner : IFileMalwareScanner
{
    public const string InfectedMarker = "INFECTED-TEST-MARKER";

    public async Task<MalwareScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.ASCII);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return text.Contains(InfectedMarker, StringComparison.Ordinal) ? MalwareScanOutcome.Infected : MalwareScanOutcome.Clean;
    }
}

internal sealed class ThrowingFileStorage : IFileStorage
{
    public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken) =>
        throw new IOException("Simulated storage outage.");

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
        throw new IOException("Simulated storage outage.");

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Attachment persistence against LocalDB and a private temporary storage root: metadata + binary + audit, scoped single
/// queries, compensation on database or storage failure, and at-most-once scan completion (RR-ARCH-001 section 11).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class AttachmentPersistenceTests : IAsyncLifetime
{
    private static readonly byte[] PngContent = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[256]];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rr-attachment-tests", Guid.NewGuid().ToString("N"));
    private readonly AuthenticationTestHost _host;

    public AttachmentPersistenceTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _host = NewHost();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AuthenticationTestHost NewHost(Action<IServiceCollection>? extra = null) =>
        new(services =>
        {
            services.Configure<FileStorageOptions>(options => options.RootPath = _root);
            extra?.Invoke(services);
        });

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task<CurrentUser> UserAsync(ScopeWorld world, Site[] sites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(world.TenantId, roles);
        await using (var scope = _host.CreateScope())
        {
            await ScopeWorld.AssignSitesAsync(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>(), world.TenantId, user.Id, sites);
        }

        return new CurrentUser(user.Id, world.TenantId, roles);
    }

    private async Task<Guid> DraftAsync(CurrentUser owner, Site site)
    {
        await using var scope = _host.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>()
            .CreateAsync(new CommandContext(owner, Guid.NewGuid()), new RepairRequestDraftFields(site.Id, null, "Leak", null, null), CancellationToken.None);
        return result.Value!.Id;
    }

    private static AttachmentUpload Png(string name = "site photo.png") =>
        new(name, "image/png", PngContent.Length, () => new MemoryStream(PngContent, writable: false));

    private static async Task<CommandResult<AttachmentDto>> UploadAsync(AuthenticationTestHost host, CurrentUser owner, Guid requestId, AttachmentUpload upload)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestAttachmentService>()
            .UploadAsync(new CommandContext(owner, Guid.NewGuid()), requestId, upload, CancellationToken.None);
    }

    private string[] StoredFiles() =>
        Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task Upload_PersistsMetadataAttachmentAndAudit_AndWritesBinaryUnderOpaqueKey()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);

        var result = await UploadAsync(_host, owner, requestId, Png("../../site photo.png"));

        Assert.True(result.Succeeded);
        var file = await WithDbAsync(db => db.FileAssets.AsNoTracking().SingleAsync(item => item.Id == result.Value!.FileAssetId));
        Assert.Equal(world.TenantId, file.TenantId);
        Assert.Equal("site photo.png", file.FileName);
        Assert.Equal("image/png", file.MimeType);
        Assert.Equal(PngContent.Length, file.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(PngContent)), file.ContentHash);
        Assert.Equal(MalwareScanStatus.Pending, file.MalwareScanStatus);
        Assert.Equal(owner.UserId, file.UploadedBy);
        Assert.Matches(new Regex($"^{world.TenantId:N}/[0-9a-f]{{32}}$"), file.StorageReference);

        var attachment = await WithDbAsync(db => db.RepairRequestAttachments.AsNoTracking().SingleAsync(item => item.FileAssetId == file.Id));
        Assert.Equal(requestId, attachment.RepairRequestId);
        Assert.Equal("REPAIR_REQUEST", attachment.AccessScopeCode);

        Assert.True(await WithDbAsync(db => db.AuditHistory.AnyAsync(audit =>
            audit.EntityId == requestId && audit.ActionCode == AttachmentAudit.AttachmentAddedAction)));

        var storedPath = Path.Combine(_root, file.StorageReference.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(PngContent, await File.ReadAllBytesAsync(storedPath));
        Assert.DoesNotContain("photo", storedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedLookups_AreSingleQueries_AndFollowOwnershipAndRepairRequestScope()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var otherRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var approver = await UserAsync(world, [world.SiteA1], RoleCodes.Approver);
        var adminRequesterElsewhere = await UserAsync(world, [world.SiteA2], RoleCodes.Administrator, RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);
        var fileAssetId = (await UploadAsync(_host, owner, requestId, Png())).Value!.FileAssetId;
        var otherTenant = new CurrentUser(owner.UserId, world.OtherTenantId, [RoleCodes.Requester]);

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
        var none = CancellationToken.None;

        _host.Logs.Clear();
        Assert.Equal(RepairRequestStatus.Draft, await store.GetOwnRequestStatusAsync(owner, requestId, none));
        Assert.Equal(1, _host.Logs.ExecutedDbCommandCount);

        _host.Logs.Clear();
        Assert.NotNull(await store.FindDownloadAsync(owner, fileAssetId, none));
        Assert.Equal(1, _host.Logs.ExecutedDbCommandCount);

        _host.Logs.Clear();
        Assert.Single((await store.ListAsync(owner, requestId, new PageRequest(1, 10), none))!.Items);
        Assert.Equal(3, _host.Logs.ExecutedDbCommandCount);

        Assert.Null(await store.GetOwnRequestStatusAsync(otherRequester, requestId, none));
        Assert.Null(await store.GetOwnRequestStatusAsync(approver, requestId, none));
        Assert.Null(await store.GetOwnRequestStatusAsync(adminRequesterElsewhere, requestId, none));
        Assert.Null(await store.GetOwnRequestStatusAsync(otherTenant, requestId, none));

        Assert.NotNull(await store.FindDownloadAsync(approver, fileAssetId, none));
        Assert.Null(await store.FindDownloadAsync(otherRequester, fileAssetId, none));
        Assert.Null(await store.FindDownloadAsync(adminRequesterElsewhere, fileAssetId, none));
        Assert.Null(await store.FindDownloadAsync(otherTenant, fileAssetId, none));
        Assert.Null(await store.ListAsync(otherRequester, requestId, new PageRequest(1, 10), none));
        Assert.Null(await store.ListAsync(otherTenant, requestId, new PageRequest(1, 10), none));
        Assert.NotNull(await store.ListAsync(approver, requestId, new PageRequest(1, 10), none));
    }

    [Fact]
    public async Task DatabaseFailure_RollsBackMetadata_AndRemovesTheStoredBinary()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);
        await using var failingHost = NewHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailAuditInsertInterceptor())));

        await Assert.ThrowsAnyAsync<Exception>(() => UploadAsync(failingHost, owner, requestId, Png()));

        Assert.False(await WithDbAsync(db => db.FileAssets.AnyAsync(file => file.TenantId == world.TenantId)));
        Assert.False(await WithDbAsync(db => db.RepairRequestAttachments.AnyAsync(attachment => attachment.RepairRequestId == requestId)));
        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task StorageFailure_WritesNoMetadata()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);
        await using var failingHost = NewHost(services => services.Replace(ServiceDescriptor.Singleton<IFileStorage, ThrowingFileStorage>()));

        await Assert.ThrowsAsync<IOException>(() => UploadAsync(failingHost, owner, requestId, Png()));

        Assert.False(await WithDbAsync(db => db.FileAssets.AnyAsync(file => file.TenantId == world.TenantId)));
        Assert.False(await WithDbAsync(db => db.AuditHistory.AnyAsync(audit =>
            audit.EntityId == requestId && audit.ActionCode == AttachmentAudit.AttachmentAddedAction)));
    }

    [Fact]
    public async Task Scan_CompletesAtMostOnce_WithAudit()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);
        var fileAssetId = (await UploadAsync(_host, owner, requestId, Png())).Value!.FileAssetId;
        await using var scanningHost = NewHost(services => services.Replace(ServiceDescriptor.Singleton<IFileMalwareScanner, TestMalwareScanner>()));

        await using (var scope = scanningHost.CreateScope())
        {
            var scans = scope.ServiceProvider.GetRequiredService<FileScanService>();
            Assert.Equal(FileScanResult.Clean, await scans.ScanAsync(fileAssetId, CancellationToken.None));
            Assert.Equal(FileScanResult.NotPending, await scans.ScanAsync(fileAssetId, CancellationToken.None));
        }

        await using (var scope = scanningHost.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();
            var stale = new FileAsset(world.TenantId, "x.png", "image/png", 1, new string('c', 64), "stale", owner.UserId, DateTime.UtcNow);
            typeof(FileAsset).GetProperty(nameof(FileAsset.Id))!.SetValue(stale, fileAssetId);
            stale.MarkFailed();
            Assert.False(await store.TryCompleteScanAsync(stale, AttachmentAudit.ScanCompleted(stale, DateTime.UtcNow, Guid.NewGuid()), CancellationToken.None));
        }

        Assert.Equal(MalwareScanStatus.Clean, await WithDbAsync(db => db.FileAssets.Where(file => file.Id == fileAssetId).Select(file => file.MalwareScanStatus).SingleAsync()));
        Assert.Equal(
            new[] { AttachmentAudit.ScanCleanAction },
            await WithDbAsync(db => db.AuditHistory.Where(audit => audit.EntityId == fileAssetId).Select(audit => audit.ActionCode).ToListAsync()));
    }

    [Fact]
    public async Task DefaultNotConfiguredScanner_LeavesFilesPending()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);
        var fileAssetId = (await UploadAsync(_host, owner, requestId, Png())).Value!.FileAssetId;

        await using (var scope = _host.CreateScope())
        {
            Assert.Equal(FileScanResult.StillPending, await scope.ServiceProvider.GetRequiredService<FileScanService>().ScanAsync(fileAssetId, CancellationToken.None));
        }

        Assert.Equal(MalwareScanStatus.Pending, await WithDbAsync(db => db.FileAssets.Where(file => file.Id == fileAssetId).Select(file => file.MalwareScanStatus).SingleAsync()));
        Assert.False(await WithDbAsync(db => db.AuditHistory.AnyAsync(audit => audit.EntityId == fileAssetId)));
    }

    private async Task SeedAttachmentRowsAsync(CurrentUser owner, Guid requestId, int count)
    {
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        for (var index = 0; index < count; index++)
        {
            var file = new FileAsset(
                owner.TenantId, $"bulk-{index:000}.png", "image/png", 10, new string('d', 64), AttachmentFileRules.NewStorageKey(owner.TenantId), owner.UserId, DateTime.UtcNow);
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(requestId, file.Id, AttachmentFileRules.AccessScopeCode));
        }

        await db.SaveChangesAsync();
    }

    private async Task<(PagedResult<AttachmentDto>? Page, int Commands, List<string> Log)> ListMeasuredAsync(CurrentUser user, Guid requestId, PageRequest paging)
    {
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();

        _host.Logs.Clear();
        var page = await store.ListAsync(user, requestId, paging, CancellationToken.None);
        return (page, _host.Logs.ExecutedDbCommandCount, _host.Logs.Entries.ToList());
    }

    [Fact]
    public async Task AttachmentList_IsPagedInTheDatabase_WithConstantQueryCount_AndReadsNoFileContent()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);

        await SeedAttachmentRowsAsync(owner, requestId, 3);
        var small = await ListMeasuredAsync(owner, requestId, new PageRequest(1, 10));
        await SeedAttachmentRowsAsync(owner, requestId, 42);
        var large = await ListMeasuredAsync(owner, requestId, new PageRequest(1, 10));

        Assert.Equal(3, small.Page!.TotalCount);
        Assert.Equal(3, small.Page.Items.Count);
        Assert.Equal(45, large.Page!.TotalCount);
        Assert.Equal(10, large.Page.Items.Count);

        // Query count does not grow with rows, and the page is cut in SQL.
        Assert.Equal(small.Commands, large.Commands);
        Assert.Equal(3, large.Commands);
        Assert.Contains(large.Log, entry => entry.Contains("OFFSET", StringComparison.Ordinal) && entry.Contains("FETCH NEXT", StringComparison.Ordinal));

        // Complete, non-overlapping, deterministic pages.
        var firstPass = new List<Guid>();
        var secondPass = new List<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            firstPass.AddRange((await ListMeasuredAsync(owner, requestId, new PageRequest(page, 10))).Page!.Items.Select(item => item.AttachmentId));
            secondPass.AddRange((await ListMeasuredAsync(owner, requestId, new PageRequest(page, 10))).Page!.Items.Select(item => item.AttachmentId));
        }

        Assert.Equal(45, firstPass.Distinct().Count());
        Assert.Equal(firstPass, secondPass);

        var beyond = await ListMeasuredAsync(owner, requestId, new PageRequest(6, 10));
        Assert.Empty(beyond.Page!.Items);
        Assert.Equal(45, beyond.Page.TotalCount);

        // Only metadata rows exist and were listed: no file content was ever written or read.
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task NoBusinessAttachmentCountLimit_UploadSucceedsAfterManyAttachments()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA1);
        await SeedAttachmentRowsAsync(owner, requestId, 150);

        var result = await UploadAsync(_host, owner, requestId, Png());

        Assert.True(result.Succeeded);
        var lastPage = await ListMeasuredAsync(owner, requestId, new PageRequest(16, 10));
        Assert.Equal(151, lastPage.Page!.TotalCount);
        Assert.Single(lastPage.Page.Items);
    }

    private sealed class FailAuditInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            FailOnAuditWrite(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            FailOnAuditWrite(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void FailOnAuditWrite(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO [audit_history]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated audit write failure.");
            }
        }
    }
}
