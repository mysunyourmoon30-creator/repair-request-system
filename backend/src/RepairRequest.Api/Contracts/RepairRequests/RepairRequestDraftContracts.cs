using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RepairRequest.Application.RepairRequests;

namespace RepairRequest.Api.Contracts.RepairRequests;

/// <summary>
/// Create and edit body of a Repair Request Draft (RR-API-001 section 4). The edit (PATCH) carries the complete editable
/// field set, as saved by the Draft form: an omitted field is saved as empty. Category and Priority are tenant lookup codes
/// and the request contact is a user id (DEC-PRE-S1-007-01/02/04). Tenant, requester, status, Request No and Location are
/// never accepted; unknown members are ignored. Timestamps must state an explicit UTC offset.
/// </summary>
public sealed record RepairRequestDraftRequest(
    Guid? SiteId,
    Guid? EquipmentId,
    string? RequestCategoryCode,
    string? PriorityCode,
    Guid? RequestContactId,
    string? Description,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? PreferredStartAt,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? PreferredEndAt)
{
    public RepairRequestDraftFields ToFields() =>
        new(SiteId, EquipmentId, RequestCategoryCode, PriorityCode, RequestContactId, Description, PreferredStartAt, PreferredEndAt);
}

/// <summary>RR-API-005 Submit body. The body itself may be omitted when no continuation reason is needed.</summary>
public sealed record SubmitRepairRequestRequest(string? DuplicateContinuationReason);

/// <summary>RR-API-007 Reject body. The reason is required (RR-DD-001 RR-016); a missing body or reason is a 422 on <c>reason</c>.</summary>
public sealed record RejectRepairRequestRequest(string? Reason);

public sealed record RepairRequestDraftResponse(
    Guid Id,
    string Status,
    string? RequestNo,
    Guid? SiteId,
    Guid? EquipmentId,
    string? RequestCategoryCode,
    string? PriorityCode,
    Guid? RequestContactId,
    string? Description,
    DateTime? PreferredStartAt,
    DateTime? PreferredEndAt,
    Guid CreatedBy,
    DateTime? SubmittedAt,
    string RowVersion);

/// <summary>Focused response projection; the EF entity is never serialized.</summary>
public static class RepairRequestResponses
{
    public static RepairRequestDraftResponse ToResponse(RepairRequestDraftDto dto) =>
        new(
            dto.Id,
            RepairRequestStatusCodes.ToCode(dto.Status),
            dto.RequestNo,
            dto.SiteId,
            dto.EquipmentId,
            dto.RequestCategoryCode,
            dto.PriorityCode,
            dto.RequestContactId,
            dto.Description,
            AsUtc(dto.PreferredStartAt),
            AsUtc(dto.PreferredEndAt),
            dto.CreatedBy,
            AsUtc(dto.SubmittedAt),
            Convert.ToBase64String(dto.RowVersion));

    private static DateTime? AsUtc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

/// <summary>
/// Reads an ISO-8601 timestamp only when it carries "Z" or an explicit offset (RR-API-001 section 1: ISO-8601 UTC), so a
/// value without an offset can never be silently interpreted in the server's local time zone. Invalid values produce
/// a 400 model-binding error.
/// </summary>
public sealed partial class ExplicitOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is null
            || !ExplicitOffset().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            throw new JsonException("Timestamps must be ISO-8601 with an explicit UTC offset, for example 2026-09-20T08:00:00Z.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ExplicitOffset();
}
