using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>`docs/17` P3: only a COMPLETED Visit has a completion time that a CHECK_OUT correction can move.</summary>
public class ServiceVisitCompletionCorrectionTests
{
    private static readonly DateTime Start = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static ServiceVisit NewVisit() =>
        ServiceVisit.Create(Guid.NewGuid(), Guid.NewGuid(), ServiceVisitType.Initial, Guid.NewGuid(), Guid.NewGuid(), Start, Start.AddHours(2));

    [Fact]
    public void CorrectCompletedAt_MovesTheCompletionTimeOfACompletedVisit_AndKeepsItCompleted()
    {
        var visit = NewVisit();
        visit.CheckIn();
        visit.CheckOut(Start.AddHours(1));

        visit.CorrectCompletedAt(Start.AddMinutes(50));

        Assert.Equal(Start.AddMinutes(50), visit.CompletedAt);
        Assert.Equal(ServiceVisitStatus.Completed, visit.Status);
    }

    [Fact]
    public void CorrectCompletedAt_IsRefusedForAVisitThatIsNotCompleted_AndForANonUtcValue()
    {
        var scheduled = NewVisit();
        Assert.Throws<DomainRuleViolationException>(() => scheduled.CorrectCompletedAt(Start));

        var inProgress = NewVisit();
        inProgress.CheckIn();
        Assert.Throws<DomainRuleViolationException>(() => inProgress.CorrectCompletedAt(Start));

        var completed = NewVisit();
        completed.CheckIn();
        completed.CheckOut(Start.AddHours(1));
        Assert.Throws<ArgumentException>(() => completed.CorrectCompletedAt(DateTime.SpecifyKind(Start, DateTimeKind.Local)));
    }
}
