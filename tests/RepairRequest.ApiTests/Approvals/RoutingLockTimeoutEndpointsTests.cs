using System.Diagnostics;
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
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Approvals;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;
using Xunit.Abstractions;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.ApiTests.Approvals;

/// <summary>API host with its own database and a short routing-lock wait (DEC-PRE-S1-007R-07 lock wait).</summary>
public sealed class RoutingLockTimeoutApiFactory : AuthApiFactory
{
    public const int LockTimeoutMilliseconds = 300;

    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_RoutingLockTimeoutApiTest;Trusted_Connection=True;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton(new RepairRequestRoutingLockOptions { TimeoutMilliseconds = LockTimeoutMilliseconds })));
    }
}

/// <summary>
/// ROUTING-API-002 under real lock contention: while another database session holds the request's routing lock, Admin
/// Retry waits only until the configured limit and answers the approved 409 CONCURRENCY_CONFLICT problem with the
/// correlation id, writing nothing; once the holder releases, the same Retry succeeds.
/// </summary>
public sealed class RoutingLockTimeoutEndpointsTests : IClassFixture<RoutingLockTimeoutApiFactory>
{
    private const string CorrelationHeader = "X-Correlation-ID";

    private readonly RoutingLockTimeoutApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public RoutingLockTimeoutEndpointsTests(RoutingLockTimeoutApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task RetryRouting_WhenAnotherSessionHoldsTheRoutingLock_Returns409ConcurrencyConflict_WritesNothing_AndSucceedsAfterRelease()
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

        var adminToken = await LoginAsync(await _factory.CreateUserInTenantAsync(tenantId, RoleCodes.Administrator));
        var approver = await UserWithSiteAsync(tenantId, site, RoleCodes.Approver);
        var requester = await UserWithSiteAsync(tenantId, site, RoleCodes.Requester);
        var submittedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        var (requestId, rowVersion) = await _factory.WithDbAsync(async db =>
        {
            var start = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
            var request = RepairRequestAggregate.CreateDraft(tenantId, requester);
            request.EditDraft(site.Id, null, "ELECTRICAL", "HIGH", requester, "Legacy request", start, start.AddHours(2));
            request.Submit("RR-2026-900001", requester, submittedAt, null);
            db.RepairRequests.Add(request);

            var route = ApprovalRoute.Create(tenantId, "ELECTRICAL", site.Id);
            db.ApprovalRoutes.Add(route);
            db.ApprovalRouteSteps.Add(ApprovalRouteStep.CreateFirstStep(route, RoleCodes.Approver, null));
            await db.SaveChangesAsync();
            return (request.Id, request.RowVersion);
        });

        var retryUrl = $"/api/v1/repair-requests/{requestId}/retry-routing";
        var etag = $"\"{Convert.ToBase64String(rowVersion)}\"";
        var correlationId = Guid.NewGuid();

        HttpResponseMessage contended;
        var waited = Stopwatch.StartNew();
        await using (var holderScope = _factory.Services.CreateAsyncScope())
        {
            // Another operation (a separate database session) holds this request's routing lock in an open transaction.
            var holder = holderScope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
            await using var holderTransaction = await holder.Database.BeginTransactionAsync();
            var resource = RepairRequestRoutingLock.Resource(requestId);
            var granted = (await holder.Database.SqlQuery<int>($"""
                DECLARE @lock_result int;
                EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
                SELECT @lock_result AS [Value];
                """).ToListAsync()).Single();
            Assert.True(granted >= 0, $"The test could not take the routing lock ({granted}).");

            waited.Restart();
            contended = await SendAsync(retryUrl, adminToken, etag, correlationId);
            waited.Stop();

            // The holder releases normally.
            await holderTransaction.RollbackAsync();
        }

        var raw = await contended.Content.ReadAsStringAsync();
        _output.WriteLine($"HTTP {(int)contended.StatusCode} {contended.StatusCode}");
        _output.WriteLine($"Content-Type: {contended.Content.Headers.ContentType}");
        _output.WriteLine($"{CorrelationHeader}: {string.Join(",", contended.Headers.GetValues(CorrelationHeader))}");
        _output.WriteLine($"Waited: {waited.ElapsedMilliseconds} ms (limit {RoutingLockTimeoutApiFactory.LockTimeoutMilliseconds} ms)");
        _output.WriteLine(raw);

        // Controlled, deterministic response: the approved concurrency problem, never a 500.
        Assert.Equal(HttpStatusCode.Conflict, contended.StatusCode);
        Assert.Equal("application/problem+json", contended.Content.Headers.ContentType?.MediaType);
        using (var document = JsonDocument.Parse(raw))
        {
            Assert.Equal("CONCURRENCY_CONFLICT", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(409, document.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(correlationId, document.RootElement.GetProperty("correlationId").GetGuid());
        }

        Assert.Equal(correlationId.ToString(), Assert.Single(contended.Headers.GetValues(CorrelationHeader)));

        // It waited for the configured limit only, not indefinitely.
        Assert.InRange(waited.Elapsed, TimeSpan.FromMilliseconds(RoutingLockTimeoutApiFactory.LockTimeoutMilliseconds - 50), TimeSpan.FromSeconds(15));

        // Nothing was written.
        var stored = await _factory.WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));
        Assert.Equal(RepairRequestStatus.Submitted, stored.Status);
        Assert.Equal("RR-2026-900001", stored.RequestNo);
        Assert.Equal(submittedAt, stored.SubmittedAt);
        Assert.Equal(rowVersion, stored.RowVersion);
        Assert.False(await _factory.WithDbAsync(db => db.RepairRequestApprovals.AnyAsync(approval => approval.RepairRequestId == requestId)));
        Assert.Equal(0, await RoutingAuditCountAsync(requestId, RepairRequestRoutingAudit.RoutedAction));
        Assert.Equal(0, await RoutingAuditCountAsync(requestId, RepairRequestRoutingAudit.RoutingFailedAction));

        // After the release, the same Retry with the same ETag succeeds.
        var routed = await SendAsync(retryUrl, adminToken, etag, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        using (var document = JsonDocument.Parse(await routed.Content.ReadAsStringAsync()))
        {
            Assert.Equal("UNDER_REVIEW", document.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(approver, await _factory.WithDbAsync(db => db.RepairRequestApprovals
            .Where(approval => approval.RepairRequestId == requestId)
            .Select(approval => approval.AssignedApproverId)
            .SingleAsync()));
        Assert.Equal(1, await RoutingAuditCountAsync(requestId, RepairRequestRoutingAudit.RoutedAction));
    }

    private Task<int> RoutingAuditCountAsync(Guid requestId, string actionCode) =>
        _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == requestId && audit.ActionCode == actionCode));

    private async Task<Guid> UserWithSiteAsync(Guid tenantId, Site site, string role)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, role);
        await _factory.WithDbAsync(db =>
        {
            db.UserSiteScopes.Add(new UserSiteScope(tenantId, user.Id, site.Id));
            return db.SaveChangesAsync();
        });
        return user.Id;
    }

    private async Task<string> LoginAsync(ApplicationUser user)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = AuthApiFactory.ValidPassword });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private Task<HttpResponseMessage> SendAsync(string url, string token, string etag, Guid correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.Add(CorrelationHeader, correlationId.ToString());
        return CreateClient().SendAsync(request);
    }
}
