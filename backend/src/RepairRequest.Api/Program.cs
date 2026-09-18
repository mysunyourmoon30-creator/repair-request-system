using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Authorization;
using RepairRequest.Api.DevelopmentScanning;
using RepairRequest.Api.Hosting;
using RepairRequest.Api.Http;
using RepairRequest.Api.Middleware;
using RepairRequest.Application.DependencyInjection;
using RepairRequest.Infrastructure.Authentication;
using RepairRequest.Infrastructure.DependencyInjection;
using RepairRequest.Infrastructure.Files;
using RepairRequest.Infrastructure.Persistence;
using Serilog;

const string CorsPolicyName = "RepairRequestCorsPolicy";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .Enrich.FromLogContext()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // preserveStaticLogger: the host gets its own configured logger instead of freezing the static
    // bootstrap logger, so more than one host can be built in a process (API integration tests).
    // The bootstrap logger stays available for the startup-failure log below.
    builder.Host.UseSerilog(
        (context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(),
        preserveStaticLogger: true);

    // Add services to the container.
    builder.Services.AddControllers();

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // DEC-PS1-004: JWT bearer validation shares its rules with token issuance. JWT settings
    // are validated on startup (JwtOptionsValidator), so a missing or weak signing key stops the host.
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<JwtOptions>>((bearerOptions, jwtOptions) =>
        {
            bearerOptions.MapInboundClaims = false;
            bearerOptions.TokenValidationParameters = JwtTokenValidation.CreateParameters(jwtOptions.Value);
        });
    // S1-003: deny-by-default fallback policy, role-capability policies and tenant/site data scope.
    builder.Services.AddRepairRequestAuthorization();
    // S1-004: bounded list paging (RR-API-001 sections 7/9), validated on start.
    builder.Services.AddApiPaging(builder.Configuration);
    // S1-007: idempotent, non-fatal Category/Priority lookup seeding for existing tenants (DEC-PRE-S1-007-03).
    builder.Services.AddHostedService<RequestLookupSeedingHostedService>();
    // S1-007: opt-in Development/Testing-only FAKE malware scanning (DEC-PRE-S1-007-08). Registered after the
    // Infrastructure default; refuses every other environment. Production keeps NotConfiguredMalwareScanner.
    builder.AddDevelopmentOnlyMalwareScanning();
    // S1-006: private attachment storage root; a relative configured path is resolved against the content root.
    builder.Services.PostConfigure<FileStorageOptions>(options =>
    {
        if (!string.IsNullOrWhiteSpace(options.RootPath) && !Path.IsPathRooted(options.RootPath))
        {
            options.RootPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, options.RootPath));
        }
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "Repair Request API",
            Version = "v1"
        });
    });

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<RepairRequestDbContext>("database");

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(CorsPolicyName, policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    // ETag is not in the CORS-safelisted response headers, so without this the browser's
                    // JS can never read it (confirmed live: RepairRequestService.get() always saw null,
                    // silently blocking Convert's If-Match guard) even though curl/HttpClient/tests never
                    // notice, since CORS exposure is enforced only by real browsers.
                    .WithExposedHeaders("ETag");
            }
        });
    });

    var app = builder.Build();

    app.UseExceptionHandler();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    app.UseCors(CorsPolicyName);

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health").AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "RepairRequest.Api terminated unexpectedly during startup.");

    // Fail fast: never swallow startup failures such as invalid JWT configuration (decision M6).
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so RepairRequest.ApiTests can bootstrap WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program { }
