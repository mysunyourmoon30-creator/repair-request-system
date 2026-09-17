using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace RepairRequest.ApiTests.Authorization;

/// <summary>
/// Authentication host plus test-only probe controllers that exercise the S1-003 authorization
/// infrastructure. The probes live in the test assembly, so no production endpoint is added.
/// </summary>
public sealed class AuthorizationApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_AuthorizationApiTest;Trusted_Connection=True;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ScopeProbeController).Assembly));
    }
}
