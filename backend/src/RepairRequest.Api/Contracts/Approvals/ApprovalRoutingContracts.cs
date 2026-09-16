using RepairRequest.Application.Approvals;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.RepairRequests;

namespace RepairRequest.Api.Contracts.Approvals;

/// <summary>
/// Create body of an approval route (DEC-PRE-S1-007R-04). Tenant, status, route identity and step number are never
/// accepted; unknown members are ignored. Values are nullable so a missing value is a 422 from the Application layer.
/// </summary>
public sealed record ApprovalRouteRequest(string? RequestCategoryCode, Guid? SiteId, string? ApproverRoleCode, Guid? ApproverUserId)
{
    public ApprovalRouteFields ToFields() => new(RequestCategoryCode, SiteId, ApproverRoleCode, ApproverUserId);
}

public sealed record ApprovalRouteResponse(
    Guid Id,
    string RequestCategoryCode,
    Guid? SiteId,
    string Status,
    short StepNo,
    string ApproverRoleCode,
    Guid? ApproverUserId,
    string RowVersion);

/// <summary>Query string of the routing-issue list: page (1-based) and pageSize (clamped to the configured maximum).</summary>
public sealed class RoutingIssueListRequest
{
    public int? Page { get; init; }

    public int? PageSize { get; init; }
}

/// <summary>Routing metadata only (DEC-PRE-S1-007R-10): no description, requester/contact, attachments or audit history.</summary>
public sealed record RoutingIssueResponse(
    Guid RepairRequestId,
    string? RequestNo,
    Guid? SiteId,
    string? RequestCategoryCode,
    DateTime? SubmittedAt,
    string? LastRoutingFailureCode,
    string RowVersion);

public sealed record RoutingResultResponse(Guid RepairRequestId, string Status, string? RoutingFailureCode, string RowVersion);

/// <summary>Focused response projections; EF entities are never serialized.</summary>
public static class ApprovalRoutingResponses
{
    public static ApprovalRouteResponse ToResponse(ApprovalRouteDto dto) =>
        new(
            dto.Id,
            dto.RequestCategoryCode,
            dto.SiteId,
            MasterDataStatusCodes.ToCode(dto.Status),
            dto.StepNo,
            dto.ApproverRoleCode,
            dto.ApproverUserId,
            Convert.ToBase64String(dto.RowVersion));

    public static RoutingIssueResponse ToResponse(RoutingIssueDto dto) =>
        new(
            dto.RepairRequestId,
            dto.RequestNo,
            dto.SiteId,
            dto.RequestCategoryCode,
            dto.SubmittedAt is null ? null : DateTime.SpecifyKind(dto.SubmittedAt.Value, DateTimeKind.Utc),
            dto.LastRoutingFailureCode,
            Convert.ToBase64String(dto.RowVersion));

    public static RoutingResultResponse ToResponse(RoutingResultDto dto) =>
        new(dto.RepairRequestId, RepairRequestStatusCodes.ToCode(dto.Status), dto.RoutingFailureCode, Convert.ToBase64String(dto.RowVersion));
}
