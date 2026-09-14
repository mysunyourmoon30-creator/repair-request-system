using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.ApiTests.Attachments;

/// <summary>Scans clean unless the content carries the test marker.</summary>
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

/// <summary>API host with its own LocalDB database, a private temporary storage root and a deterministic test scanner.</summary>
public sealed class AttachmentApiFactory : AuthApiFactory
{
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "rr-attachment-api-tests", Guid.NewGuid().ToString("N"));

    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_AttachmentApiTest;Trusted_Connection=True;TrustServerCertificate=True";

    protected override IDictionary<string, string?> TestSettings =>
        new Dictionary<string, string?>(base.TestSettings) { ["FileStorage:RootPath"] = StorageRoot };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IFileMalwareScanner, TestMalwareScanner>()));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}

/// <summary>
/// Attachment upload (FILE-API-001) and download (FILE-API-002) end to end: REQUESTER owner + DRAFT, Repair Request business
/// scope (S1-003/S1-005 incl. the multi-role fix), file policy, private opaque storage, CLEAN-only download with safe
/// headers, and audit (UC-RR-001; TC-RR-002; TC-SEC-001; RR-ARCH-001 section 11).
/// </summary>
public sealed class AttachmentEndpointsTests : IClassFixture<AttachmentApiFactory>
{
    private static readonly byte[] PngContent = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[512]];

    private readonly AttachmentApiFactory _factory;

    public AttachmentEndpointsTests(AttachmentApiFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string> NonRequesterRoles =>
    [
        RoleCodes.Approver,
        RoleCodes.Coordinator,
        RoleCodes.Technician,
        RoleCodes.TeamLead,
        RoleCodes.Supervisor,
        RoleCodes.Administrator
    ];

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record World(Guid TenantId, Site SiteA, Site SiteB, Guid OtherTenantId, Site OtherTenantSite);

    // ---------------- Upload ----------------

    [Fact]
    public async Task Upload_ValidPng_Returns201Pending_WithOpaqueStorage_Audit_AndDetailListing()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        var correlationId = Guid.NewGuid();

        var response = await UploadAsync(owner, requestId, "site photo.png", "image/png", PngContent, correlationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        var fileAssetId = body.GetProperty("fileAssetId").GetGuid();
        Assert.Equal("site photo.png", body.GetProperty("fileName").GetString());
        Assert.Equal("image/png", body.GetProperty("mimeType").GetString());
        Assert.Equal(PngContent.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("PENDING", body.GetProperty("malwareScanStatus").GetString());
        Assert.False(body.TryGetProperty("storageReference", out _));
        Assert.Equal($"/api/v1/files/{fileAssetId}", response.Headers.Location!.OriginalString);

        var file = await WithDbAsync(db => db.FileAssets.AsNoTracking().SingleAsync(item => item.Id == fileAssetId));
        Assert.Matches(new Regex($"^{world.TenantId:N}/[0-9a-f]{{32}}$"), file.StorageReference);
        var storedPath = Path.Combine(_factory.StorageRoot, file.StorageReference.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(PngContent, await File.ReadAllBytesAsync(storedPath));

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(item =>
            item.EntityId == requestId && item.ActionCode == AttachmentAudit.AttachmentAddedAction));
        Assert.Equal(owner.UserId, audit.ActorId);
        Assert.Equal(correlationId, audit.CorrelationId);

        // Draft detail keeps its S1-005 contract; attachments are listed through the paged endpoint.
        var detail = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/repair-requests/{requestId}", owner));
        Assert.False(detail.TryGetProperty("attachments", out _));

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/repair-requests/{requestId}/attachments", owner));
        var listed = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(fileAssetId, listed.GetProperty("fileAssetId").GetGuid());
        Assert.Equal("PENDING", listed.GetProperty("malwareScanStatus").GetString());
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Upload_WithoutToken_Returns401()
    {
        var response = await UploadAsync(null, Guid.NewGuid(), "a.png", "image/png", PngContent);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [MemberData(nameof(NonRequesterRoles))]
    public async Task Upload_ByNonRequesterRole_Returns403(string role)
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        var caller = await CallerAsync(world.TenantId, [world.SiteA], role);

        var response = await UploadAsync(caller, requestId, "a.png", "image/png", PngContent);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ACCESS_DENIED");
        Assert.False(await WithDbAsync(db => db.FileAssets.AnyAsync(file => file.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task Upload_ToRequestsTheCallerDoesNotOwnOrCannotSee_Returns404IdenticalToUnknown()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var otherRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var approverRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver, RoleCodes.Requester);
        var adminRequesterElsewhere = await CallerAsync(world.TenantId, [world.SiteB], RoleCodes.Administrator, RoleCodes.Requester);
        var otherTenantRequester = await CallerAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);

        var expected = await NotFoundShapeAsync(await UploadAsync(owner, Guid.NewGuid(), "a.png", "image/png", PngContent));
        Assert.Equal(expected, await NotFoundShapeAsync(await UploadAsync(otherRequester, requestId, "a.png", "image/png", PngContent)));
        Assert.Equal(expected, await NotFoundShapeAsync(await UploadAsync(approverRequester, requestId, "a.png", "image/png", PngContent)));
        Assert.Equal(expected, await NotFoundShapeAsync(await UploadAsync(adminRequesterElsewhere, requestId, "a.png", "image/png", PngContent)));
        Assert.Equal(expected, await NotFoundShapeAsync(await UploadAsync(otherTenantRequester, requestId, "a.png", "image/png", PngContent)));
        Assert.False(await WithDbAsync(db => db.RepairRequestAttachments.AnyAsync(attachment => attachment.RepairRequestId == requestId)));
    }

    [Fact]
    public async Task Upload_AdministratorRequester_IsLimitedToAssignedBusinessSites()
    {
        var world = await WorldAsync();
        var adminRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator, RoleCodes.Requester);
        var requestId = await DraftAsync(adminRequester, world.SiteA);

        var allowed = await UploadAsync(adminRequester, requestId, "a.png", "image/png", PngContent);
        await WithDbAsync(db => db.UserSiteScopes.Where(scope => scope.UserId == adminRequester.UserId).ExecuteDeleteAsync());
        var afterUnassignment = await UploadAsync(adminRequester, requestId, "b.png", "image/png", PngContent);

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterUnassignment.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenRequestIsNotDraft_Returns409()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == requestId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Status, RepairRequestStatus.Submitted)));

        var response = await UploadAsync(owner, requestId, "a.png", "image/png", PngContent);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    public static TheoryData<string, string, byte[]> RejectedFiles => new()
    {
        { "malware.exe", "application/octet-stream", PngContent },
        { "animation.gif", "image/gif", [.. "GIF89a"u8.ToArray(), .. new byte[32]] },
        { "fake.pdf", "application/pdf", PngContent },
        { "photo.png", "application/pdf", PngContent },
        { "photo.png", "image/png", [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[32]] },
        { "empty.png", "image/png", [] }
    };

    [Theory]
    [MemberData(nameof(RejectedFiles))]
    public async Task Upload_InvalidFile_Returns422_AndStoresNothing(string fileName, string contentType, byte[] content)
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);

        var response = await UploadAsync(owner, requestId, fileName, contentType, content);

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("file", out _));
        Assert.False(await WithDbAsync(db => db.FileAssets.AnyAsync(file => file.TenantId == world.TenantId)));
        Assert.False(Directory.Exists(Path.Combine(_factory.StorageRoot, world.TenantId.ToString("N"))));
    }

    [Fact]
    public async Task Upload_LargerThan10MB_Returns422()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        byte[] content = [.. PngContent.AsSpan(0, 8), .. new byte[AttachmentFileRules.MaxSizeBytes - 7]];

        var response = await UploadAsync(owner, requestId, "large.png", "image/png", content);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.False(await WithDbAsync(db => db.FileAssets.AnyAsync(file => file.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task Upload_PathTraversalFileName_KeepsOnlySanitizedDisplayName()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);

        var response = await UploadAsync(owner, requestId, "../../../../etc/evil.png", "image/png", PngContent);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fileAssetId = (await JsonAsync(response)).GetProperty("fileAssetId").GetGuid();
        var file = await WithDbAsync(db => db.FileAssets.AsNoTracking().SingleAsync(item => item.Id == fileAssetId));
        Assert.Equal("evil.png", file.FileName);
        Assert.DoesNotContain("evil", file.StorageReference, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(_factory.StorageRoot, world.TenantId.ToString("N"))));
    }

    [Fact]
    public async Task Upload_WithoutFileField_Returns400()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        using var form = new MultipartFormDataContent { { new StringContent("value"), "note" } };

        var response = await SendAsync(HttpMethod.Post, $"/api/v1/repair-requests/{requestId}/attachments", owner, form);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    // ---------------- List ----------------

    [Fact]
    public async Task ListAttachments_IsPaged_ClampedToTheConfiguredMaximum_AndValidated()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        for (var index = 0; index < 3; index++)
        {
            await UploadedFileAsync(owner, requestId, $"photo-{index}.png", PngContent);
        }

        var url = $"/api/v1/repair-requests/{requestId}/attachments";
        var first = await JsonAsync(await SendAsync(HttpMethod.Get, $"{url}?page=1&pageSize=2", owner));
        var second = await JsonAsync(await SendAsync(HttpMethod.Get, $"{url}?page=2&pageSize=2", owner));
        var clamped = await JsonAsync(await SendAsync(HttpMethod.Get, $"{url}?pageSize=1000", owner));

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal(2, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Empty(
            first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("attachmentId").GetGuid())
                .Intersect(second.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("attachmentId").GetGuid())));
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, clamped.GetProperty("items").GetArrayLength());
        Assert.False(clamped.GetProperty("items")[0].TryGetProperty("storageReference", out _));

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{url}?page=0", owner), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{url}?pageSize=0", owner), HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task ListAttachments_FollowsRepairRequestScope()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var approver = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver);
        var otherRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var otherTenantApprover = await CallerAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Approver);
        var administrator = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator);
        var requestId = await DraftAsync(owner, world.SiteA);
        await UploadedFileAsync(owner, requestId, "photo.png", PngContent);
        var url = $"/api/v1/repair-requests/{requestId}/attachments";

        var viewer = await SendAsync(HttpMethod.Get, url, approver);

        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        Assert.Equal(1, (await JsonAsync(viewer)).GetProperty("totalCount").GetInt32());

        var expected = await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, $"/api/v1/repair-requests/{Guid.NewGuid()}/attachments", owner));
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, otherRequester)));
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, otherTenantApprover)));
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, url, administrator), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, url, null), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    // ---------------- Download ----------------

    [Fact]
    public async Task Download_CleanFile_StreamsContentWithSafeHeaders_AndAuditsAccess()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        var fileAssetId = await UploadedFileAsync(owner, requestId, "ใบแจ้งซ่อม.png", PngContent);
        Assert.Equal(FileScanResult.Clean, await ScanAsync(fileAssetId));

        var response = await SendAsync(HttpMethod.Get, $"/api/v1/files/{fileAssetId}", owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PngContent, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("ใบแจ้งซ่อม.png", response.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(response.Headers.CacheControl!.NoStore);

        Assert.True(await WithDbAsync(db => db.AuditHistory.AnyAsync(audit =>
            audit.EntityId == requestId && audit.ActionCode == AttachmentAudit.AttachmentDownloadedAction && audit.ActorId == owner.UserId)));
    }

    [Fact]
    public async Task Download_PendingOrFailedFile_Returns409()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var requestId = await DraftAsync(owner, world.SiteA);
        var pending = await UploadedFileAsync(owner, requestId, "pending.png", PngContent);
        var infected = await UploadedFileAsync(owner, requestId, "infected.png", [.. PngContent, .. Encoding.ASCII.GetBytes(TestMalwareScanner.InfectedMarker)]);
        Assert.Equal(FileScanResult.Failed, await ScanAsync(infected));

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"/api/v1/files/{pending}", owner), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"/api/v1/files/{infected}", owner), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(MalwareScanStatus.Failed, await WithDbAsync(db => db.FileAssets.Where(file => file.Id == infected).Select(file => file.MalwareScanStatus).SingleAsync()));
    }

    [Fact]
    public async Task Download_FollowsRepairRequestScope()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var approver = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver);
        var otherRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var adminRequesterElsewhere = await CallerAsync(world.TenantId, [world.SiteB], RoleCodes.Administrator, RoleCodes.Requester);
        var otherTenantApprover = await CallerAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Approver);
        var administrator = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator);
        var requestId = await DraftAsync(owner, world.SiteA);
        var fileAssetId = await UploadedFileAsync(owner, requestId, "photo.png", PngContent);
        await ScanAsync(fileAssetId);
        var url = $"/api/v1/files/{fileAssetId}";

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, url, approver)).StatusCode);

        var expected = await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, $"/api/v1/files/{Guid.NewGuid()}", owner));
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, otherRequester)));
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, adminRequesterElsewhere)));
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, otherTenantApprover)));
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, url, administrator), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, url, null), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    // ---------------- Helpers ----------------

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private Task<World> WorldAsync()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        return WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, "CUST-1");
            var otherCustomer = new Customer(otherTenantId, "CUST-1");
            db.Customers.AddRange(customer, otherCustomer);
            var siteA = new Site(tenantId, customer.Id, "SITE-A");
            var siteB = new Site(tenantId, customer.Id, "SITE-B");
            var otherTenantSite = new Site(otherTenantId, otherCustomer.Id, "SITE-A");
            db.Sites.AddRange(siteA, siteB, otherTenantSite);
            await db.SaveChangesAsync();
            return new World(tenantId, siteA, siteB, otherTenantId, otherTenantSite);
        });
    }

    private async Task<Caller> CallerAsync(Guid tenantId, Site[] sites, params string[] roles)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, roles);
        if (sites.Length > 0)
        {
            await WithDbAsync(db =>
            {
                foreach (var site in sites)
                {
                    db.UserSiteScopes.Add(new UserSiteScope(tenantId, user.Id, site.Id));
                }

                return db.SaveChangesAsync();
            });
        }

        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = AuthApiFactory.ValidPassword });
        response.EnsureSuccessStatusCode();
        return new Caller(user.Id, tenantId, (await JsonAsync(response)).GetProperty("accessToken").GetString()!);
    }

    private async Task<Guid> DraftAsync(Caller owner, Site site)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/repair-requests", owner, JsonContent.Create(new { siteId = site.Id, description = "Leak" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> UploadAsync(Caller? caller, Guid requestId, string fileName, string contentType, byte[] content, Guid? correlationId = null)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var form = new MultipartFormDataContent { { file, "file", fileName } };
        return SendAsync(HttpMethod.Post, $"/api/v1/repair-requests/{requestId}/attachments", caller, form, correlationId);
    }

    private async Task<Guid> UploadedFileAsync(Caller owner, Guid requestId, string fileName, byte[] content)
    {
        var response = await UploadAsync(owner, requestId, fileName, "image/png", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("fileAssetId").GetGuid();
    }

    private async Task<FileScanResult> ScanAsync(Guid fileAssetId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FileScanService>().ScanAsync(fileAssetId, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Caller? caller, HttpContent? content = null, Guid? correlationId = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (caller is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-ID", correlationId.Value.ToString());
        }

        return await CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static async Task<string> NotFoundShapeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        node.Remove("correlationId");
        node.Remove("traceId");
        return node.ToJsonString();
    }

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task WithDbAsync(Func<RepairRequestDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }
}
