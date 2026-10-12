using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.ApiTests.Attachments;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.ApiTests.WorkEvidence;

/// <summary>
/// API host with its own LocalDB database, a private temporary storage root and the deterministic test scanner. The CLEAN evidence guard is
/// exercised for real here: every Work Summary Submit needs evidence uploaded through the API and scanned by the scan service.
/// </summary>
public class WorkEvidenceApiFactory : AuthApiFactory
{
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "rr-work-evidence-api-tests", Guid.NewGuid().ToString("N"));

    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkEvidenceApiTest;Trusted_Connection=True;TrustServerCertificate=True";

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

internal sealed record EvidenceCaller(Guid UserId, Guid TenantId, string Token);

/// <summary>A tenant with a Work Order IN_PROGRESS, its assigned Technician with a checked-out Work Session, and every actor of the access matrix.</summary>
internal sealed record EvidenceApiWorld(
    Guid TenantId,
    Site SiteA,
    EvidenceCaller Technician,
    EvidenceCaller OtherTechnician,
    EvidenceCaller TeamLead,
    EvidenceCaller Supervisor,
    EvidenceCaller Coordinator,
    EvidenceCaller Approver,
    EvidenceCaller Administrator,
    EvidenceCaller Contact,
    EvidenceCaller OtherRequester,
    EvidenceCaller OutOfSiteTeamLead,
    EvidenceCaller OtherTenantSupervisor,
    Guid WorkOrderId,
    Guid VisitId);

internal sealed class EvidenceApiClient
{
    private readonly WorkEvidenceApiFactory _factory;

    public EvidenceApiClient(WorkEvidenceApiFactory factory)
    {
        _factory = factory;
    }

    public HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<EvidenceCaller> CallerAsync(Guid tenantId, Site[] sites, params string[] roles)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, roles);
        if (sites.Length > 0)
        {
            await _factory.WithDbAsync(db =>
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
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        return new EvidenceCaller(user.Id, tenantId, token);
    }

    private async Task<Site> SiteAsync(Guid tenantId, string code)
    {
        return await _factory.WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, $"CUST-{code}");
            db.Customers.Add(customer);
            var site = new Site(tenantId, customer.Id, code);
            db.Sites.Add(site);
            await db.SaveChangesAsync();
            return site;
        });
    }

    public async Task<EvidenceApiWorld> CreateWorldAsync(bool checkedOut = true)
    {
        var tenantId = Guid.NewGuid();
        var siteA = await SiteAsync(tenantId, "SITE-A");
        var siteB = await SiteAsync(tenantId, "SITE-B");
        var otherTenantId = Guid.NewGuid();
        var otherSite = await SiteAsync(otherTenantId, "SITE-A");

        var technician = await CallerAsync(tenantId, [siteA], RoleCodes.Technician);
        var otherTechnician = await CallerAsync(tenantId, [siteA], RoleCodes.Technician);
        var teamLead = await CallerAsync(tenantId, [siteA], RoleCodes.TeamLead);
        var supervisor = await CallerAsync(tenantId, [siteA], RoleCodes.Supervisor);
        var coordinator = await CallerAsync(tenantId, [siteA], RoleCodes.Coordinator);
        var approver = await CallerAsync(tenantId, [siteA], RoleCodes.Approver);
        var administrator = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var contact = await CallerAsync(tenantId, [siteA], RoleCodes.Requester);
        var otherRequester = await CallerAsync(tenantId, [siteA], RoleCodes.Requester);
        var outOfSiteTeamLead = await CallerAsync(tenantId, [siteB], RoleCodes.TeamLead);
        var otherTenantSupervisor = await CallerAsync(otherTenantId, [otherSite], RoleCodes.Supervisor);

        var (workOrderId, visitId) = await _factory.WithDbAsync(async db =>
        {
            var request = RepairRequestAggregate.CreateDraft(tenantId, otherRequester.UserId);
            var entry = db.RepairRequests.Add(request);
            entry.Property(item => item.SiteId).CurrentValue = siteA.Id;
            await db.SaveChangesAsync();

            var workOrder = WorkOrder.Create(tenantId, request.Id, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow);
            db.WorkOrders.Add(workOrder);
            await db.SaveChangesAsync();
            workOrder.Schedule(Guid.NewGuid());
            workOrder.BeginWork();
            await db.SaveChangesAsync();

            // WO-008: the exact Acceptance Contact (designated by Submit for Acceptance in the real flow).
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [acceptance_contact_id] = {contact.UserId} WHERE [work_order_id] = {workOrder.Id}");

            var visit = ServiceVisit.Create(
                tenantId, workOrder.Id, ServiceVisitType.Initial, Guid.NewGuid(), technician.UserId, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(3));
            visit.CheckIn();
            db.ServiceVisits.Add(visit);
            var session = WorkSession.Create(tenantId, visit.Id, technician.UserId, DateTime.UtcNow.AddHours(-2));
            if (checkedOut)
            {
                session.CheckOut(DateTime.UtcNow.AddHours(-1));
            }

            db.WorkSessions.Add(session);
            await db.SaveChangesAsync();
            return (workOrder.Id, visit.Id);
        });

        return new EvidenceApiWorld(
            tenantId, siteA, technician, otherTechnician, teamLead, supervisor, coordinator, approver, administrator, contact, otherRequester,
            outOfSiteTeamLead, otherTenantSupervisor, workOrderId, visitId);
    }

    /// <summary>A second IN_PROGRESS Work Order (B) of the same tenant and Site, with its own Visit and checked-out Work Session of the world's Technician.</summary>
    public async Task<(Guid WorkOrderId, Guid VisitId)> AddWorkOrderInSameTenantAsync(EvidenceApiWorld world)
    {
        return await _factory.WithDbAsync(async db =>
        {
            var request = RepairRequestAggregate.CreateDraft(world.TenantId, world.OtherRequester.UserId);
            var entry = db.RepairRequests.Add(request);
            entry.Property(item => item.SiteId).CurrentValue = world.SiteA.Id;
            await db.SaveChangesAsync();

            var workOrder = WorkOrder.Create(world.TenantId, request.Id, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow);
            db.WorkOrders.Add(workOrder);
            await db.SaveChangesAsync();
            workOrder.Schedule(Guid.NewGuid());
            workOrder.BeginWork();
            await db.SaveChangesAsync();

            var visit = ServiceVisit.Create(
                world.TenantId, workOrder.Id, ServiceVisitType.Initial, Guid.NewGuid(), world.Technician.UserId, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(3));
            visit.CheckIn();
            db.ServiceVisits.Add(visit);
            var session = WorkSession.Create(world.TenantId, visit.Id, world.Technician.UserId, DateTime.UtcNow.AddHours(-2));
            session.CheckOut(DateTime.UtcNow.AddHours(-1));
            db.WorkSessions.Add(session);
            await db.SaveChangesAsync();
            return (workOrder.Id, visit.Id);
        });
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, EvidenceCaller? caller, HttpContent? content = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (caller is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return CreateClient().SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string url, EvidenceCaller? caller) => SendAsync(HttpMethod.Get, url, caller);

    public Task<HttpResponseMessage> UploadAsync(
        EvidenceCaller? caller, Guid workOrderId, byte[] content, string fileName = "evidence.png", string contentType = "image/png", string? type = "AFTER_WORK")
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var form = new MultipartFormDataContent { { file, "file", fileName } };
        if (type is not null)
        {
            form.Add(new StringContent(type), "evidenceType");
        }

        return SendAsync(HttpMethod.Post, $"/api/v1/work-orders/{workOrderId}/evidence", caller, form);
    }

    public async Task<string> ETagAsync(Guid workOrderId)
    {
        var rowVersion = await _factory.WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == workOrderId).Select(item => item.RowVersion).SingleAsync());
        return $"\"{Convert.ToBase64String(rowVersion)}\"";
    }

    /// <summary>Submit Work Summary naming exactly the given evidence upload ids (the request's <c>evidenceIds</c>); null sends the property as null.</summary>
    public async Task<HttpResponseMessage> SubmitSummaryAsync(
        EvidenceCaller caller, Guid workOrderId, IEnumerable<Guid>? evidenceIds, string text = "Replaced the worn bearing.") =>
        await SubmitRawAsync(caller, workOrderId, new { summaryText = text, repairOutcomeCode = "REPAIRED", evidenceIds }, await ETagAsync(workOrderId));

    /// <summary>Submit Work Summary with an arbitrary JSON body (a missing property, extra hostile properties, ...).</summary>
    public Task<HttpResponseMessage> SubmitRawAsync(EvidenceCaller caller, Guid workOrderId, object body, string? ifMatch) =>
        SendAsync(HttpMethod.Post, $"/api/v1/work-orders/{workOrderId}/submit-work-summary", caller, JsonContent.Create(body), ifMatch);

    public async Task<FileScanResult> ScanAsync(Guid evidenceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var fileAssetId = await db.WorkEvidenceUploads.Where(item => item.Id == evidenceId).Select(item => item.FileAssetId).SingleAsync();
        return await scope.ServiceProvider.GetRequiredService<FileScanService>().ScanAsync(fileAssetId, CancellationToken.None);
    }

    public Task SetScanStatusAsync(Guid evidenceId, string status) =>
        _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [file_asset] SET [malware_scan_status] = {status}
            WHERE [file_asset_id] = (SELECT [file_asset_id] FROM [work_evidence_upload] WHERE [work_evidence_id] = {evidenceId})
            """));

    public async Task<Guid> UploadOkAsync(EvidenceCaller caller, Guid workOrderId, byte[]? content = null, string? type = "AFTER_WORK")
    {
        var response = await UploadAsync(caller, workOrderId, content ?? Png(Guid.NewGuid().ToString()), type: type);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("evidenceId").GetGuid();
    }

    public async Task<Guid> CleanEvidenceAsync(EvidenceCaller caller, Guid workOrderId, string? type = "AFTER_WORK")
    {
        var id = await UploadOkAsync(caller, workOrderId, type: type);
        Assert.Equal(FileScanResult.Clean, await ScanAsync(id));
        return id;
    }

    public static byte[] Png(string payload = "evidence-bytes") =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. System.Text.Encoding.ASCII.GetBytes(payload)];

    public static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    public static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
