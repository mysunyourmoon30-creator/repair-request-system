using System.Text.Json;

namespace RepairRequest.ApiTests.Audit;

/// <summary>
/// The redaction rules of a Work Order timeline response (docs/15 §4.1, §6; docs/17 P11), shared by every API test that reads a
/// timeline. The rules are context-aware: <c>oldValue</c> / <c>newValue</c> remain forbidden everywhere, EXCEPT inside <c>details</c> of an
/// item whose <c>entityType</c> is <c>TIME_CORRECTION</c> and whose <c>actionCode</c> is one of the three approved Time Correction actions.
/// There they are part of the approved allowlist (together with the session/pause ids and the timestamp type); the reason, the raw JSON and
/// every other key stay excluded for every event type.
/// </summary>
internal static class TimelineRedactionAssertions
{
    public const string TimeCorrectionEntityType = "TIME_CORRECTION";

    public static readonly string[] TimeCorrectionActions =
        ["TIME_CORRECTION_REQUESTED", "TIME_CORRECTION_APPROVED", "TIME_CORRECTION_REJECTED"];

    /// <summary>`details` keys of every event type other than Time Correction (docs/15 §4.1).</summary>
    public static readonly HashSet<string> LegacyDetailKeys =
    [
        "workOrderId", "workOrderNo", "serviceVisitId", "ownerTeamId", "assignedTechnicianId", "scheduledStartAt", "scheduledEndAt",
        "workSessionId", "workSessionPauseId", "pausedAt", "resumedAt", "workSummaryId", "repairOutcomeCode", "customerAcceptanceId",
        "correctiveActionId", "costSummaryId", "assignedTeamId", "decision", "newServiceVisitId",
    ];

    /// <summary>`details` keys of the three Time Correction actions on a TIME_CORRECTION item (docs/17 P11).</summary>
    public static readonly HashSet<string> TimeCorrectionDetailKeys =
        ["workSessionId", "workSessionPauseId", "timestampType", "oldValue", "newValue"];

    /// <summary>Names that may never appear as a property anywhere in a timeline response, except the before/after pair in a Time Correction item's details.</summary>
    public static readonly string[] ForbiddenPropertyNames =
        ["reason", "correlationId", "oldValue", "oldValueJson", "newValue", "newValueJson", "totalAmount", "currencyCode", "note", "email",
         "displayName", "userName", "phone", "acceptanceContactSnapshot", "summaryText", "planText", "decisionReason", "cancelReason",
         "visitCompletedAtSynchronized"];

    private static readonly HashSet<string> ResponseProperties = ["items", "nextCursor", "hasMore"];

    private static readonly HashSet<string> ItemProperties =
        ["auditId", "occurredAt", "actionCode", "entityType", "entityId", "fromState", "toState", "actor", "details"];

    public static bool IsTimeCorrectionItem(JsonElement item) =>
        item.GetProperty("entityType").GetString() == TimeCorrectionEntityType
        && Array.IndexOf(TimeCorrectionActions, item.GetProperty("actionCode").GetString()) >= 0;

    /// <summary>Asserts every rule on one serialized page.</summary>
    public static void AssertPage(JsonElement page)
    {
        Assert.All(page.EnumerateObject().Select(property => property.Name), name => Assert.Contains(name, ResponseProperties));

        foreach (var item in page.GetProperty("items").EnumerateArray())
        {
            AssertItem(item);
        }
    }

    public static void AssertItem(JsonElement item)
    {
        foreach (var property in item.EnumerateObject())
        {
            Assert.Contains(property.Name, ItemProperties);
            Assert.DoesNotContain(property.Name, ForbiddenPropertyNames);
            if (property.Name != "details")
            {
                // The rest of the item (actor, ids, states) never contains a forbidden name at any depth.
                Assert.All(PropertyNames(property.Value), name => Assert.DoesNotContain(name, ForbiddenPropertyNames));
            }
        }

        var allowedKeys = IsTimeCorrectionItem(item) ? TimeCorrectionDetailKeys : LegacyDetailKeys;
        foreach (var detail in item.GetProperty("details").EnumerateObject())
        {
            Assert.Contains(detail.Name, allowedKeys);
            Assert.True(detail.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null);
            if (!IsTimeCorrectionItem(item))
            {
                Assert.DoesNotContain(detail.Name, ForbiddenPropertyNames);
            }
            else
            {
                Assert.DoesNotContain(
                    detail.Name,
                    ForbiddenPropertyNames.Where(name => name is not ("oldValue" or "newValue")));
            }
        }
    }

    public static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var child in PropertyNames(property.Value))
                    {
                        yield return child;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in PropertyNames(item))
                    {
                        yield return child;
                    }
                }

                break;
        }
    }
}
