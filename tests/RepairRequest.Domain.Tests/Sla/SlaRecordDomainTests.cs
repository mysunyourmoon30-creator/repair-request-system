using System.Reflection;
using RepairRequest.Domain.Sla;

namespace RepairRequest.Domain.Tests.Sla;

/// <summary>`docs/16` D5-D7: SLA-004/v1 policy, 24/7 elapsed UTC time, start-only construction, immutable record.</summary>
public class SlaRecordDomainTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 8, 30, 15, 123, DateTimeKind.Utc);

    [Fact]
    public void PolicyVersion_IsExactlySla004V1()
    {
        Assert.Equal("SLA-004/v1", SlaPolicy.Version);
        Assert.Equal(TimeSpan.FromHours(24), SlaPolicy.Window);
        Assert.Equal(TimeSpan.FromHours(4), SlaPolicy.RiskLead);
    }

    [Fact]
    public void Start_SetsPolicyVersion_DueAfter24Hours_AndRiskFourHoursBeforeDue()
    {
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var record = SlaRecord.Start(tenantId, requestId, Start);

        Assert.NotEqual(Guid.Empty, record.Id);
        Assert.Equal(tenantId, record.TenantId);
        Assert.Equal(requestId, record.RepairRequestId);
        Assert.Equal("SLA-004/v1", record.SlaPolicyVersion);
        Assert.Equal(Start, record.StartedAt);
        Assert.Equal(Start.AddHours(24), record.OriginalDueAt);
        Assert.Equal(record.OriginalDueAt.AddHours(-4), record.RiskAt);
        Assert.Equal(Start.AddHours(20), record.RiskAt);
        Assert.Null(record.StoppedAt);
        Assert.Null(record.StopReason);
        Assert.False(record.IsStopped);
    }

    [Fact]
    public void Start_IsPureElapsedTime_AcrossWeekendHolidayAndDaylightSavingBoundaries()
    {
        // Saturday 23:00 UTC: 24 elapsed hours later is Sunday 23:00 UTC — no working calendar, no zone.
        var saturday = new DateTime(2026, 10, 10, 23, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 11, 23, 0, 0, DateTimeKind.Utc), SlaRecord.Start(Guid.NewGuid(), Guid.NewGuid(), saturday).OriginalDueAt);

        // A US DST change day (2026-11-01) still adds exactly 24 hours of UTC time.
        var dst = new DateTime(2026, 11, 1, 0, 30, 0, DateTimeKind.Utc);
        Assert.Equal(dst.AddHours(24), SlaRecord.Start(Guid.NewGuid(), Guid.NewGuid(), dst).OriginalDueAt);
    }

    [Fact]
    public void Start_TruncatesToTheDatabaseMillisecondPrecision_KeepingDueAndRiskExact()
    {
        var withTicks = Start.AddTicks(4567);

        var record = SlaRecord.Start(Guid.NewGuid(), Guid.NewGuid(), withTicks);

        Assert.Equal(Start, record.StartedAt);
        Assert.Equal(DateTimeKind.Utc, record.StartedAt.Kind);
        Assert.Equal(Start.AddHours(24), record.OriginalDueAt);
        Assert.Equal(Start.AddHours(20), record.RiskAt);
        Assert.Equal(0, record.OriginalDueAt.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void Start_RejectsEmptyIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => SlaRecord.Start(Guid.Empty, Guid.NewGuid(), Start));
        Assert.Throws<ArgumentException>(() => SlaRecord.Start(Guid.NewGuid(), Guid.Empty, Start));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Start_RejectsNonUtcTimes(DateTimeKind kind) =>
        Assert.Throws<ArgumentException>(() => SlaRecord.Start(Guid.NewGuid(), Guid.NewGuid(), DateTime.SpecifyKind(Start, kind)));

    [Fact]
    public void Record_ExposesNoMutatorAndNoRestartOrStopMethod()
    {
        var type = typeof(SlaRecord);

        // No public setter on any property, no public constructor, and no instance method that could stop, edit or restart.
        Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.True(property.SetMethod is null || !property.SetMethod.IsPublic, property.Name));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);
        Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly), method => method.Name == nameof(SlaRecord.Start));
    }

    [Theory]
    [InlineData(SlaStopReason.RequestRejected, "REQUEST_REJECTED")]
    [InlineData(SlaStopReason.RequestCancelled, "REQUEST_CANCELLED")]
    [InlineData(SlaStopReason.WoClosed, "WO_CLOSED")]
    [InlineData(SlaStopReason.WoCancelled, "WO_CANCELLED")]
    public void StopReasonCodes_AreTheExactBaselineStrings(SlaStopReason reason, string code) =>
        Assert.Equal(code, SlaStopReasonCodes.ToCode(reason));

    [Fact]
    public void StopReason_HasExactlyTheFourApprovedValues() =>
        Assert.Equal(4, Enum.GetValues<SlaStopReason>().Length);
}
