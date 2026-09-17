using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Authentication;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.ApiTests;

/// <summary>POST /api/v1/auth/login | refresh | revoke contract, cookie hardening and generic failures.</summary>
public class AuthEndpointsTests : IClassFixture<AuthApiFactory>
{
    private const string CookieName = "__Secure-rr-refresh-token";
    private const string WrongPassword = "Wrong-Password-99!";

    private readonly AuthApiFactory _factory;

    public AuthEndpointsTests(AuthApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    private static Task<HttpResponseMessage> PostWithCookieAsync(HttpClient client, string path, string? refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (refreshToken is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={refreshToken}");
        }

        return client.SendAsync(request);
    }

    private static string? RefreshSetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal))
            : null;

    private static string CookieValue(string setCookie) => setCookie[(CookieName.Length + 1)..setCookie.IndexOf(';')];

    private static async Task<string> NormalizedProblemAsync(HttpResponseMessage response)
    {
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        problem.Remove("correlationId");
        return problem.ToJsonString();
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsBearerTokenAndHardenedRefreshCookie()
    {
        var user = await _factory.CreateUserAsync(RoleCodes.Requester);
        var client = CreateClient();

        var response = await LoginAsync(client, user.Email!, AuthApiFactory.ValidPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Bearer", json.RootElement.GetProperty("tokenType").GetString());
        var accessToken = json.RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.True(json.RootElement.TryGetProperty("expiresAt", out _));
        Assert.False(json.RootElement.TryGetProperty("refreshToken", out _));
        Assert.DoesNotContain(AuthApiFactory.ValidPassword, body);

        var setCookie = RefreshSetCookie(response);
        Assert.NotNull(setCookie);
        var attributes = setCookie.ToLowerInvariant();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/api/v1/auth", attributes);
        Assert.Contains("expires=", attributes);
        Assert.False(string.IsNullOrEmpty(CookieValue(setCookie)));
        Assert.DoesNotContain(CookieValue(setCookie), body);

        var authenticated = await BearerAuthentication.AuthenticateAsync(_factory.Services, accessToken);
        Assert.True(authenticated.Succeeded);
        Assert.Equal(user.Id.ToString(), authenticated.Principal!.Identity!.Name);
        Assert.True(authenticated.Principal.IsInRole(RoleCodes.Requester));
    }

    [Fact]
    public async Task Login_WrongPasswordUnknownEmailAndLockedAccount_ReturnIdenticalGenericFailure()
    {
        var user = await _factory.CreateUserAsync();
        var client = CreateClient();

        var wrongPassword = await LoginAsync(client, user.Email!, WrongPassword);
        var unknownEmail = await LoginAsync(client, $"missing-{Guid.NewGuid():N}@example.test", AuthApiFactory.ValidPassword);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await LoginAsync(client, user.Email!, WrongPassword);
        }

        var lockedOut = await LoginAsync(client, user.Email!, AuthApiFactory.ValidPassword);

        HttpResponseMessage[] failures = [wrongPassword, unknownEmail, lockedOut];
        var expected = await NormalizedProblemAsync(wrongPassword);

        foreach (var failure in failures)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
            Assert.Equal("application/problem+json", failure.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expected, await NormalizedProblemAsync(failure));
            Assert.Equal(string.Empty, CookieValue(RefreshSetCookie(failure)!));
        }

        Assert.Contains("\"code\":\"UNAUTHENTICATED\"", expected);
    }

    [Theory]
    [InlineData("{\"email\":\"not-an-email\",\"password\":\"Correct-Horse-42!\"}")]
    [InlineData("{\"email\":\"someone@example.test\"}")]
    [InlineData("{}")]
    public async Task Login_WithMalformedRequest_Returns400(string payload)
    {
        var client = CreateClient();

        var response = await client.PostAsync("/api/v1/auth/login", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Correct-Horse-42!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refresh_WithValidCookie_RotatesCookieAndIssuesNewAccessToken()
    {
        var user = await _factory.CreateUserAsync(RoleCodes.Coordinator);
        var client = CreateClient();
        var login = await LoginAsync(client, user.Email!, AuthApiFactory.ValidPassword);
        var originalCookie = CookieValue(RefreshSetCookie(login)!);

        var refresh = await PostWithCookieAsync(client, "/api/v1/auth/refresh", originalCookie);

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var rotatedCookie = CookieValue(RefreshSetCookie(refresh)!);
        Assert.False(string.IsNullOrEmpty(rotatedCookie));
        Assert.NotEqual(originalCookie, rotatedCookie);

        var accessToken = (await refresh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        Assert.True((await BearerAuthentication.AuthenticateAsync(_factory.Services, accessToken)).Succeeded);
    }

    [Fact]
    public async Task Refresh_ReusingRotatedCookie_IsRejected_AndEndsTheSession()
    {
        var user = await _factory.CreateUserAsync();
        var client = CreateClient();
        var originalCookie = CookieValue(RefreshSetCookie(await LoginAsync(client, user.Email!, AuthApiFactory.ValidPassword))!);
        var rotatedCookie = CookieValue(RefreshSetCookie(await PostWithCookieAsync(client, "/api/v1/auth/refresh", originalCookie))!);

        var replay = await PostWithCookieAsync(client, "/api/v1/auth/refresh", originalCookie);
        var afterReplay = await PostWithCookieAsync(client, "/api/v1/auth/refresh", rotatedCookie);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
        Assert.Equal(await NormalizedProblemAsync(replay), await NormalizedProblemAsync(afterReplay));
    }

    [Fact]
    public async Task Refresh_WithoutCookie_Returns401()
    {
        var response = await PostWithCookieAsync(CreateClient(), "/api/v1/auth/refresh", refreshToken: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_EndsSession_AndClearsCookie()
    {
        var user = await _factory.CreateUserAsync();
        var client = CreateClient();
        var cookie = CookieValue(RefreshSetCookie(await LoginAsync(client, user.Email!, AuthApiFactory.ValidPassword))!);

        var revoke = await PostWithCookieAsync(client, "/api/v1/auth/revoke", cookie);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var cleared = RefreshSetCookie(revoke);
        Assert.NotNull(cleared);
        Assert.Equal(string.Empty, CookieValue(cleared));
        Assert.Contains("expires=thu, 01 jan 1970", cleared.ToLowerInvariant());

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWithCookieAsync(client, "/api/v1/auth/refresh", cookie)).StatusCode);
    }

    [Fact]
    public async Task Revoke_WithoutCookie_Returns204()
    {
        var response = await PostWithCookieAsync(CreateClient(), "/api/v1/auth/revoke", refreshToken: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task FailedLogin_EchoesCallerCorrelationId_AndLinksAuditEvent()
    {
        var user = await _factory.CreateUserAsync();
        var client = CreateClient();
        var correlationId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = user.Email, password = WrongPassword })
        };
        request.Headers.Add("X-Correlation-ID", correlationId.ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(correlationId.ToString(), response.Headers.GetValues("X-Correlation-ID").Single());
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(correlationId, problem.GetProperty("correlationId").GetGuid());

        await using var scope = _factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>().AuditHistory
            .AsNoTracking()
            .SingleAsync(row => row.CorrelationId == correlationId);
        Assert.Equal(AuthenticationEventCodes.LoginFailed, audit.ActionCode);
        Assert.Equal(user.Id, audit.EntityId);
        Assert.Equal(user.Id, audit.ActorId);
    }
}
