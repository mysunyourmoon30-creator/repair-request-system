using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Middleware;
using RepairRequest.Application.DependencyInjection;
using RepairRequest.Infrastructure.Authentication;
using RepairRequest.Infrastructure.DependencyInjection;
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
    builder.Services.AddAuthorization();

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
                    .AllowCredentials();
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
    app.MapHealthChecks("/health");

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
