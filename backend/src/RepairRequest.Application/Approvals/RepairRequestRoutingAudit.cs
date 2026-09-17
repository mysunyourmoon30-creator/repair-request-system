using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Routing audit records on the Repair Request timeline (ST-RR-003 "Routing audit"; DEC-PRE-S1-007R-01/07/09). The actor
/// is always System (<see cref="SystemActors.ApprovalRouting"/>): routing decides and assigns, never the user. The user
/// whose command initiated routing (the submitting Requester or the retrying Administrator) is kept only as context in
/// <see cref="InitiatedByField"/>, next to the trigger and the correlation id. Values carry identifiers and codes only,
/// never descriptions, contacts or tokens.
/// </summary>
public static class RepairRequestRoutingAudit
{
    public const string RoutedAction = "REPAIR_REQUEST_ROUTED";
    public const string RoutingFailedAction = "REPAIR_REQUEST_ROUTING_FAILED";

    public const string RoutingFailureCodeField = "routingFailureCode";
    public const string ApprovalRouteIdField = "approvalRouteId";
    public const string ApprovalStepNoField = "approvalStepNo";
    public const string ApprovalCycleNoField = "approvalCycleNo";
    public const string AssignedApproverIdField = "assignedApproverId";
    public const string TriggerField = "trigger";
    public const string InitiatedByField = "initiatedBy";

    public static AuditHistory Routed(
        CommandContext context,
        RepairRequestAggregate request,
        RoutingDecision decision,
        short approvalCycleNo,
        RoutingTrigger trigger,
        DateTime occurredAt) =>
        new(
            request.TenantId,
            RepairRequestAudit.EntityType,
            request.Id,
            RoutedAction,
            RepairRequestStatusCodes.ToCode(RepairRequestStatus.Submitted),
            RepairRequestStatusCodes.ToCode(RepairRequestStatus.UnderReview),
            null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [ApprovalRouteIdField] = decision.RouteId,
                [ApprovalStepNoField] = decision.StepNo,
                [ApprovalCycleNoField] = approvalCycleNo,
                [AssignedApproverIdField] = decision.AssignedApproverId,
                [TriggerField] = TriggerCode(trigger),
                [InitiatedByField] = context.User.UserId
            }),
            null,
            SystemActors.ApprovalRouting,
            occurredAt,
            context.CorrelationId);

    public static AuditHistory RoutingFailed(
        CommandContext context,
        RepairRequestAggregate request,
        RoutingDecision decision,
        short approvalCycleNo,
        RoutingTrigger trigger,
        DateTime occurredAt) =>
        new(
            request.TenantId,
            RepairRequestAudit.EntityType,
            request.Id,
            RoutingFailedAction,
            null,
            null,
            null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [RoutingFailureCodeField] = decision.FailureCode,
                [ApprovalRouteIdField] = decision.RouteId,
                [ApprovalStepNoField] = decision.StepNo,
                [ApprovalCycleNoField] = approvalCycleNo,
                [TriggerField] = TriggerCode(trigger),
                [InitiatedByField] = context.User.UserId
            }),
            null,
            SystemActors.ApprovalRouting,
            occurredAt,
            context.CorrelationId);

    public static string TriggerCode(RoutingTrigger trigger) => trigger switch
    {
        RoutingTrigger.Submit => "SUBMIT",
        RoutingTrigger.AdminRetry => "ADMIN_RETRY",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };

    /// <summary>The routing failure code stored in a ROUTING_FAILED audit value document, or null.</summary>
    public static string? ReadFailureCode(string? newValueJson)
    {
        if (string.IsNullOrEmpty(newValueJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(newValueJson);
        return document.RootElement.TryGetProperty(RoutingFailureCodeField, out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
    }
}
