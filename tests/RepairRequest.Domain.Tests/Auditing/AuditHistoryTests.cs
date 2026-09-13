using RepairRequest.Domain.Auditing;

namespace RepairRequest.Domain.Tests.Auditing;

public class AuditHistoryTests
{
    private static AuditHistory Create(
        string actionCode = "SUBMIT",
        DateTimeKind occurredAtKind = DateTimeKind.Utc,
        Guid? correlationId = null) =>
        new(
            Guid.NewGuid(),
            "REPAIR_REQUEST",
            Guid.NewGuid(),
            actionCode,
            "DRAFT",
            "SUBMITTED",
            null,
            null,
            null,
            Guid.NewGuid(),
            new DateTime(2026, 9, 13, 8, 0, 0, occurredAtKind),
            correlationId ?? Guid.NewGuid());

    [Fact]
    public void ValidRecord_CapturesTransition()
    {
        var audit = Create();

        Assert.Equal("DRAFT", audit.FromState);
        Assert.Equal("SUBMITTED", audit.ToState);
        Assert.Equal(DateTimeKind.Utc, audit.OccurredAt.Kind);
    }

    [Fact]
    public void NonUtcOccurredAt_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(occurredAtKind: DateTimeKind.Local));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankActionCode_IsRejected(string actionCode)
    {
        Assert.Throws<ArgumentException>(() => Create(actionCode: actionCode));
    }

    [Fact]
    public void MissingCorrelationId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(correlationId: Guid.Empty));
    }
}
