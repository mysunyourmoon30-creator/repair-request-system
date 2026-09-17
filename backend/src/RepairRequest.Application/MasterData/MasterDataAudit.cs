using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>
/// Audit records for master-data commands, written through the existing append-only audit_history table
/// in the same transaction as the change (RR-API-001 section 1; RR-ARCH-001 section 8). Old/new values
/// carry only the fields the command changed; no secrets or tokens are ever included.
/// </summary>
public static class MasterDataAudit
{
    public const string CustomerEntityType = "CUSTOMER";
    public const string SiteEntityType = "SITE";
    public const string EquipmentEntityType = "EQUIPMENT";

    public static string CreatedAction(string entityType) => $"{entityType}_CREATED";

    public static string UpdatedAction(string entityType) => $"{entityType}_UPDATED";

    public static string ActivatedAction(string entityType) => $"{entityType}_ACTIVATED";

    public static string DeactivatedAction(string entityType) => $"{entityType}_DEACTIVATED";

    public static string EntityTypeOf(MasterDataEntity entity) => entity switch
    {
        Customer => CustomerEntityType,
        Site => SiteEntityType,
        Equipment => EquipmentEntityType,
        _ => throw new ArgumentOutOfRangeException(nameof(entity))
    };

    public static AuditHistory Created(CommandContext context, MasterDataEntity entity, IReadOnlyDictionary<string, object?> values, DateTime occurredAt)
    {
        var entityType = EntityTypeOf(entity);
        return Create(context, entity, CreatedAction(entityType), null, MasterDataStatusCodes.ToCode(entity.Status), null, values, null, occurredAt);
    }

    public static AuditHistory Updated(CommandContext context, MasterDataEntity entity, string field, string oldValue, string newValue, DateTime occurredAt) =>
        Create(
            context,
            entity,
            UpdatedAction(EntityTypeOf(entity)),
            null,
            null,
            new Dictionary<string, object?> { [field] = oldValue },
            new Dictionary<string, object?> { [field] = newValue },
            null,
            occurredAt);

    public static AuditHistory Activated(CommandContext context, MasterDataEntity entity, string? previousReason, DateTime occurredAt) =>
        Create(
            context,
            entity,
            ActivatedAction(EntityTypeOf(entity)),
            MasterDataStatusCodes.Inactive,
            MasterDataStatusCodes.Active,
            new Dictionary<string, object?> { ["status"] = MasterDataStatusCodes.Inactive, ["deactivateReason"] = previousReason },
            new Dictionary<string, object?> { ["status"] = MasterDataStatusCodes.Active, ["deactivateReason"] = null },
            null,
            occurredAt);

    public static AuditHistory Deactivated(CommandContext context, MasterDataEntity entity, string reason, DateTime occurredAt) =>
        Create(
            context,
            entity,
            DeactivatedAction(EntityTypeOf(entity)),
            MasterDataStatusCodes.Active,
            MasterDataStatusCodes.Inactive,
            new Dictionary<string, object?> { ["status"] = MasterDataStatusCodes.Active, ["deactivateReason"] = null },
            new Dictionary<string, object?> { ["status"] = MasterDataStatusCodes.Inactive, ["deactivateReason"] = reason },
            reason,
            occurredAt);

    private static AuditHistory Create(
        CommandContext context,
        MasterDataEntity entity,
        string actionCode,
        string? fromState,
        string? toState,
        IReadOnlyDictionary<string, object?>? oldValues,
        IReadOnlyDictionary<string, object?>? newValues,
        string? reason,
        DateTime occurredAt) =>
        new(
            entity.TenantId,
            EntityTypeOf(entity),
            entity.Id,
            actionCode,
            fromState,
            toState,
            oldValues is null ? null : JsonSerializer.Serialize(oldValues),
            newValues is null ? null : JsonSerializer.Serialize(newValues),
            reason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);
}
