using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;
using RepairRequest.IntegrationTests.TimeCorrection;

namespace RepairRequest.IntegrationTests.Audit;

/// <summary>
/// Time Correction events on the Work Order timeline (`docs/17` P11, owner order of 13 October 2026), driven through the real
/// <c>TimeCorrectionService</c>, <c>WorkOrderCloseService</c>, <c>WorkOrderCancelService</c> and the real
/// <see cref="AuditTimelineService"/> / store on SQL Server: the three event types, the historical before/after values of each audit event,
/// no reason and no raw JSON, events of a pending request rejected after CLOSED/CANCELLED, one appearance per event on the right
/// Work Order, and isolation across Work Orders, Sites and tenants. Reading writes no audit row.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class AuditTimelineTimeCorrectionTests : IAsyncLifetime
{
    private const string RequestReasonSentinel = "SENTINEL-REQUEST-REASON-7c1d";
    private const string DecisionReasonSentinel = "Not supported by the visit log.";

    private readonly AuthenticationTestHost _host = new();
    private readonly TimeCorrectionDriver _driver;

    public AuditTimelineTimeCorrectionTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _driver = new TimeCorrectionDriver(_host);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static readonly string[] CorrectionCodes = ["TIME_CORRECTION_REQUESTED", "TIME_CORRECTION_APPROVED", "TIME_CORRECTION_REJECTED"];

    private static DateTime Parse(object? value) =>
        DateTime.Parse((string)value!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private async Task<AuditTimelineResult> PageAsync(CurrentUser user, Guid workOrderId, int pageSize, string? cursor = null)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuditTimelineService>()
            .GetWorkOrderTimelineAsync(user, "WORK_ORDER", workOrderId, pageSize, cursor, CancellationToken.None);
    }

    /// <summary>Walks every page of the timeline and returns the items in delivered order.</summary>
    private async Task<List<AuditTimelineItemDto>> WalkAsync(CurrentUser user, Guid workOrderId, int pageSize)
    {
        var items = new List<AuditTimelineItemDto>();
        string? cursor = null;
        do
        {
            var result = await PageAsync(user, workOrderId, pageSize, cursor);
            Assert.Equal(AuditTimelineOutcome.Ok, result.Outcome);
            items.AddRange(result.Page!.Items);
            cursor = result.Page.HasMore ? result.Page.NextCursor : null;
        }
        while (cursor is not null);

        return items;
    }

    private static List<AuditTimelineItemDto> CorrectionItems(IEnumerable<AuditTimelineItemDto> items) =>
        items.Where(item => item.EntityType == WorkOrderAudit.TimeCorrectionEntityType).ToList();

    private Task<int> AuditRowsAsync(Guid tenantId) =>
        _driver.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.TenantId == tenantId));

    private async Task<CommandResult<Guid>> CloseAsync(TimeCorrectionWorld world)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkOrderCloseService>().CloseAsync(
            new CommandContext(world.Base.Supervisor, Guid.NewGuid()), world.WorkOrderId, await _driver.WorkOrderRowVersionAsync(world.WorkOrderId), CancellationToken.None);
    }

    private async Task<CommandResult<Guid>> CancelAsync(TimeCorrectionWorld world)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkOrderCancelService>().CancelAsync(
            new CommandContext(world.Base.Supervisor, Guid.NewGuid()), world.WorkOrderId, await _driver.WorkOrderRowVersionAsync(world.WorkOrderId), "Customer withdrew the order.", CancellationToken.None);
    }

    // ---------------- the three event types, history, redaction, ordering and paging ----------------

    [Fact]
    public async Task AllThreeEventTypes_AppearOnceEach_WithTheirOwnHistoricalBeforeAfter_AndNoReasonOrRawJson()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var supervisor = world.Base.Supervisor;

        // R1 then R2 both approved, so the session's current check-in is R2's value; R3 rejected; R4 left pending (a pause target).
        var r1 = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5), reason: RequestReasonSentinel);
        Assert.True((await _driver.ApproveAsync(supervisor, r1.Value)).Succeeded);
        var r2 = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-8), reason: RequestReasonSentinel);
        Assert.True((await _driver.ApproveAsync(supervisor, r2.Value)).Succeeded);
        var r3 = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_OUT", world.At(55), reason: RequestReasonSentinel);
        Assert.True((await _driver.RejectAsync(supervisor, r3.Value)).Succeeded);
        var r4 = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "PAUSE_START", world.At(12), world.Pause1, reason: RequestReasonSentinel);
        Assert.True(r4.Succeeded, r4.Error?.Message);
        var auditRowsBeforeReading = await AuditRowsAsync(world.TenantId);

        var all = await WalkAsync(supervisor, world.WorkOrderId, 100);
        var corrections = CorrectionItems(all);

        // Seven events: 4 requests, 2 approvals, 1 rejection — each exactly once, none from anywhere else.
        Assert.Equal(7, corrections.Count);
        Assert.Equal(7, corrections.Select(item => item.AuditId).Distinct().Count());
        Assert.Equal(4, corrections.Count(item => item.ActionCode == "TIME_CORRECTION_REQUESTED"));
        Assert.Equal(2, corrections.Count(item => item.ActionCode == "TIME_CORRECTION_APPROVED"));
        Assert.Equal(1, corrections.Count(item => item.ActionCode == "TIME_CORRECTION_REJECTED"));
        Assert.All(corrections, item => Assert.Contains(item.ActionCode, CorrectionCodes));
        Assert.Equal(new[] { r1.Value, r2.Value, r3.Value, r4.Value }.Order(), corrections.Select(item => item.EntityId).Distinct().Order());

        // Historical values come from each event, not from the session's current facts.
        var current = (await _driver.SessionAsync(world.SessionId)).CheckIn;
        Assert.Equal(world.At(-8), current, TimeSpan.FromMilliseconds(1));
        var approvedR1 = corrections.Single(item => item.EntityId == r1.Value && item.ActionCode == "TIME_CORRECTION_APPROVED");
        Assert.Equal(world.CheckIn, Parse(approvedR1.Details["oldValue"]), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.At(-5), Parse(approvedR1.Details["newValue"]), TimeSpan.FromMilliseconds(1));
        var approvedR2 = corrections.Single(item => item.EntityId == r2.Value && item.ActionCode == "TIME_CORRECTION_APPROVED");
        Assert.Equal(world.At(-5), Parse(approvedR2.Details["oldValue"]), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.At(-8), Parse(approvedR2.Details["newValue"]), TimeSpan.FromMilliseconds(1));
        var requestedR2 = corrections.Single(item => item.EntityId == r2.Value && item.ActionCode == "TIME_CORRECTION_REQUESTED");
        Assert.Equal(world.At(-5), Parse(requestedR2.Details["oldValue"]), TimeSpan.FromMilliseconds(1));
        var rejectedR3 = corrections.Single(item => item.ActionCode == "TIME_CORRECTION_REJECTED");
        Assert.Equal("CHECK_OUT", rejectedR3.Details["timestampType"]);
        Assert.Equal(world.At(60), Parse(rejectedR3.Details["oldValue"]), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.At(55), Parse(rejectedR3.Details["newValue"]), TimeSpan.FromMilliseconds(1));
        Assert.Equal("TIME_CORRECTION_REJECTED", rejectedR3.ActionCode);

        // Details: the approved allowlist only; the pause id appears only for the pause target.
        var allowed = new[] { "workSessionId", "workSessionPauseId", "timestampType", "oldValue", "newValue" };
        Assert.All(corrections, item =>
        {
            Assert.All(item.Details.Keys, key => Assert.Contains(key, allowed));
            Assert.Equal(world.SessionId, Guid.Parse((string)item.Details["workSessionId"]!));
        });
        Assert.All(corrections.Where(item => item.EntityId != r4.Value), item => Assert.DoesNotContain("workSessionPauseId", item.Details.Keys));
        Assert.All(corrections.Where(item => item.EntityId == r4.Value), item => Assert.Equal(world.Pause1, Guid.Parse((string)item.Details["workSessionPauseId"]!)));

        // Actor: the requester (Team Lead) and the decider (Supervisor), both existing users of the tenant.
        Assert.All(corrections.Where(item => item.ActionCode == "TIME_CORRECTION_REQUESTED"), item => Assert.Equal(world.Base.TeamLead.UserId, item.Actor.ActorId));
        Assert.All(corrections.Where(item => item.ActionCode != "TIME_CORRECTION_REQUESTED"), item => Assert.Equal(supervisor.UserId, item.Actor.ActorId));
        Assert.All(corrections, item => Assert.Equal(AuditActorKind.User, item.Actor.Kind));

        // Neither reason, nor raw JSON, nor the Visit-sync flag is anywhere in the serialized page.
        var json = JsonSerializer.Serialize(all);
        Assert.DoesNotContain(RequestReasonSentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(DecisionReasonSentinel, json, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "visitCompletedAtSynchronized", "oldValueJson", "newValueJson", "correlationId", "\"Reason\"", "decisionReason" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        // Reading is read-only.
        Assert.Equal(auditRowsBeforeReading, await AuditRowsAsync(world.TenantId));

        // Deterministic order and keyset paging: a walk with a tiny page size delivers the same items in the same order, each once.
        // (Ties on occurred_at are broken by SQL Server's own uniqueidentifier order, which differs from the CLR's; the walks below prove determinism.)
        Assert.Equal(all.OrderByDescending(item => item.OccurredAt).Select(item => item.OccurredAt), all.Select(item => item.OccurredAt));
        var small = await WalkAsync(supervisor, world.WorkOrderId, 3);
        Assert.Equal(all.Select(item => item.AuditId), small.Select(item => item.AuditId));
        Assert.Equal(all.Count, all.Select(item => item.AuditId).Distinct().Count());
    }

    [Fact]
    public async Task MixedEvents_ExistingWorkOrderVisitAndSessionEvents_AreUnchanged_AndInterleaveDeterministically()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var supervisor = world.Base.Supervisor;
        var before = await WalkAsync(supervisor, world.WorkOrderId, 100);

        var request = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        Assert.True((await _driver.ApproveAsync(supervisor, request.Value)).Succeeded);
        var after = await WalkAsync(supervisor, world.WorkOrderId, 100);

        // Every event that was on the timeline before is still there exactly as it was; the only additions are the two correction events.
        Assert.Equal(before.Count + 2, after.Count);
        Assert.Equal(before.Select(item => item.AuditId).Order(), after.Where(item => item.EntityType != WorkOrderAudit.TimeCorrectionEntityType).Select(item => item.AuditId).Order());
        Assert.Equal(before.OrderBy(item => item.AuditId), after.Where(item => item.EntityType != WorkOrderAudit.TimeCorrectionEntityType).OrderBy(item => item.AuditId));

        // Total order is occurred_at DESC, audit id DESC across entity types, at every page size.
        foreach (var pageSize in new[] { 1, 2, 5, 100 })
        {
            var walked = await WalkAsync(supervisor, world.WorkOrderId, pageSize);
            Assert.Equal(after.Select(item => item.AuditId), walked.Select(item => item.AuditId));
            Assert.Equal(walked.Count, walked.Select(item => item.AuditId).Distinct().Count());
        }
    }

    // ---------------- a rejected pending correction after CLOSED / CANCELLED stays visible ----------------

    [Theory]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public async Task ARejectedPendingCorrection_AfterTheWorkOrderIsClosedOrCancelled_StaysVisibleOnTheTimeline(string terminal)
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var supervisor = world.Base.Supervisor;
        var pending = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_OUT", world.At(55));
        var toApprove = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        var facts = await _driver.SessionAsync(world.SessionId);

        if (terminal == "CLOSED")
        {
            await _driver.MakeClosableAsync(world);
            Assert.True((await CloseAsync(world)).Succeeded);
        }
        else
        {
            Assert.True((await CancelAsync(world)).Succeeded);
        }

        // Approve is refused (nothing written); Reject is allowed and changes no time fact.
        Assert.Equal(CommandFailure.StateConflict, (await _driver.ApproveAsync(supervisor, toApprove.Value)).Error?.Failure);
        Assert.True((await _driver.RejectAsync(supervisor, pending.Value)).Succeeded);
        Assert.Equal(facts, await _driver.SessionAsync(world.SessionId));

        var items = await WalkAsync(supervisor, world.WorkOrderId, 100);
        var corrections = CorrectionItems(items);

        Assert.Single(corrections, item => item.ActionCode == "TIME_CORRECTION_REJECTED" && item.EntityId == pending.Value);
        Assert.DoesNotContain(corrections, item => item.ActionCode == "TIME_CORRECTION_APPROVED");
        Assert.Equal(2, corrections.Count(item => item.ActionCode == "TIME_CORRECTION_REQUESTED"));
        Assert.Contains(items, item => item.ActionCode == (terminal == "CLOSED" ? "WORK_ORDER_CLOSED" : "WORK_ORDER_CANCELLED"));
        Assert.Equal(items.Select(item => item.AuditId).Distinct().Count(), items.Count);

        // The terminal event is the older of the two kinds: the rejection came after it, so it is listed before it (newest first).
        var terminalIndex = items.FindIndex(item => item.ActionCode == (terminal == "CLOSED" ? "WORK_ORDER_CLOSED" : "WORK_ORDER_CANCELLED"));
        var rejectedIndex = items.FindIndex(item => item.ActionCode == "TIME_CORRECTION_REJECTED");
        Assert.True(rejectedIndex < terminalIndex);
    }

    // ---------------- isolation: Work Order, Site, tenant, ownership ----------------

    private async Task<(Guid WorkOrderId, Guid SessionId)> SecondWorkOrderAsync(TimeCorrectionWorld world)
    {
        var tenantId = world.TenantId;
        var technicianId = (await _host.CreateUserInTenantAsync(tenantId, RoleCodes.Technician)).Id;
        return await _driver.WithDbAsync(async db =>
        {
            var requestId = await ScopeWorld.AddRequestAsync(db, tenantId, world.Base.OtherRequester.UserId, world.Base.Scope.SiteA1);
            var workOrderId = await ScopeWorld.AddWorkOrderAsync(db, tenantId, requestId, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow);
            var visit = ServiceVisit.Create(
                tenantId, workOrderId, ServiceVisitType.Initial, Guid.NewGuid(), technicianId, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2));
            db.ServiceVisits.Add(visit);
            await db.SaveChangesAsync();
            var session = WorkSession.Create(tenantId, visit.Id, technicianId, DateTime.UtcNow.AddHours(-2));
            session.CheckOut(DateTime.UtcNow.AddHours(-1));
            db.WorkSessions.Add(session);
            await db.SaveChangesAsync();
            return (workOrderId, session.Id);
        });
    }

    [Fact]
    public async Task EachCorrectionEvent_AppearsOnlyOnItsOwnWorkOrder_ThroughTenantConsistentJoins()
    {
        var one = await TimeCorrectionWorlds.CreateAsync(_host);
        var supervisor = one.Base.Supervisor;
        var (secondWorkOrderId, secondSessionId) = await SecondWorkOrderAsync(one);
        var other = await TimeCorrectionWorlds.CreateAsync(_host);           // another tenant, with its own corrections

        var onFirst = await _driver.RequestAsync(one.Base.TeamLead, one.SessionId, "CHECK_IN", one.At(-5));
        Assert.True((await _driver.ApproveAsync(supervisor, onFirst.Value)).Succeeded);
        var secondSession = await _driver.WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(item => item.Id == secondSessionId));
        var onSecond = await _driver.RequestAsync(one.Base.TeamLead, secondSessionId, "CHECK_OUT", secondSession.CheckOutAt!.Value.AddMinutes(-3));
        Assert.True(onSecond.Succeeded, onSecond.Error?.Message);
        Assert.True((await _driver.ApproveAsync(supervisor, onSecond.Value)).Succeeded);
        var inOtherTenant = await _driver.RequestAsync(other.Base.TeamLead, other.SessionId, "CHECK_IN", other.At(-5));
        Assert.True((await _driver.ApproveAsync(other.Base.Supervisor, inOtherTenant.Value)).Succeeded);

        // The correction events are reached relationally (correction -> Work Session -> Visit -> Work Order, inside the tenant): each Work Order's
        // timeline holds exactly its own corrections' events.
        var firstItems = CorrectionItems(await WalkAsync(supervisor, one.WorkOrderId, 100));
        var secondItems = CorrectionItems(await WalkAsync(supervisor, secondWorkOrderId, 100));
        Assert.Equal(2, firstItems.Count);
        Assert.All(firstItems, item => Assert.Equal(onFirst.Value, item.EntityId));
        Assert.Equal(2, secondItems.Count);
        Assert.All(secondItems, item => Assert.Equal(onSecond.Value, item.EntityId));
        Assert.Empty(firstItems.Select(item => item.AuditId).Intersect(secondItems.Select(item => item.AuditId)));

        // Another tenant sees nothing of this one and cannot name its Work Order; its own events never appear here.
        Assert.Equal(AuditTimelineOutcome.NotFound, (await PageAsync(other.Base.Supervisor, one.WorkOrderId, 100)).Outcome);
        Assert.DoesNotContain(firstItems.Concat(secondItems), item => item.EntityId == inOtherTenant.Value);
        var otherItems = CorrectionItems(await WalkAsync(other.Base.Supervisor, other.WorkOrderId, 100));
        Assert.All(otherItems, item => Assert.Equal(inOtherTenant.Value, item.EntityId));
    }

    [Fact]
    public async Task ScopeIsAppliedBeforeAnyEventIsReturned_SiteOwnershipAndTenant()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var request = await _driver.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        Assert.True((await _driver.ApproveAsync(world.Base.Supervisor, request.Value)).Succeeded);

        // A Supervisor of another Site of the same tenant, a Requester who does not own the request, and a user of another tenant.
        var otherSite = await _host.CreateUserInTenantAsync(world.TenantId, RoleCodes.Supervisor);
        await _driver.WithDbAsync(async db =>
        {
            await ScopeWorld.AssignSitesAsync(db, world.TenantId, otherSite.Id, world.Base.Scope.SiteA2);
            return true;
        });
        var otherSiteSupervisor = new CurrentUser(otherSite.Id, world.TenantId, [RoleCodes.Supervisor]);

        // Out-of-Site, non-owning Requester (the designated acceptance contact did not create the request) and Technician: one identical NotFound.
        Assert.Equal(AuditTimelineOutcome.NotFound, (await PageAsync(otherSiteSupervisor, world.WorkOrderId, 100)).Outcome);
        Assert.Equal(AuditTimelineOutcome.NotFound, (await PageAsync(world.Base.AcceptanceContact, world.WorkOrderId, 100)).Outcome);
        Assert.Equal(AuditTimelineOutcome.NotFound, (await PageAsync(new CurrentUser(world.Base.Supervisor.UserId, Guid.NewGuid(), [RoleCodes.Supervisor]), world.WorkOrderId, 100)).Outcome);

        // The creator of the request owns the Work Order and sees the same correction events as the Supervisor; nothing more.
        var owner = new CurrentUser(world.Base.OtherRequester.UserId, world.TenantId, [RoleCodes.Requester]);
        var seenByOwner = CorrectionItems(await WalkAsync(owner, world.WorkOrderId, 100));
        var seenBySupervisor = CorrectionItems(await WalkAsync(world.Base.Supervisor, world.WorkOrderId, 100));
        Assert.Equal(2, seenByOwner.Count);
        Assert.Equal(seenBySupervisor.Select(item => item.AuditId), seenByOwner.Select(item => item.AuditId));
    }
}
