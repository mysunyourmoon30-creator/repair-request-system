using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.RepairRequests;

namespace RepairRequest.Application.DependencyInjection;

/// <summary>
/// Composition root entry point for the Application layer.
/// Business command/query services are registered here as they are approved and implemented.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // S1-004: Customer / Site / Equipment master data use cases.
        services.AddScoped<CustomerService>();
        services.AddScoped<SiteService>();
        services.AddScoped<EquipmentService>();

        // S1-005: Repair Request Draft create / edit / detail.
        services.AddScoped<RepairRequestDraftService>();

        // S1-006: attachment upload/download and malware scan result handling.
        services.AddScoped<RepairRequestAttachmentService>();
        services.AddScoped<FileScanService>();

        // S1-007: Repair Request Submit (ST-RR-002).
        services.AddScoped<RepairRequestSubmitService>();

        // S1-007R: approval route configuration and ST-RR-003 routing / approver assignment.
        services.AddScoped<ApprovalRouteService>();
        services.AddScoped<RepairRequestRoutingService>();
        services.AddScoped<ISubmittedRequestRouter>(provider => provider.GetRequiredService<RepairRequestRoutingService>());

        return services;
    }
}
