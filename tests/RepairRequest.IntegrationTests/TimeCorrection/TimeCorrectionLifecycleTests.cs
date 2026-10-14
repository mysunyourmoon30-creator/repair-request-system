using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;
using RepairRequest.IntegrationTests.WorkEvidence;

namespace RepairRequest.IntegrationTests.TimeCorrection;

/// <summary>
/// Time Correction through the real services and stores against LocalDB (`docs/17`, owner approval of 13 October 2026): scope and
/// ownership, the exact pause target, what an approval changes and what it never touches, no self-decision, duplicate pending, stale value,
/// chronology at request and approval, the Work Order guard (Reject still possible) and the downstream data (SLA, Work Summary, Acceptance,
/// Cost Summary, Service Report facts).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class TimeCorrectionLifecycleTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host = new();
    private readonly TimeCorrectionDriver _driver;

    public TimeCorrectionLifecycleTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _driver = new TimeCorrectionDriver(_host);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CurrentUser TeamLead(TimeCorrectionWorld world) => world.Base.TeamLead;

    private static CurrentUser Supervisor(TimeCorrectionWorld world) => world.Base.Supervisor;

    private static void AssertFailure(CommandFailure expected, CommandResult<Guid> result) =>
        Assert.Equal(expected, result.Error?.Failure);

    // ---------------- the four types, pause history and mirrors ----------------

    [Fact]
    public async Task AllFourTypes_AreRequestedByATeamLead_AndApprovedBySupervisor_ChangingExactlyTheTargetedFacts()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var checkOutVisit = world.Base.VisitId;
        var sessionVersionBefore = await _driver.SessionRowVersionAsync(world.SessionId);

        var checkIn = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-5));
        var pauseStart = await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(12), world.Pause1);
        var resumeOfLatest = await _driver.RequestAsync(TeamLead(world), world.SessionId, "RESUME", world.At(42), world.Pause2);
        var checkOut = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_OUT", world.At(55));
        foreach (var requested in new[] { checkIn, pauseStart, resumeOfLatest, checkOut })
        {
            Assert.True(requested.Succeeded, requested.Error?.Message);
        }

        // A request changes no time fact.
        Assert.Equal(world.CheckIn, (await _driver.SessionAsync(world.SessionId)).CheckIn);
        Assert.Equal(sessionVersionBefore, await _driver.SessionRowVersionAsync(world.SessionId));

        foreach (var id in new[] { checkIn.Value, pauseStart.Value, resumeOfLatest.Value, checkOut.Value })
        {
            var approved = await _driver.ApproveAsync(Supervisor(world), id);
            Assert.True(approved.Succeeded, approved.Error?.Message);
        }

        var session = await _driver.SessionAsync(world.SessionId);
        Assert.Equal(world.At(-5), session.CheckIn);
        Assert.Equal(world.At(55), session.CheckOut);
        Assert.Equal(WorkSessionStatus.CheckedOut, session.Status);
        Assert.Null(session.PauseStart);                                  // no pause is open: nothing to mirror for PAUSE_START
        Assert.Equal(world.At(42), session.Resume);                       // the latest closed pause mirrors RESUME
        Assert.Equal((world.At(12), world.At(20)), await _driver.PauseAsync(world.Pause1));
        Assert.Equal((world.At(30), (DateTime?)world.At(42)), await _driver.PauseAsync(world.Pause2));
        Assert.Equal(world.At(55), await _driver.VisitCompletedAtAsync(checkOutVisit)); // the only session of the Visit: authoritative source
        Assert.NotEqual(sessionVersionBefore, await _driver.SessionRowVersionAsync(world.SessionId));

        Assert.Equal(4, await _driver.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionRequestedAction));
        Assert.Equal(4, await _driver.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));

        // Every decision row carries the session id; pause corrections also carry the exact pause.
        var approvals = await _driver.WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(audit => audit.TenantId == world.TenantId && audit.ActionCode == WorkOrderAudit.TimeCorrectionApprovedAction)
            .Select(audit => audit.NewValueJson!).ToListAsync());
        Assert.All(approvals, json => Assert.Contains(world.SessionId.ToString(), json, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, approvals.Count(json => json.Contains(world.Pause1.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, approvals.Count(json => json.Contains(world.Pause2.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(approvals, json => json.Contains("\"visitCompletedAtSynchronized\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeOfAnOlderPause_LeavesTheLatestEventColumnAlone_AndPauseStartOfAnOpenPauseIsMirrored()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var older = await _driver.RequestAsync(TeamLead(world), world.SessionId, "RESUME", world.At(22), world.Pause1);
        Assert.True((await _driver.ApproveAsync(Supervisor(world), older.Value)).Succeeded);

        Assert.Equal((world.At(10), (DateTime?)world.At(22)), await _driver.PauseAsync(world.Pause1));
        Assert.Equal(world.At(40), (await _driver.SessionAsync(world.SessionId)).Resume);

        // An open pause: a fresh session with one open pause.
        var open = await TimeCorrectionWorlds.CreateAsync(_host);
        await _driver.WithDbAsync(async db =>
        {
            // Re-open the history: turn the checked-out session into a paused one (test setup written directly).
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_session] SET [status] = 'PAUSED', [check_out_at] = NULL, [pause_start_at] = {open.At(70)} WHERE [work_session_id] = {open.SessionId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [work_session_pause] ([work_session_pause_id], [tenant_id], [work_session_id], [paused_at], [pause_reason], [resumed_at]) VALUES ({Guid.NewGuid()}, {open.TenantId}, {open.SessionId}, {open.At(70)}, 'Waiting', NULL)");
            return 0;
        });
        var openPauseId = await _driver.WithDbAsync(db => db.WorkSessionPauses.AsNoTracking()
            .Where(pause => pause.WorkSessionId == open.SessionId && pause.ResumedAt == null).Select(pause => pause.Id).SingleAsync());

        var start = await _driver.RequestAsync(TeamLead(open), open.SessionId, "PAUSE_START", open.At(65), openPauseId);
        Assert.True((await _driver.ApproveAsync(Supervisor(open), start.Value)).Succeeded);

        Assert.Equal(open.At(65), (await _driver.PauseAsync(openPauseId)).PausedAt);
        var session2 = await _driver.SessionAsync(open.SessionId);
        Assert.Equal(open.At(65), session2.PauseStart);                       // the open pause is mirrored
        Assert.Equal(WorkSessionStatus.Paused, session2.Status);
    }

    [Fact]
    public async Task ApprovalOfACheckOut_DoesNotOverwriteAnotherSessionsCompletion()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        // A second session on the same Visit makes the corrected Check-out no longer the Visit's only (authoritative) source.
        await _driver.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [work_session] ([work_session_id], [tenant_id], [service_visit_id], [technician_id], [status], [check_in_at], [check_out_at])
                VALUES ({Guid.NewGuid()}, {world.TenantId}, {world.Base.VisitId}, {world.Base.OtherTechnician.UserId}, 'CHECKED_OUT', {world.At(61)}, {world.At(65)})
                """);
            return 0;
        });

        var request = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_OUT", world.At(55));
        Assert.True((await _driver.ApproveAsync(Supervisor(world), request.Value)).Succeeded);

        Assert.Equal(world.At(55), (await _driver.SessionAsync(world.SessionId)).CheckOut);
        Assert.Equal(world.CheckOut, await _driver.VisitCompletedAtAsync(world.Base.VisitId)); // untouched
        var audit = await _driver.WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(item => item.TenantId == world.TenantId && item.ActionCode == WorkOrderAudit.TimeCorrectionApprovedAction).Select(item => item.NewValueJson!).SingleAsync());
        Assert.Contains("\"visitCompletedAtSynchronized\":false", audit, StringComparison.Ordinal);
    }

    // ---------------- scope, ownership, roles ----------------

    [Fact]
    public async Task OnlyTheOwningTechnician_AndATeamLeadOfTheSite_CanRequest_AndOnlyASupervisorOfTheSiteCanDecideAndList()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var otherTenant = await TimeCorrectionWorlds.CreateAsync(_host);
        var outOfSiteTeamLead = new CurrentUser(
            (await _host.CreateUserInTenantAsync(world.TenantId, RoleCodes.TeamLead)).Id, world.TenantId, [RoleCodes.TeamLead]); // no Site scope at all
        var outOfSiteSupervisor = new CurrentUser(
            (await _host.CreateUserInTenantAsync(world.TenantId, RoleCodes.Supervisor)).Id, world.TenantId, [RoleCodes.Supervisor]);

        AssertFailure(CommandFailure.NotFound, await _driver.RequestAsync(world.Base.OtherTechnician, world.SessionId, "CHECK_IN", world.At(-5)));
        AssertFailure(CommandFailure.NotFound, await _driver.RequestAsync(outOfSiteTeamLead, world.SessionId, "CHECK_IN", world.At(-5)));
        AssertFailure(CommandFailure.NotFound, await _driver.RequestAsync(otherTenant.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5)));
        AssertFailure(CommandFailure.NotFound, await _driver.RequestAsync(Supervisor(world), world.SessionId, "CHECK_IN", world.At(-5)));   // a Supervisor never requests
        Assert.Equal(0, await _driver.WithDbAsync(db => db.TimeCorrections.CountAsync(item => item.TenantId == world.TenantId)));

        var byTechnician = await _driver.RequestAsync(world.Base.Technician, world.SessionId, "CHECK_IN", world.At(-5));
        Assert.True(byTechnician.Succeeded, byTechnician.Error?.Message);

        // Decisions: Team Lead, the other Technician, an out-of-site Supervisor and another tenant's Supervisor all get the same 404.
        foreach (var caller in new[] { TeamLead(world), world.Base.OtherTechnician, outOfSiteSupervisor, otherTenant.Base.Supervisor })
        {
            AssertFailure(CommandFailure.NotFound, await _driver.ApproveAsync(caller, byTechnician.Value, rowVersion: [1, 2, 3, 4, 5, 6, 7, 8]));
            AssertFailure(CommandFailure.NotFound, await _driver.RejectAsync(caller, byTechnician.Value, rowVersion: [1, 2, 3, 4, 5, 6, 7, 8]));
        }

        // The pending list: in-scope Supervisor sees it, out-of-site and other-tenant Supervisors and a Team Lead see nothing.
        async Task<int> PendingFor(CurrentUser user)
        {
            await using var scope = _host.CreateScope();
            var page = await scope.ServiceProvider.GetRequiredService<TimeCorrectionService>().ListPendingAsync(
                user, new PendingTimeCorrectionsQuery(new PageRequest(1, 50)), CancellationToken.None);
            return page.TotalCount;
        }

        Assert.Equal(1, await PendingFor(Supervisor(world)));
        Assert.Equal(0, await PendingFor(outOfSiteSupervisor));
        Assert.Equal(0, await PendingFor(otherTenant.Base.Supervisor));
        Assert.Equal(0, await PendingFor(TeamLead(world)));
    }

    [Fact]
    public async Task ANoSelfDecisionRule_HoldsEvenForAUserWhoIsBothTeamLeadAndSupervisor()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var both = await TimeCorrectionWorlds.UserAsync(_host, world, RoleCodes.TeamLead, RoleCodes.Supervisor);

        var request = await _driver.RequestAsync(both, world.SessionId, "CHECK_IN", world.At(-5));
        Assert.True(request.Succeeded, request.Error?.Message);

        AssertFailure(CommandFailure.AccessDenied, await _driver.ApproveAsync(both, request.Value));
        AssertFailure(CommandFailure.AccessDenied, await _driver.RejectAsync(both, request.Value));
        Assert.Equal(world.CheckIn, (await _driver.SessionAsync(world.SessionId)).CheckIn);
        Assert.Equal(TimeCorrectionStatus.Pending, await _driver.WithDbAsync(db => db.TimeCorrections.AsNoTracking().Where(item => item.Id == request.Value).Select(item => item.Status).SingleAsync()));

        Assert.True((await _driver.ApproveAsync(Supervisor(world), request.Value)).Succeeded);
    }

    // ---------------- duplicates, stale values, chronology ----------------

    [Fact]
    public async Task OnePendingCorrectionPerLogicalTarget_IsEnforcedForNullableAndPauseTargets_AndFreesUpAfterTheDecision()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);

        var first = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-5));
        AssertFailure(CommandFailure.StateConflict, await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-6)));
        var pause1 = await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(12), world.Pause1);
        AssertFailure(CommandFailure.StateConflict, await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(13), world.Pause1));
        var pause2 = await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(32), world.Pause2); // another pause: a different target
        var resume1 = await _driver.RequestAsync(TeamLead(world), world.SessionId, "RESUME", world.At(22), world.Pause1);      // another type on the same pause
        foreach (var ok in new[] { first, pause1, pause2, resume1 })
        {
            Assert.True(ok.Succeeded, ok.Error?.Message);
        }

        // Once decided the target is free again — after a Reject and after an Approve.
        Assert.True((await _driver.RejectAsync(Supervisor(world), first.Value)).Succeeded);
        Assert.True((await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-6))).Succeeded);
        Assert.True((await _driver.ApproveAsync(Supervisor(world), pause1.Value)).Succeeded);
        Assert.True((await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(11), world.Pause1)).Succeeded);

        Assert.Equal(1, await _driver.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionRejectedAction));
    }

    [Fact]
    public async Task ManyConcurrentRequestsForTheSameTarget_ProduceExactlyOnePendingCorrection()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-1 - index)))));

        Assert.Equal(1, results.Count(result => result.Succeeded));
        Assert.All(results.Where(result => !result.Succeeded), result =>
            Assert.True(result.Error?.Failure is CommandFailure.StateConflict or CommandFailure.ConcurrencyConflict, result.Error?.Failure.ToString()));
        Assert.Equal(1, await _driver.WithDbAsync(db => db.TimeCorrections.CountAsync(item => item.WorkSessionId == world.SessionId)));
        Assert.Equal(1, await _driver.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionRequestedAction));
    }

    [Fact]
    public async Task Approve_IsRefused_WhenTheTargetChangedSinceTheRequest_AndNothingIsApplied()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var request = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_OUT", world.At(55));

        // Another path changed the target after the request (written directly: no application path changes it any more).
        await _driver.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_session] SET [check_out_at] = {world.At(58)} WHERE [work_session_id] = {world.SessionId}"));

        var result = await _driver.ApproveAsync(Supervisor(world), request.Value);

        AssertFailure(CommandFailure.StateConflict, result);
        Assert.Equal(world.At(58), (await _driver.SessionAsync(world.SessionId)).CheckOut);
        Assert.Equal(TimeCorrectionStatus.Pending, await _driver.WithDbAsync(db => db.TimeCorrections.AsNoTracking().Where(item => item.Id == request.Value).Select(item => item.Status).SingleAsync()));
        Assert.Equal(0, await _driver.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
    }

    [Fact]
    public async Task Chronology_IsCheckedAtRequest_AgainstEveryAffectedInterval_AndRevalidatedAtApproval()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);

        // At request: check-in after the first pause, resume after the check-out, pause start before the earlier pause ended, overlapping, in the future.
        AssertFailure(CommandFailure.ValidationFailed, await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(15)));
        AssertFailure(CommandFailure.ValidationFailed, await _driver.RequestAsync(TeamLead(world), world.SessionId, "RESUME", world.At(70), world.Pause2));
        AssertFailure(CommandFailure.ValidationFailed, await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(15), world.Pause2));
        AssertFailure(CommandFailure.ValidationFailed, await _driver.RequestAsync(TeamLead(world), world.SessionId, "RESUME", world.At(35), world.Pause1));
        AssertFailure(CommandFailure.ValidationFailed, await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_OUT", DateTime.UtcNow.AddMinutes(10)));
        AssertFailure(CommandFailure.ValidationFailed, await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_OUT", world.CheckOut));

        // At approval: both requests are valid on their own; applying the first makes the second invalid against the *current* intervals.
        var movePause = await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(2), world.Pause1);
        var moveCheckIn = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(5));
        Assert.True(movePause.Succeeded && moveCheckIn.Succeeded);
        Assert.True((await _driver.ApproveAsync(Supervisor(world), movePause.Value)).Succeeded);

        AssertFailure(CommandFailure.StateConflict, await _driver.ApproveAsync(Supervisor(world), moveCheckIn.Value));
        Assert.Equal(world.CheckIn, (await _driver.SessionAsync(world.SessionId)).CheckIn);
    }

    [Fact]
    public async Task ApprovingTheSameCorrectionTwice_Or_ARejectedOne_IsAStateConflict()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var request = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-5));
        var token = await _driver.RowVersionAsync(request.Value);

        Assert.True((await _driver.ApproveAsync(Supervisor(world), request.Value, token)).Succeeded);

        AssertFailure(CommandFailure.ConcurrencyConflict, await _driver.ApproveAsync(Supervisor(world), request.Value, token));      // stale If-Match
        AssertFailure(CommandFailure.StateConflict, await _driver.ApproveAsync(Supervisor(world), request.Value));                  // fresh token, already approved
        AssertFailure(CommandFailure.StateConflict, await _driver.RejectAsync(Supervisor(world), request.Value));
        Assert.Equal(1, await _driver.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
    }

    // ---------------- Work Order guard ----------------

    [Theory]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public async Task OnAClosedOrCancelledWorkOrder_RequestAndApproveAreRefused_ButAPendingRequestCanStillBeRejected(string terminal)
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var pendingForApprove = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-5));
        var pendingForReject = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_OUT", world.At(55));
        await _driver.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = {terminal} WHERE [work_order_id] = {world.WorkOrderId}"));
        var facts = await _driver.SessionAsync(world.SessionId);

        AssertFailure(CommandFailure.StateConflict, await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(12), world.Pause1));
        AssertFailure(CommandFailure.StateConflict, await _driver.ApproveAsync(Supervisor(world), pendingForApprove.Value));
        Assert.Equal(facts, await _driver.SessionAsync(world.SessionId));

        var rejected = await _driver.RejectAsync(Supervisor(world), pendingForReject.Value);
        Assert.True(rejected.Succeeded, rejected.Error?.Message);
        Assert.Equal(facts, await _driver.SessionAsync(world.SessionId));                        // no time fact changed
        Assert.Equal(world.CheckOut, await _driver.VisitCompletedAtAsync(world.Base.VisitId));
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("SCHEDULED")]
    [InlineData("AWAITING_SUPERVISOR_REVIEW")]
    [InlineData("AWAITING_CUSTOMER_ACCEPTANCE")]
    [InlineData("COMPLETED")]
    [InlineData("CORRECTIVE_ACTION_REQUIRED")]
    public async Task EveryOtherWorkOrderState_PermitsRequestAndApproval(string state)
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        await _driver.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = {state} WHERE [work_order_id] = {world.WorkOrderId}"));

        var request = await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-5));
        var approved = await _driver.ApproveAsync(Supervisor(world), request.Value);

        Assert.True(approved.Succeeded, approved.Error?.Message);
        Assert.Equal(world.At(-5), (await _driver.SessionAsync(world.SessionId)).CheckIn);
    }

    // ---------------- downstream data ----------------

    [Fact]
    public async Task Approval_NeverTouchesSlaWorkSummaryAcceptanceCostOrTheWorkOrder_AndTheServiceReportShowsTheCorrectedFacts()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        await _driver.SeedDownstreamAsync(world);
        var before = await _driver.DownstreamSnapshotAsync(world);
        Assert.Contains("4821.37", before, StringComparison.Ordinal);

        var requests = new[]
        {
            await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_IN", world.At(-5)),
            await _driver.RequestAsync(TeamLead(world), world.SessionId, "PAUSE_START", world.At(12), world.Pause1),
            await _driver.RequestAsync(TeamLead(world), world.SessionId, "RESUME", world.At(22), world.Pause1),
            await _driver.RequestAsync(TeamLead(world), world.SessionId, "CHECK_OUT", world.At(55))
        };
        foreach (var request in requests)
        {
            Assert.True((await _driver.ApproveAsync(Supervisor(world), request.Value)).Succeeded);
        }

        // SLA, Work Summary, Customer Acceptance, Cost Summary and the Work Order row (status, row version) are byte-for-byte unchanged.
        Assert.Equal(before, await _driver.DownstreamSnapshotAsync(world));

        // The Service Report is a live read model: it shows the corrected facts and still carries the cost only for the Supervisor.
        await using var scope = _host.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<IServiceReportStore>().GetAsync(Supervisor(world), world.WorkOrderId, CancellationToken.None);
        Assert.NotNull(report);
        var session = Assert.Single(Assert.Single(report.Report.Visits).Sessions);
        Assert.Equal(world.At(-5), session.CheckInAt);
        Assert.Equal(world.At(55), session.CheckOutAt);
        Assert.Equal(
            new[] { (world.At(12), (DateTime?)world.At(22)), (world.At(30), (DateTime?)world.At(40)) },
            session.Pauses.OrderBy(pause => pause.PausedAt).Select(pause => (pause.PausedAt, pause.ResumedAt)));
        Assert.Equal(world.At(55), Assert.Single(report.Report.Visits).CompletedAt);
        Assert.Equal(4821.37m, report.Cost!.TotalAmount);
    }
}
