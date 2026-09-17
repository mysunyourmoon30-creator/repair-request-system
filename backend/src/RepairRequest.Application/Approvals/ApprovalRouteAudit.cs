using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;

namespace RepairRequest.Application.Approvals;

/// <summary>Audit records for approval route configuration, written in the same transaction as the change.</summary>
public static class ApprovalRouteAudit
{
    public const string EntityType = "APPROVAL_ROUTE";
    public const string CreatedAction = "APPROVAL_ROUTE_CREATED";
    public const string ActivatedAction = "APPROVAL_ROUTE_ACTIVATED";
    public const string DeactivatedAction = "APPROVAL_ROUTE_DEACTIVATED";

    public static AuditHistory Created(CommandContext context, ApprovalRoute route, ApprovalRouteStep step, DateTime occurredAt) =>
        Create(
            context,
            route,
            CreatedAction,
            null,
            MasterDataStatusCodes.Active,
            new Dictionary<string, object?>
            {
                [ApprovalRouteFieldNames.RequestCategoryCode] = route.RequestCategoryCode,
                [ApprovalRouteFieldNames.SiteId] = route.SiteId,
                ["stepNo"] = step.StepNo,
                [ApprovalRouteFieldNames.ApproverRoleCode] = step.ApproverRoleCode,
                [ApprovalRouteFieldNames.ApproverUserId] = step.ApproverUserId
            },
            occurredAt);

    public static AuditHistory Activated(CommandContext context, ApprovalRoute route, DateTime occurredAt) =>
        Create(context, route, ActivatedAction, MasterDataStatusCodes.Inactive, MasterDataStatusCodes.Active, null, occurredAt);

    public static AuditHistory Deactivated(CommandContext context, ApprovalRoute route, DateTime occurredAt) =>
        Create(context, route, DeactivatedAction, MasterDataStatusCodes.Active, MasterDataStatusCodes.Inactive, null, occurredAt);

    private static AuditHistory Create(
        CommandContext context,
        ApprovalRoute route,
        string actionCode,
        string? fromState,
        string toState,
        IReadOnlyDictionary<string, object?>? newValues,
        DateTime occurredAt) =>
        new(
            route.TenantId,
            EntityType,
            route.Id,
            actionCode,
            fromState,
            toState,
            null,
            newValues is null ? null : JsonSerializer.Serialize(newValues),
            null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);
}
