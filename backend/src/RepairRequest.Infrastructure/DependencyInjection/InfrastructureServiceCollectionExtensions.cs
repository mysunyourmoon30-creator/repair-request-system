using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Authentication;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Infrastructure.Approvals;
using RepairRequest.Infrastructure.Attachments;
using RepairRequest.Infrastructure.Authentication;
using RepairRequest.Infrastructure.Authorization;
using RepairRequest.Infrastructure.Files;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.MasterData;
using RepairRequest.Infrastructure.RepairRequests;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.WorkOrders;

namespace RepairRequest.Infrastructure.DependencyInjection;

/// <summary>
/// Composition root entry point for the Infrastructure layer (RR-ARCH-001 section 5).
/// Wires EF Core / SQL Server and the ASP.NET Core Identity + token adapters (DEC-PS1-004).
/// Storage and notification-outbox adapters are added in later sprints.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<RepairRequestDbContext>(options =>
        {
            // Read when the context is first created so host-level overrides (environment
            // variables, user-secrets, test hosts) are always honoured.
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is not configured. " +
                    "Set it via appsettings.{Environment}.json (gitignored), user-secrets, or an environment variable.");
            }

            options.UseSqlServer(connectionString);
        });

        AddAuthentication(services, configuration);

        // S1-003: caller resolution and tenant/site data scope.
        services.AddScoped<ICurrentUserStore, CurrentUserStore>();
        services.AddScoped<IDataScope, DataScope>();

        // S1-004: master data persistence port.
        services.AddScoped<IMasterDataStore, MasterDataStore>();

        // S1-005: Repair Request Draft persistence port.
        services.AddScoped<IRepairRequestDraftStore, RepairRequestDraftStore>();

        // S1-007: Submit persistence port and idempotent Category/Priority lookup seeding.
        services.AddSingleton(new RepairRequestDuplicateLockOptions());
        services.AddScoped<IRepairRequestSubmitStore, RepairRequestSubmitStore>();
        services.AddScoped<RequestLookupSeeder>();

        // S1-007R: approval route configuration and routing persistence ports.
        services.AddSingleton(new RepairRequestRoutingLockOptions());
        services.AddScoped<IApprovalRouteStore, ApprovalRouteStore>();
        services.AddScoped<IApprovalRoutingStore, ApprovalRoutingStore>();

        // S1-008: Approve / Reject decision persistence (shares the review-workflow lock with routing).
        services.AddScoped<IRepairRequestDecisionStore, RepairRequestDecisionStore>();

        // S1-009: Cancel persistence (shares the review-workflow lock with routing and decisions).
        services.AddScoped<IRepairRequestCancelStore, RepairRequestCancelStore>();

        // S2-001: Work Order List/Detail read persistence port.
        services.AddScoped<IWorkOrderStore, WorkOrderStore>();

        // S2-002: Convert persistence port (ST-RR-008).
        services.AddScoped<IRepairRequestConvertStore, RepairRequestConvertStore>();

        // S1-006: attachment metadata, private file storage and the malware scanning provider (DEC-PS1-005).
        services.AddScoped<IAttachmentStore, AttachmentStore>();
        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FileStorageOptions>, FileStorageOptionsValidator>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.TryAddSingleton<IFileMalwareScanner, NotConfiguredMalwareScanner>();

        return services;
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddIdentityCore<ApplicationUser>(IdentityPolicy.Configure)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<RepairRequestDbContext>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services.AddOptions<RefreshTokenOptions>()
            .Bind(configuration.GetSection(RefreshTokenOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RefreshTokenOptions>, RefreshTokenOptionsValidator>();

        services.AddSingleton<JwtAccessTokenIssuer>();
        services.AddSingleton<DummyPasswordHash>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
    }
}
