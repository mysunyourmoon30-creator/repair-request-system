using System.Text.Json;

namespace RepairRequest.Application.Audit;

/// <summary>
/// The action-specific `details` allowlist (docs/15 §4.1, D4). It is the only way audit payload data leaves the
/// server: only the listed keys, only scalar values (string of at most <see cref="MaxStringLength"/> characters,
/// number, boolean, null). Missing, unparseable or non-object JSON, unknown keys and unknown action codes all yield
/// empty details — never an error and never the raw text. Adding a key or a code is a reviewed code change with a test.
/// </summary>
public static class AuditTimelineDetails
{
    public const int MaxStringLength = 64;

    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["REPAIR_REQUEST_CONVERTED"] = ["workOrderId", "workOrderNo"],
        ["WORK_ORDER_SCHEDULED"] = ["serviceVisitId", "ownerTeamId", "assignedTechnicianId", "scheduledStartAt", "scheduledEndAt"],
        ["SERVICE_VISIT_CHECKED_IN"] = ["workSessionId"],
        ["SERVICE_VISIT_COMPLETED"] = ["workSessionId"],
        ["WORK_SESSION_PAUSED"] = ["workSessionPauseId", "pausedAt"],
        ["WORK_SESSION_RESUMED"] = ["workSessionPauseId", "resumedAt"],
        ["WORK_SUMMARY_SUBMITTED"] = ["workSummaryId", "repairOutcomeCode"],
        ["WORK_ORDER_REJECTED"] = ["customerAcceptanceId", "correctiveActionId"],
        // Cost events expose only the Cost Summary id — never totalAmount, currencyCode or the note.
        ["COST_SUMMARY_PREPARED"] = ["costSummaryId"],
        ["COST_SUMMARY_UPDATED"] = ["costSummaryId"],
        ["COST_SUMMARY_REVIEWED"] = ["costSummaryId"],
        ["WORK_ORDER_CLOSED"] = ["costSummaryId"],
        ["CORRECTIVE_ACTION_REWORK_SCHEDULED"] = ["serviceVisitId", "assignedTeamId", "assignedTechnicianId", "scheduledStartAt", "scheduledEndAt"],
        ["SERVICE_VISIT_RESCHEDULED"] = ["scheduledStartAt", "scheduledEndAt"],
        ["SERVICE_VISIT_REASSIGNED"] = ["assignedTeamId", "assignedTechnicianId"],
        ["SERVICE_VISIT_MISSED_DECIDED"] = ["decision", "newServiceVisitId"],
        // WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE deliberately has no entry: acceptanceContactId and the snapshot are not exposed.
    };

    /// <summary>The action codes that have an allowlist entry; the store reads <c>new_value_json</c> only for these.</summary>
    public static readonly string[] CodesWithDetails = [.. Allowed.Keys];

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    public static IReadOnlyDictionary<string, object?> Project(string actionCode, string? newValueJson)
    {
        if (string.IsNullOrWhiteSpace(newValueJson) || !Allowed.TryGetValue(actionCode, out var keys))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(newValueJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            var details = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (!document.RootElement.TryGetProperty(key, out var value))
                {
                    continue;
                }

                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        var text = value.GetString()!;
                        if (text.Length <= MaxStringLength)
                        {
                            details[key] = text;
                        }

                        break;
                    case JsonValueKind.Number:
                        // Box each branch separately: a `cond ? long : double` expression would promote integers to double.
                        details[key] = value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble();
                        break;
                    case JsonValueKind.True:
                        details[key] = true;
                        break;
                    case JsonValueKind.False:
                        details[key] = false;
                        break;
                    case JsonValueKind.Null:
                        details[key] = null;
                        break;
                    // Objects and arrays are never copied.
                }
            }

            return details.Count == 0 ? Empty : details;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}
