using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.RepairRequests;
using Xunit.Abstractions;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.ApiTests.Approvals;

/// <summary>
/// API host whose post-Submit routing always throws and whose final-state read-back can be made to throw, to prove that a
/// committed Submit is never reported as failed (DEC-PRE-S1-007R-01).
/// </summary>
public sealed class PostSubmitFailureApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_PostSubmitFailureApiTest;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>When true, <see cref="IRepairRequestDraftStore.GetAsync"/> throws; every other draft-store call works.</summary>
    public bool FailReadBack { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<ISubmittedRequestRouter, ThrowingSubmittedRequestRouter>());

            var original = services.Last(descriptor => descriptor.ServiceType == typeof(IRepairRequestDraftStore));
            services.Remove(original);
            services.AddScoped<IRepairRequestDraftStore>(provider => new ReadBackFailingDraftStore(
                (IRepairRequestDraftStore)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
                this));
        });
    }

    private sealed class ThrowingSubmittedRequestRouter : ISubmittedRequestRouter
    {
        public Task<CommandResult<RoutingResultDto>> RouteSubmittedAsync(
            CommandContext context,
            Guid repairRequestId,
            byte[] submittedRowVersion,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated post-commit routing failure");

        public void DiscardPendingChanges()
        {
        }
    }

    private sealed class ReadBackFailingDraftStore(IRepairRequestDraftStore inner, PostSubmitFailureApiFactory factory) : IRepairRequestDraftStore
    {
        public Task<RepairRequestDraftDto?> GetAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
            factory.FailReadBack
                ? throw new InvalidOperationException("Simulated final-state read-back failure")
                : inner.GetAsync(user, repairRequestId, cancellationToken);

        public Task<RepairRequestAggregate?> FindOwnAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
            inner.FindOwnAsync(user, repairRequestId, cancellationToken);

        public Task<DraftSelection> GetDraftSelectionAsync(CurrentUser user, DraftSelectionQuery query, CancellationToken cancellationToken) =>
            inner.GetDraftSelectionAsync(user, query, cancellationToken);

        public void Add(RepairRequestAggregate repairRequest) => inner.Add(repairRequest);

        public void AddAudit(Domain.Auditing.AuditHistory audit) => inner.AddAudit(audit);

        public Task<RepairRequestSaveOutcome> SaveChangesAsync(RepairRequestAggregate repairRequest, byte[]? expectedRowVersion, CancellationToken cancellationToken) =>
            inner.SaveChangesAsync(repairRequest, expectedRowVersion, cancellationToken);
    }
}

/// <summary>
/// A committed Submit whose post-commit routing and final-state read-back both throw still answers 200 with the committed
/// SUBMITTED state (Request No, submittedAt = SLA start, committed ETag), and the Submit is never repeated.
/// </summary>
public sealed class PostSubmitRoutingFailureEndpointsTests : IClassFixture<PostSubmitFailureApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly PostSubmitFailureApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public PostSubmitRoutingFailureEndpointsTests(PostSubmitFailureApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Submit_WhenPostCommitRoutingAndReadBackThrow_Returns200Submitted_AndTheSubmitIsNotRepeated()
    {
        var tenantId = Guid.NewGuid();
        var site = await _factory.WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, "CUST-1");
            db.Customers.Add(customer);
            var siteA = new Site(tenantId, customer.Id, "SITE-A");
            db.Sites.Add(siteA);
            await db.SaveChangesAsync();
            return siteA;
        });
        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedTenantAsync(tenantId, CancellationToken.None);
        }

        var (requesterId, token) = await RequesterAsync(tenantId, site);
        var created = await SendAsync(HttpMethod.Post, Requests, token, new
        {
            siteId = site.Id,
            requestCategoryCode = "ELECTRICAL",
            priorityCode = "HIGH",
            requestContactId = requesterId,
            description = "Pump leaking",
            preferredStartAt = "2026-09-20T08:00:00Z",
            preferredEndAt = "2026-09-20T10:00:00Z"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var draftETag = created.Headers.ETag!.Tag;
        await AttachCleanPhotoAsync(tenantId, requesterId, id);

        _factory.FailReadBack = true;
        HttpResponseMessage submitted;
        try
        {
            submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", token, new { }, draftETag);
        }
        finally
        {
            _factory.FailReadBack = false;
        }

        // The committed Submit is reported as the committed SUBMITTED state, never as a failed or rolled-back Submit.
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var raw = await submitted.Content.ReadAsStringAsync();
        _output.WriteLine($"HTTP {(int)submitted.StatusCode} {submitted.StatusCode}");
        _output.WriteLine($"Content-Type: {submitted.Content.Headers.ContentType}");
        _output.WriteLine($"ETag: {submitted.Headers.ETag}");
        _output.WriteLine(raw);

        // The full RR-API-005 response contract: the committed SUBMITTED request, nothing from routing.
        Assert.Equal("application/json", submitted.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(raw);
        var body = document.RootElement;
        Assert.Equal(
            ["createdBy", "description", "equipmentId", "id", "preferredEndAt", "preferredStartAt", "priorityCode", "requestCategoryCode", "requestContactId", "requestNo", "rowVersion", "siteId", "status", "submittedAt"],
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(site.Id, body.GetProperty("siteId").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("equipmentId").ValueKind);
        Assert.Equal("ELECTRICAL", body.GetProperty("requestCategoryCode").GetString());
        Assert.Equal("HIGH", body.GetProperty("priorityCode").GetString());
        Assert.Equal(requesterId, body.GetProperty("requestContactId").GetGuid());
        Assert.Equal("Pump leaking", body.GetProperty("description").GetString());
        Assert.Equal(requesterId, body.GetProperty("createdBy").GetGuid());
        Assert.Equal(submitted.Headers.ETag!.Tag, $"\"{body.GetProperty("rowVersion").GetString()}\"");
        Assert.Equal("SUBMITTED", body.GetProperty("status").GetString());
        var requestNo = body.GetProperty("requestNo").GetString();
        Assert.Matches(@"^RR-\d{4}-000001$", requestNo);

        var stored = await _factory.WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == id));
        Assert.Equal("SUBMITTED", RepairRequestStatusCodes.ToCode(stored.Status));
        Assert.Equal(requestNo, stored.RequestNo);
        Assert.NotNull(stored.SubmittedAt);
        Assert.Equal(stored.SubmittedAt!.Value, body.GetProperty("submittedAt").GetDateTime());
        Assert.Equal(requesterId, stored.SubmittedBy);
        Assert.Equal($"\"{Convert.ToBase64String(stored.RowVersion)}\"", submitted.Headers.ETag!.Tag);

        Assert.Equal(1, await SubmitAuditCountAsync(id));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.RequestNumberCounters.CountAsync(counter => counter.TenantId == tenantId)));
        Assert.False(await _factory.WithDbAsync(db => db.RepairRequestApprovals.AnyAsync(approval => approval.RepairRequestId == id)));
        Assert.False(await _factory.WithDbAsync(db => db.AuditHistory.AnyAsync(audit => audit.EntityId == id && audit.ActionCode == RepairRequestRoutingAudit.RoutedAction)));

        // Repeating the Submit never creates a second successful Submit.
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", token, new { }, submitted.Headers.ETag.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", token, new { }, draftETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        Assert.Equal(1, await SubmitAuditCountAsync(id));
        Assert.Equal(requestNo, await _factory.WithDbAsync(db => db.RepairRequests.Where(request => request.Id == id).Select(request => request.RequestNo).SingleAsync()));
    }

    private Task<int> SubmitAuditCountAsync(Guid requestId) =>
        _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == requestId && audit.ActionCode == RepairRequestAudit.SubmittedAction));

    private async Task<(Guid UserId, string Token)> RequesterAsync(Guid tenantId, Site site)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, RoleCodes.Requester);
        await _factory.WithDbAsync(db =>
        {
            db.UserSiteScopes.Add(new UserSiteScope(tenantId, user.Id, site.Id));
            return db.SaveChangesAsync();
        });

        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = AuthApiFactory.ValidPassword });
        response.EnsureSuccessStatusCode();
        return (user.Id, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
    }

    private Task AttachCleanPhotoAsync(Guid tenantId, Guid ownerId, Guid requestId) =>
        _factory.WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", ownerId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(requestId, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }
}
