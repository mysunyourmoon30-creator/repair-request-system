using RepairRequest.Application.Audit;

namespace RepairRequest.Application.Tests.Audit;

/// <summary>
/// The action-specific `details` allowlist (docs/15 §4.1, D4): the only way audit payload data leaves the server.
/// </summary>
public class AuditTimelineDetailsTests
{
    private const string Sentinel = "SENTINEL-SECRET-VALUE";

    /// <summary>Every code of the §4.1 table with the keys it may expose.</summary>
    public static IEnumerable<object[]> AllowedKeysByCode() =>
    [
        ["REPAIR_REQUEST_CONVERTED", new[] { "workOrderId", "workOrderNo" }],
        ["WORK_ORDER_SCHEDULED", new[] { "serviceVisitId", "ownerTeamId", "assignedTechnicianId", "scheduledStartAt", "scheduledEndAt" }],
        ["SERVICE_VISIT_CHECKED_IN", new[] { "workSessionId" }],
        ["SERVICE_VISIT_COMPLETED", new[] { "workSessionId" }],
        ["WORK_SESSION_PAUSED", new[] { "workSessionPauseId", "pausedAt" }],
        ["WORK_SESSION_RESUMED", new[] { "workSessionPauseId", "resumedAt" }],
        ["WORK_SUMMARY_SUBMITTED", new[] { "workSummaryId", "repairOutcomeCode" }],
        ["WORK_ORDER_REJECTED", new[] { "customerAcceptanceId", "correctiveActionId" }],
        ["COST_SUMMARY_PREPARED", new[] { "costSummaryId" }],
        ["COST_SUMMARY_UPDATED", new[] { "costSummaryId" }],
        ["COST_SUMMARY_REVIEWED", new[] { "costSummaryId" }],
        ["WORK_ORDER_CLOSED", new[] { "costSummaryId" }],
        ["CORRECTIVE_ACTION_REWORK_SCHEDULED", new[] { "serviceVisitId", "assignedTeamId", "assignedTechnicianId", "scheduledStartAt", "scheduledEndAt" }],
        ["SERVICE_VISIT_RESCHEDULED", new[] { "scheduledStartAt", "scheduledEndAt" }],
        ["SERVICE_VISIT_REASSIGNED", new[] { "assignedTeamId", "assignedTechnicianId" }],
        ["SERVICE_VISIT_MISSED_DECIDED", new[] { "decision", "newServiceVisitId" }],
        ["TIME_CORRECTION_REQUESTED", new[] { "workSessionId", "workSessionPauseId", "timestampType", "oldValue", "newValue" }],
        ["TIME_CORRECTION_APPROVED", new[] { "workSessionId", "workSessionPauseId", "timestampType", "oldValue", "newValue" }],
        ["TIME_CORRECTION_REJECTED", new[] { "workSessionId", "workSessionPauseId", "timestampType", "oldValue", "newValue" }],
    ];

    private static readonly string[] ForbiddenKeys =
    [
        "totalAmount", "currencyCode", "note", "acceptanceContactSnapshot", "acceptanceContactId", "email", "phone", "summaryText",
        "planText", "reason", "decisionReason", "correlationId", "oldValue", "newValue", "secret",
    ];

    private static string JsonWith(IEnumerable<string> allowed)
    {
        var members = allowed.Select(key => $"\"{key}\":\"value-of-{key}\"")
            .Concat(ForbiddenKeys.Where(key => !allowed.Contains(key)).Select(key => $"\"{key}\":\"{Sentinel}\""));
        return "{" + string.Join(",", members) + "}";
    }

    [Theory]
    [MemberData(nameof(AllowedKeysByCode))]
    public void EveryCode_ExposesExactlyItsAllowlistedKeys_AndNothingElse(string code, string[] allowed)
    {
        var details = AuditTimelineDetails.Project(code, JsonWith(allowed));

        Assert.Equal(allowed.OrderBy(k => k), details.Keys.OrderBy(k => k));
        Assert.All(allowed, key => Assert.Equal($"value-of-{key}", details[key]));
        Assert.DoesNotContain(details.Values, v => v is string s && s.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public void CodesWithDetails_AreExactlyTheNineteenAllowlistedCodes()
    {
        var expected = AllowedKeysByCode().Select(row => (string)row[0]).OrderBy(c => c);

        Assert.Equal(expected, AuditTimelineDetails.CodesWithDetails.OrderBy(c => c));
        Assert.Equal(19, AuditTimelineDetails.CodesWithDetails.Length);
    }

    [Theory]
    [InlineData("WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE")]
    [InlineData("WORK_ORDER_STARTED")]
    [InlineData("WORK_ORDER_REWORK_STARTED")]
    [InlineData("WORK_SESSION_CHECKED_OUT")]
    [InlineData("WORK_ORDER_ACCEPTED")]
    [InlineData("WORK_ORDER_CANCELLED")]
    [InlineData("SERVICE_VISIT_CANCELLED")]
    [InlineData("SERVICE_VISIT_MARKED_MISSED")]
    [InlineData("CORRECTIVE_ACTION_PLAN_SUBMITTED")]
    [InlineData("CORRECTIVE_ACTION_PLAN_APPROVED")]
    [InlineData("SOME_FUTURE_UNKNOWN_CODE")]
    public void CodesWithoutAnAllowlistEntry_AndUnknownCodes_YieldEmptyDetails_EvenWithAPayload(string code)
    {
        Assert.Empty(AuditTimelineDetails.Project(code, JsonWith(["workSessionId", "serviceVisitId", "costSummaryId", "decision"])));
        Assert.DoesNotContain(code, AuditTimelineDetails.CodesWithDetails);
    }

    [Theory]
    [InlineData("TIME_CORRECTION_REQUESTED")]
    [InlineData("TIME_CORRECTION_APPROVED")]
    [InlineData("TIME_CORRECTION_REJECTED")]
    public void TimeCorrectionEvents_ExposeTheHistoricalBeforeAfterFromTheEvent_AndNothingElseOfThePayload(string code)
    {
        const string json =
            "{\"workSessionId\":\"11111111-1111-1111-1111-111111111111\",\"workSessionPauseId\":\"22222222-2222-2222-2222-222222222222\","
            + "\"timestampType\":\"RESUME\",\"oldValue\":\"2026-10-14T08:20:00\",\"newValue\":\"2026-10-14T08:25:00\","
            + "\"visitCompletedAtSynchronized\":true,\"reason\":\"SENTINEL-SECRET-VALUE\",\"decisionReason\":\"SENTINEL-SECRET-VALUE\","
            + "\"nested\":{\"oldValue\":\"x\"}}";

        var details = AuditTimelineDetails.Project(code, json);

        Assert.Equal(["newValue", "oldValue", "timestampType", "workSessionId", "workSessionPauseId"], details.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("2026-10-14T08:20:00", details["oldValue"]);
        Assert.Equal("2026-10-14T08:25:00", details["newValue"]);
        Assert.Equal("RESUME", details["timestampType"]);
        Assert.DoesNotContain(details.Values, value => value is string text && text.Contains("SENTINEL", StringComparison.Ordinal));
    }

    [Fact]
    public void TimeCorrectionEvents_WithoutAPauseTarget_OmitThePauseKey_AndAnUnparseablePayloadYieldsNothing()
    {
        var details = AuditTimelineDetails.Project(
            "TIME_CORRECTION_APPROVED",
            "{\"workSessionId\":\"11111111-1111-1111-1111-111111111111\",\"timestampType\":\"CHECK_IN\",\"oldValue\":\"a\",\"newValue\":\"b\"}");

        Assert.DoesNotContain("workSessionPauseId", details.Keys);
        Assert.Empty(AuditTimelineDetails.Project("TIME_CORRECTION_REJECTED", "not json"));
        Assert.Empty(AuditTimelineDetails.Project("TIME_CORRECTION_REQUESTED", null));
    }

    [Theory]
    [InlineData("TIME_CORRECTION", "TIME_CORRECTION_REQUESTED", true)]
    [InlineData("TIME_CORRECTION", "TIME_CORRECTION_APPROVED", true)]
    [InlineData("TIME_CORRECTION", "TIME_CORRECTION_REJECTED", true)]
    [InlineData("TIME_CORRECTION", "TIME_CORRECTION_SOMETHING_ELSE", false)]
    [InlineData("TIME_CORRECTION", "WORK_ORDER_CLOSED", false)]
    [InlineData("WORK_SESSION", "TIME_CORRECTION_APPROVED", false)]
    [InlineData("WORK_ORDER", "TIME_CORRECTION_REQUESTED", false)]
    [InlineData("WORK_ORDER", "WORK_ORDER_CLOSED", true)]
    [InlineData("WORK_SESSION", "WORK_SESSION_PAUSED", true)]
    public void TheEntityTypeAwareProjection_AllowsTheBeforeAfterKeysOnlyForTimeCorrectionActionsOnTimeCorrectionRows(string entityType, string code, bool exposesAnything)
    {
        const string json = "{\"workSessionPauseId\":\"p\",\"costSummaryId\":\"c\",\"oldValue\":\"a\",\"newValue\":\"b\",\"timestampType\":\"CHECK_IN\",\"workSessionId\":\"s\",\"pausedAt\":\"t\"}";

        var details = AuditTimelineDetails.Project(entityType, code, json);

        Assert.Equal(exposesAnything, details.Count > 0);
        if (code != "TIME_CORRECTION_REQUESTED" && code != "TIME_CORRECTION_APPROVED" && code != "TIME_CORRECTION_REJECTED")
        {
            Assert.DoesNotContain("oldValue", details.Keys);
            Assert.DoesNotContain("newValue", details.Keys);
        }
    }

    [Fact]
    public void CostEvents_NeverExposeAmountCurrencyOrNote()
    {
        const string json = "{\"costSummaryId\":\"cs-1\",\"totalAmount\":1234.56,\"currencyCode\":\"USD\",\"note\":\"Parts and labor\"}";

        foreach (var code in new[] { "COST_SUMMARY_PREPARED", "COST_SUMMARY_UPDATED", "COST_SUMMARY_REVIEWED", "WORK_ORDER_CLOSED" })
        {
            var details = AuditTimelineDetails.Project(code, json);

            Assert.Equal(["costSummaryId"], details.Keys);
            Assert.DoesNotContain("1234", string.Join(",", details.Values.Select(v => v?.ToString())), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScalarValues_AreCopiedByType()
    {
        var details = AuditTimelineDetails.Project(
            "WORK_ORDER_SCHEDULED",
            "{\"serviceVisitId\":\"v-1\",\"ownerTeamId\":7,\"assignedTechnicianId\":2.5,\"scheduledStartAt\":null,\"scheduledEndAt\":\"2026-10-01T10:00:00Z\"}");

        Assert.Equal("v-1", details["serviceVisitId"]);
        Assert.Equal(7L, details["ownerTeamId"]);
        Assert.Equal(2.5d, details["assignedTechnicianId"]);
        Assert.Null(details["scheduledStartAt"]);
        Assert.Equal("2026-10-01T10:00:00Z", details["scheduledEndAt"]);
    }

    [Fact]
    public void BooleanValues_AreCopied()
    {
        var details = AuditTimelineDetails.Project("SERVICE_VISIT_MISSED_DECIDED", "{\"decision\":true,\"newServiceVisitId\":false}");

        Assert.Equal(true, details["decision"]);
        Assert.Equal(false, details["newServiceVisitId"]);
    }

    [Fact]
    public void NestedObjectsAndArrays_AreNeverCopied()
    {
        var details = AuditTimelineDetails.Project(
            "WORK_SUMMARY_SUBMITTED",
            "{\"workSummaryId\":{\"nested\":\"" + Sentinel + "\"},\"repairOutcomeCode\":[\"" + Sentinel + "\"]}");

        Assert.Empty(details);
    }

    [Fact]
    public void StringsLongerThan64Characters_AreDropped()
    {
        var details = AuditTimelineDetails.Project(
            "WORK_SUMMARY_SUBMITTED",
            $"{{\"workSummaryId\":\"{new string('x', 64)}\",\"repairOutcomeCode\":\"{new string('y', 65)}\"}}");

        Assert.Equal(new string('x', 64), details["workSummaryId"]);
        Assert.DoesNotContain("repairOutcomeCode", details.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"workSessionId\":")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("123")]
    [InlineData("null")]
    public void MissingMalformedOrNonObjectJson_YieldsEmptyDetails_AndNeverThrows(string? json)
    {
        Assert.Empty(AuditTimelineDetails.Project("SERVICE_VISIT_CHECKED_IN", json));
    }

    [Fact]
    public void KeysAreMatchedCaseSensitively()
    {
        Assert.Empty(AuditTimelineDetails.Project("SERVICE_VISIT_CHECKED_IN", "{\"WorkSessionId\":\"x\",\"WORKSESSIONID\":\"y\"}"));
    }

    [Fact]
    public void Projection_ReturnsOnlyJsonScalarTypes()
    {
        foreach (var row in AllowedKeysByCode())
        {
            var details = AuditTimelineDetails.Project((string)row[0], JsonWith((string[])row[1]));
            Assert.All(details.Values, value => Assert.True(value is null or string or long or double or bool));
        }
    }
}
