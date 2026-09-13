using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RepairRequest.ApiTests;

/// <summary>
/// Sprint 0 API-layer smoke test: the host boots and a controller responds.
/// Deliberately hits the DB-independent /api/health controller (not the
/// EF-backed /health check) so this test has no external dependency and can
/// run in any CI environment; see RepairRequest.IntegrationTests for the
/// DB-connected proof required by the Sprint 0 Definition of Done.
/// </summary>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetApiHealth_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetApiHealth_ReturnsCorrelationId()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("correlationId", body);
    }
}
