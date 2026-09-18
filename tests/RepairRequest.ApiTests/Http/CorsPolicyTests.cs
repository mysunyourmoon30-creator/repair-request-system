using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RepairRequest.Domain.Security;

namespace RepairRequest.ApiTests.Http;

/// <summary>API host with a real allowed CORS origin configured, so the browser-enforced Expose-Headers behavior is exercised.</summary>
public sealed class CorsPolicyApiFactory : AuthApiFactory
{
    public const string AllowedOrigin = "http://localhost:4200";

    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_CorsPolicyApiTest;Trusted_Connection=True;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Program.cs reads Cors:AllowedOrigins synchronously from builder.Configuration before Build(), earlier than an
        // appended ConfigureAppConfiguration/AddInMemoryCollection provider (TestSettings) becomes visible there.
        // UseSetting applies before that read (the same technique DevelopmentMalwareScanningTests uses for its own
        // early-read flag).
        builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin);
    }
}

/// <summary>
/// CORS response headers as an actual browser sees them (`HttpClient`/curl and every other API test never enforce CORS,
/// so a missing `Access-Control-Expose-Headers` entry is otherwise invisible to the whole suite). Confirmed live in a
/// real browser during S2-002 verification: without exposing ETag, `response.headers.get('ETag')` in
/// `RepairRequestService.get()` always returned null, so Convert's `If-Match` guard silently no-opped on click.
/// </summary>
public sealed class CorsPolicyTests : IClassFixture<CorsPolicyApiFactory>
{
    private readonly CorsPolicyApiFactory _factory;

    public CorsPolicyTests(CorsPolicyApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CrossOriginGetResponses_ExposeETag_SoBrowserJavaScriptCanReadItForIfMatch()
    {
        var user = await _factory.CreateUserAsync(RoleCodes.Requester);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = AuthApiFactory.ValidPassword });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("accessToken").GetString()!;

        var created = new HttpRequestMessage(HttpMethod.Post, "/api/v1/repair-requests") { Content = JsonContent.Create(new { }) };
        created.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        created.Headers.Add("Origin", CorsPolicyApiFactory.AllowedOrigin);
        var createdResponse = await client.SendAsync(created);

        Assert.True(createdResponse.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposedHeaders));
        Assert.Contains(exposedHeaders!, value => value.Split(',').Select(v => v.Trim()).Contains("ETag"));
        Assert.NotNull(createdResponse.Headers.ETag);
    }
}
