using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Api.Contracts.WorkOrders;

/// <summary>CA-API-001 Submit Plan body (`docs/13` §4.21). Both fields are required.</summary>
public sealed record SubmitCorrectivePlanRequest(string? PlanText, Guid? PlanFileAssetId);

/// <summary>
/// Corrective Action response (`docs/13` §4.21/§4.22), returned by Submit Plan, Approve Plan and Schedule Rework.
/// <see cref="WorkOrderRowVersion"/> is Submit/Approve Plan's own next If-Match token — the Work Order's own
/// RowVersion, not a `corrective_action` token (see the store's own remarks) — the same cross-resource ETag shape
/// as <c>WorkSessionResponse.WorkOrderRowVersion</c>. Schedule Rework's own next If-Match token is instead carried
/// in the response's <c>ETag</c> header (the Corrective Action's own RowVersion — `docs/13` §4.22), not this body.
/// </summary>
public sealed record CorrectiveActionResponse(
    Guid CorrectiveActionId,
    Guid WorkOrderId,
    int CycleNo,
    string Status,
    Guid? OwnerTeamLeadId,
    string? PlanText,
    Guid? PlanFileAssetId,
    Guid? ApprovedBy,
    DateTime? ApprovedAt,
    string WorkOrderRowVersion,
    Guid? CorrectiveServiceVisitId);

public static class CorrectiveActionResponses
{
    public static CorrectiveActionResponse ToResponse(CorrectiveActionDto dto) =>
        new(
            dto.CorrectiveActionId,
            dto.WorkOrderId,
            dto.CycleNo,
            CorrectiveActionStatusCodes.ToCode(dto.Status),
            dto.OwnerTeamLeadId,
            dto.PlanText,
            dto.PlanFileAssetId,
            dto.ApprovedBy,
            dto.ApprovedAt,
            Convert.ToBase64String(dto.WorkOrderRowVersion),
            dto.CorrectiveServiceVisitId);
}
