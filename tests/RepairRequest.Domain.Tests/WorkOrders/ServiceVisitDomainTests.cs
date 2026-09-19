using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// S2-003 Service Visit invariants: ST-SV-001 Create (SCHEDULED); ST-SV-004/005 Reschedule (settles back at
/// SCHEDULED within one call); ST-SV-006 Reassign (stays SCHEDULED); ST-SV-007 Cancel (SCHEDULED -&gt; CANCELLED);
/// ST-SV-008 Mark Missed (SCHEDULED -&gt; MISSED); ST-SV-009/D-15 Decide Missed (MISSED stays MISSED, decided once).
/// </summary>
public class ServiceVisitDomainTests
{
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    private static ServiceVisit CreateVisit() =>
        ServiceVisit.Create(Guid.NewGuid(), Guid.NewGuid(), ServiceVisitType.Initial, Guid.NewGuid(), Guid.NewGuid(), Start, End);

    private static ServiceVisit VisitIn(ServiceVisitStatus status)
    {
        var visit = CreateVisit();
        if (status != ServiceVisitStatus.Scheduled)
        {
            typeof(ServiceVisit).GetProperty(nameof(ServiceVisit.Status))!.SetValue(visit, status);
        }

        return visit;
    }

    // ---------------- Create (ST-SV-001) ----------------

    [Fact]
    public void Create_SetsFieldsAndStartsScheduled()
    {
        var tenantId = Guid.NewGuid();
        var workOrderId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var technicianId = Guid.NewGuid();

        var visit = ServiceVisit.Create(tenantId, workOrderId, ServiceVisitType.Initial, teamId, technicianId, Start, End);

        Assert.Equal(tenantId, visit.TenantId);
        Assert.Equal(workOrderId, visit.WorkOrderId);
        Assert.Equal(ServiceVisitType.Initial, visit.VisitType);
        Assert.Equal(ServiceVisitStatus.Scheduled, visit.Status);
        Assert.Equal(teamId, visit.AssignedTeamId);
        Assert.Equal(technicianId, visit.AssignedTechnicianId);
        Assert.Equal(Start, visit.ScheduledStartAt);
        Assert.Equal(End, visit.ScheduledEndAt);
        Assert.Null(visit.SourceMissedVisitId);
        Assert.Null(visit.MissedDecisionCode);
    }

    [Fact]
    public void Create_SetsTheSourceMissedVisitId_ForAFollowUp()
    {
        var sourceId = Guid.NewGuid();
        var visit = ServiceVisit.Create(
            Guid.NewGuid(), Guid.NewGuid(), ServiceVisitType.FollowUp, Guid.NewGuid(), Guid.NewGuid(), Start, End, sourceMissedVisitId: sourceId);

        Assert.Equal(sourceId, visit.SourceMissedVisitId);
    }

    [Fact]
    public void Create_RequiresANonEmptyTeam() =>
        Assert.Throws<ArgumentException>(() => ServiceVisit.Create(Guid.NewGuid(), Guid.NewGuid(), ServiceVisitType.Initial, Guid.Empty, Guid.NewGuid(), Start, End));

    [Fact]
    public void Create_RequiresANonEmptyTechnician() =>
        Assert.Throws<ArgumentException>(() => ServiceVisit.Create(Guid.NewGuid(), Guid.NewGuid(), ServiceVisitType.Initial, Guid.NewGuid(), Guid.Empty, Start, End));

    [Fact]
    public void Create_RejectsAnEndBeforeTheStart() =>
        Assert.Throws<ArgumentException>(() => ServiceVisit.Create(Guid.NewGuid(), Guid.NewGuid(), ServiceVisitType.Initial, Guid.NewGuid(), Guid.NewGuid(), End, Start));

    // ---------------- Reschedule (ST-SV-004/005) ----------------

    [Fact]
    public void Reschedule_FromScheduled_UpdatesTheWindow_AndStaysScheduled()
    {
        var visit = CreateVisit();
        var newStart = Start.AddDays(1);
        var newEnd = End.AddDays(1);

        visit.Reschedule("Customer requested a later slot", newStart, newEnd);

        Assert.Equal(ServiceVisitStatus.Scheduled, visit.Status);
        Assert.Equal(newStart, visit.ScheduledStartAt);
        Assert.Equal(newEnd, visit.ScheduledEndAt);
        Assert.Equal("Customer requested a later slot", visit.RescheduleReason);
    }

    [Fact]
    public void Reschedule_RequiresAReason() =>
        Assert.Throws<ArgumentException>(() => CreateVisit().Reschedule(" ", Start, End));

    [Fact]
    public void Reschedule_RejectsAnEndBeforeTheStart() =>
        Assert.Throws<ArgumentException>(() => CreateVisit().Reschedule("reason", End, Start));

    [Theory]
    [InlineData(ServiceVisitStatus.Rescheduled)]
    [InlineData(ServiceVisitStatus.InProgress)]
    [InlineData(ServiceVisitStatus.Completed)]
    [InlineData(ServiceVisitStatus.Missed)]
    [InlineData(ServiceVisitStatus.Cancelled)]
    public void Reschedule_FromAnyStateOtherThanScheduled_IsDenied(ServiceVisitStatus status)
    {
        var visit = VisitIn(status);

        Assert.Throws<DomainRuleViolationException>(() => visit.Reschedule("reason", Start.AddDays(1), End.AddDays(1)));

        Assert.Equal(status, visit.Status);
        Assert.Null(visit.RescheduleReason);
    }

    // ---------------- Reassign (ST-SV-006) ----------------

    [Fact]
    public void Reassign_FromScheduled_UpdatesTeamAndTechnician_AndStaysScheduled()
    {
        var visit = CreateVisit();
        var newTeam = Guid.NewGuid();
        var newTechnician = Guid.NewGuid();

        visit.Reassign("Original technician is unavailable", newTeam, newTechnician);

        Assert.Equal(ServiceVisitStatus.Scheduled, visit.Status);
        Assert.Equal(newTeam, visit.AssignedTeamId);
        Assert.Equal(newTechnician, visit.AssignedTechnicianId);
        Assert.Equal("Original technician is unavailable", visit.ReassignReason);
    }

    [Fact]
    public void Reassign_RequiresAReason() =>
        Assert.Throws<ArgumentException>(() => CreateVisit().Reassign(" ", Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public void Reassign_RequiresANonEmptyTeam() =>
        Assert.Throws<ArgumentException>(() => CreateVisit().Reassign("reason", Guid.Empty, Guid.NewGuid()));

    [Fact]
    public void Reassign_RequiresANonEmptyTechnician() =>
        Assert.Throws<ArgumentException>(() => CreateVisit().Reassign("reason", Guid.NewGuid(), Guid.Empty));

    [Theory]
    [InlineData(ServiceVisitStatus.Rescheduled)]
    [InlineData(ServiceVisitStatus.InProgress)]
    [InlineData(ServiceVisitStatus.Completed)]
    [InlineData(ServiceVisitStatus.Missed)]
    [InlineData(ServiceVisitStatus.Cancelled)]
    public void Reassign_FromAnyStateOtherThanScheduled_IsDenied(ServiceVisitStatus status)
    {
        var visit = VisitIn(status);
        var originalTeam = visit.AssignedTeamId;
        var originalTechnician = visit.AssignedTechnicianId;

        Assert.Throws<DomainRuleViolationException>(() => visit.Reassign("reason", Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(status, visit.Status);
        Assert.Equal(originalTeam, visit.AssignedTeamId);
        Assert.Equal(originalTechnician, visit.AssignedTechnicianId);
    }

    // ---------------- Cancel (ST-SV-007) ----------------

    [Fact]
    public void Cancel_FromScheduled_MovesToCancelled()
    {
        var visit = CreateVisit();

        visit.Cancel("No longer needed");

        Assert.Equal(ServiceVisitStatus.Cancelled, visit.Status);
        Assert.Equal("No longer needed", visit.CancelReason);
    }

    [Fact]
    public void Cancel_RequiresAReason() =>
        Assert.Throws<ArgumentException>(() => CreateVisit().Cancel(" "));

    [Theory]
    [InlineData(ServiceVisitStatus.Rescheduled)]
    [InlineData(ServiceVisitStatus.InProgress)]
    [InlineData(ServiceVisitStatus.Completed)]
    [InlineData(ServiceVisitStatus.Missed)]
    [InlineData(ServiceVisitStatus.Cancelled)]
    public void Cancel_FromAnyStateOtherThanScheduled_IsDenied(ServiceVisitStatus status)
    {
        var visit = VisitIn(status);

        Assert.Throws<DomainRuleViolationException>(() => visit.Cancel("reason"));

        Assert.Equal(status, visit.Status);
    }

    // ---------------- Mark Missed (ST-SV-008) ----------------

    [Fact]
    public void MarkMissed_FromScheduled_MovesToMissed()
    {
        var visit = CreateVisit();

        visit.MarkMissed("Technician could not reach the site");

        Assert.Equal(ServiceVisitStatus.Missed, visit.Status);
        Assert.Equal("Technician could not reach the site", visit.MissedReason);
    }

    [Fact]
    public void MarkMissed_RequiresAReason() =>
        Assert.Throws<ArgumentException>(() => CreateVisit().MarkMissed(" "));

    [Theory]
    [InlineData(ServiceVisitStatus.Rescheduled)]
    [InlineData(ServiceVisitStatus.InProgress)]
    [InlineData(ServiceVisitStatus.Completed)]
    [InlineData(ServiceVisitStatus.Missed)]
    [InlineData(ServiceVisitStatus.Cancelled)]
    public void MarkMissed_FromAnyStateOtherThanScheduled_IsDenied(ServiceVisitStatus status)
    {
        var visit = VisitIn(status);

        Assert.Throws<DomainRuleViolationException>(() => visit.MarkMissed("reason"));

        Assert.Equal(status, visit.Status);
    }

    // ---------------- Check-in (ST-SV-002, S3-001) ----------------

    [Fact]
    public void CheckIn_FromScheduled_MovesToInProgress()
    {
        var visit = CreateVisit();

        visit.CheckIn();

        Assert.Equal(ServiceVisitStatus.InProgress, visit.Status);
    }

    [Theory]
    [InlineData(ServiceVisitStatus.Rescheduled)]
    [InlineData(ServiceVisitStatus.InProgress)]
    [InlineData(ServiceVisitStatus.Completed)]
    [InlineData(ServiceVisitStatus.Missed)]
    [InlineData(ServiceVisitStatus.Cancelled)]
    public void CheckIn_FromAnyStateOtherThanScheduled_IsDenied(ServiceVisitStatus status)
    {
        var visit = VisitIn(status);

        Assert.Throws<DomainRuleViolationException>(() => visit.CheckIn());

        Assert.Equal(status, visit.Status);
    }

    // ---------------- Decide Missed (ST-SV-009/D-15) ----------------

    [Theory]
    [InlineData(MissedVisitDecisionCode.Reschedule)]
    [InlineData(MissedVisitDecisionCode.FollowUp)]
    [InlineData(MissedVisitDecisionCode.Reassign)]
    [InlineData(MissedVisitDecisionCode.NoFollowUp)]
    public void DecideMissed_FromMissed_RecordsTheDecision_AndStaysMissed(MissedVisitDecisionCode decision)
    {
        var visit = VisitIn(ServiceVisitStatus.Missed);
        var decidedAt = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

        visit.DecideMissed(decision, decidedAt);

        Assert.Equal(ServiceVisitStatus.Missed, visit.Status);
        Assert.Equal(decision, visit.MissedDecisionCode);
        Assert.Equal(decidedAt, visit.MissedDecidedAt);
    }

    [Fact]
    public void DecideMissed_AlreadyDecided_IsDenied_AndKeepsTheOriginalDecision()
    {
        var visit = VisitIn(ServiceVisitStatus.Missed);
        var decidedAt = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);
        visit.DecideMissed(MissedVisitDecisionCode.NoFollowUp, decidedAt);

        Assert.Throws<DomainRuleViolationException>(() => visit.DecideMissed(MissedVisitDecisionCode.Reschedule, decidedAt.AddMinutes(1)));

        Assert.Equal(MissedVisitDecisionCode.NoFollowUp, visit.MissedDecisionCode);
        Assert.Equal(decidedAt, visit.MissedDecidedAt);
    }

    [Theory]
    [InlineData(ServiceVisitStatus.Scheduled)]
    [InlineData(ServiceVisitStatus.Rescheduled)]
    [InlineData(ServiceVisitStatus.InProgress)]
    [InlineData(ServiceVisitStatus.Completed)]
    [InlineData(ServiceVisitStatus.Cancelled)]
    public void DecideMissed_FromAnyStateOtherThanMissed_IsDenied(ServiceVisitStatus status)
    {
        var visit = VisitIn(status);

        Assert.Throws<DomainRuleViolationException>(() => visit.DecideMissed(MissedVisitDecisionCode.NoFollowUp, DateTime.UtcNow));

        Assert.Equal(status, visit.Status);
        Assert.Null(visit.MissedDecisionCode);
    }
}
