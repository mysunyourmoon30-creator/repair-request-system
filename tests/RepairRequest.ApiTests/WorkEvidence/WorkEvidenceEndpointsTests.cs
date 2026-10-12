using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.ApiTests.Attachments;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.ApiTests.WorkEvidence;

/// <summary>
/// Work Order evidence end to end (`docs/13` §4.28; `docs/16` D3/D4, OD1..OD3): WEV-API-001 upload, WEV-API-002 list, WEV-API-003 download
/// and the CLEAN guard of Submit Work Summary, through the real host, real storage and the real scan service. Authorization, scope, state,
/// file policy, CLEAN-only content with safe headers, and "nothing sensitive in any response".
/// </summary>
public sealed class WorkEvidenceEndpointsTests : IClassFixture<WorkEvidenceApiFactory>
{
    private readonly WorkEvidenceApiFactory _factory;
    private readonly EvidenceApiClient _api;

    public WorkEvidenceEndpointsTests(WorkEvidenceApiFactory factory)
    {
        _factory = factory;
        _api = new EvidenceApiClient(factory);
    }

    private static string Evidence(Guid workOrderId) => $"/api/v1/work-orders/{workOrderId}/evidence";

    private static string Content(Guid workOrderId, Guid evidenceId) => $"{Evidence(workOrderId)}/{evidenceId}/content";

    // ---------------- upload: success ----------------

    [Fact]
    public async Task Upload_ByTheAssignedTechnician_Returns201_WithPendingMetadata_ALocation_NoStore_AndNoStorageDetails()
    {
        var world = await _api.CreateWorldAsync();
        var bytes = EvidenceApiClient.Png("pump before");

        var response = await _api.UploadAsync(world.Technician, world.WorkOrderId, bytes, "Pump Before.png", type: "before_work");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await EvidenceApiClient.Json(response);
        var evidenceId = body.GetProperty("evidenceId").GetGuid();
        Assert.Equal(Content(world.WorkOrderId, evidenceId), response.Headers.Location?.OriginalString);
        Assert.Equal("BEFORE_WORK", body.GetProperty("evidenceType").GetString());
        Assert.Equal("Pump Before.png", body.GetProperty("fileName").GetString());
        Assert.Equal("image/png", body.GetProperty("mimeType").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("PENDING", body.GetProperty("scanStatus").GetString());
        Assert.False(body.GetProperty("bound").GetBoolean());
        Assert.Equal(world.VisitId, body.GetProperty("serviceVisitId").GetGuid());

        var names = EvidenceApiClient.PropertyNames(body).Select(name => name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, name => name.Contains("storage") || name.Contains("hash") || name.Contains("tenant") || name.Contains("uploadedby"));

        // The file is stored privately under an opaque key and audited; the metadata starts PENDING.
        var file = await _factory.WithDbAsync(db => (
            from upload in db.WorkEvidenceUploads
            join asset in db.FileAssets on upload.FileAssetId equals asset.Id
            where upload.Id == evidenceId
            select asset).AsNoTracking().SingleAsync());
        Assert.Equal(MalwareScanStatus.Pending, file.MalwareScanStatus);
        Assert.StartsWith($"{world.TenantId:N}/", file.StorageReference, StringComparison.Ordinal);
        Assert.DoesNotContain("Pump", file.StorageReference, StringComparison.Ordinal);
        Assert.Equal(world.Technician.UserId, file.UploadedBy);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == evidenceId && audit.ActionCode == "WORK_EVIDENCE_UPLOADED")));
    }

    [Fact]
    public async Task Upload_SanitizesAHostileFileName_AndNeverUsesItForStorage()
    {
        var world = await _api.CreateWorldAsync();

        var response = await _api.UploadAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png(), "../../etc/pass\u202Ewd\r\n<x>.png");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var name = (await EvidenceApiClient.Json(response)).GetProperty("fileName").GetString()!;
        Assert.Equal("passwdx.png", name);
        Assert.All(new[] { "/", "\\", "..", "\r", "\n", "<", ">", "\u202E" }, forbidden => Assert.DoesNotContain(forbidden, name, StringComparison.Ordinal));
    }

    // ---------------- upload: authorization, scope and state ----------------

    [Fact]
    public async Task Upload_IsTechnicianOnly_AndOnlyForATechnicianWithAnOwnSessionOnTheWorkOrder()
    {
        var world = await _api.CreateWorldAsync();
        var bytes = EvidenceApiClient.Png();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.UploadAsync(null, world.WorkOrderId, bytes)).StatusCode);
        foreach (var caller in new[] { world.TeamLead, world.Supervisor, world.Coordinator, world.Approver, world.Administrator, world.Contact })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _api.UploadAsync(caller, world.WorkOrderId, bytes)).StatusCode);
        }

        // A Technician who is not assigned, another tenant, and an unknown Work Order are the same non-leaking 404.
        var other = await _api.CreateWorldAsync();
        var bodies = new List<string?>();
        foreach (var response in new[]
                 {
                     await _api.UploadAsync(world.OtherTechnician, world.WorkOrderId, bytes),
                     await _api.UploadAsync(other.Technician, world.WorkOrderId, bytes),
                     await _api.UploadAsync(world.Technician, Guid.NewGuid(), bytes)
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add((await EvidenceApiClient.Json(response)).GetProperty("code").GetString());
        }

        Assert.Single(bodies.Distinct());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
    }

    [Theory]
    [InlineData("SCHEDULED")]
    [InlineData("AWAITING_SUPERVISOR_REVIEW")]
    [InlineData("COMPLETED")]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public async Task Upload_IsRefusedWhenTheWorkOrderIsNotInProgress(string status)
    {
        var world = await _api.CreateWorldAsync();
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = {status} WHERE [work_order_id] = {world.WorkOrderId}"));

        var response = await _api.UploadAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("STATE_CONFLICT", (await EvidenceApiClient.Json(response)).GetProperty("code").GetString());
    }

    // ---------------- upload: validation ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DURING_WORK")]
    [InlineData("PHOTO")]
    public async Task Upload_WithAnEvidenceTypeOutsideTheCatalog_Is422(string? type)
    {
        var world = await _api.CreateWorldAsync();

        var response = await _api.UploadAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png(), type: type);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await EvidenceApiClient.Json(response);
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("code").GetString());
        Assert.Contains("evidenceType", body.GetProperty("errors").EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.png", "image/jpeg")]
    [InlineData("a.exe", "application/octet-stream")]
    [InlineData("a.png.exe", "image/png")]
    public async Task Upload_WithAnUnsupportedOrMismatchedFile_Is422_AndStoresNothing(string name, string contentType)
    {
        var world = await _api.CreateWorldAsync();

        var response = await _api.UploadAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png(), name, contentType);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("file", (await EvidenceApiClient.Json(response)).GetProperty("errors").EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
    }

    [Fact]
    public async Task Upload_WhoseContentIsNotTheDeclaredType_OrIsEmptyOrTooLarge_Is422()
    {
        var world = await _api.CreateWorldAsync();

        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await _api.UploadAsync(world.Technician, world.WorkOrderId, "GIF89a-not-a-png"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _api.UploadAsync(world.Technician, world.WorkOrderId, [])).StatusCode);

        var tooLarge = EvidenceApiClient.Png();
        Array.Resize(ref tooLarge, (10 * 1024 * 1024) + 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _api.UploadAsync(world.Technician, world.WorkOrderId, tooLarge)).StatusCode);

        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
    }

    [Fact]
    public async Task Upload_WithoutAFilePart_Is400()
    {
        var world = await _api.CreateWorldAsync();
        using var form = new MultipartFormDataContent { { new StringContent("OTHER"), "evidenceType" } };

        var response = await _api.SendAsync(HttpMethod.Post, Evidence(world.WorkOrderId), world.Technician, form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_HasNoPerRoundCount_SoRepeatedFailedOrUnselectedUploadsNeverBlockTheVisit()
    {
        var world = await _api.CreateWorldAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 34; i++)
        {
            ids.Add(await _api.UploadOkAsync(world.Technician, world.WorkOrderId)); // every upload is accepted: well past ten, well past thirty
        }

        foreach (var id in ids.Take(33))
        {
            await _api.SetScanStatusAsync(id, "FAILED");
        }

        // The Visit is still usable: another upload, a CLEAN scan and a Submit of the one CLEAN item all work.
        var last = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);
        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [last])).StatusCode);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
    }

    // ---------------- the guard through the API ----------------

    [Fact]
    public async Task SubmitWorkSummary_RequiresAtLeastOneCleanItem_PendingAndFailedAndMalwareDoNotCount()
    {
        var world = await _api.CreateWorldAsync();

        var none = await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Contains("evidenceIds", (await EvidenceApiClient.Json(none)).GetProperty("errors").EnumerateObject().Select(property => property.Name));

        var pending = await _api.UploadOkAsync(world.Technician, world.WorkOrderId);
        var infected = await _api.UploadOkAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png(TestMalwareScanner.InfectedMarker));
        Assert.Equal(FileScanResult.Failed, await _api.ScanAsync(infected));
        var failed = await _api.UploadOkAsync(world.Technician, world.WorkOrderId);
        await _api.SetScanStatusAsync(failed, "FAILED");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [pending, infected, failed])).StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(summary => summary.WorkOrderId == world.WorkOrderId)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.Id == pending || item.Id == infected || item.Id == failed)));

        // One CLEAN item (any type, including OTHER) is enough; the others stay unbound.
        var clean = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId, "OTHER");
        var submitted = await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [clean]);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var bound = await _factory.WithDbAsync(db => db.WorkSummaryEvidence.AsNoTracking().Select(item => item.Id).ToListAsync());
        Assert.Contains(clean, bound);
        Assert.DoesNotContain(pending, bound);
        Assert.DoesNotContain(infected, bound);
        Assert.DoesNotContain(failed, bound);

        // The round is closed: no further upload, and the set cannot change.
        Assert.Equal(HttpStatusCode.Conflict, (await _api.UploadAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png(Guid.NewGuid().ToString()))).StatusCode);
    }

    // ---------------- the evidenceIds contract of Submit/Resubmit ----------------

    private static string[] EvidenceErrors(JsonElement problem) =>
        problem.GetProperty("errors").GetProperty("evidenceIds").EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Fact]
    public async Task SubmitWorkSummary_RequiresEvidenceIds_AMissingNullOrEmptyPropertyIs422OnEvidenceIds_EvenWithCleanUploadsWaiting()
    {
        var world = await _api.CreateWorldAsync();
        await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);

        foreach (var body in new object[]
                 {
                     new { summaryText = "Done.", repairOutcomeCode = "REPAIRED" },                       // property missing
                     new { summaryText = "Done.", repairOutcomeCode = "REPAIRED", evidenceIds = (Guid[]?)null },
                     new { summaryText = "Done.", repairOutcomeCode = "REPAIRED", evidenceIds = Array.Empty<Guid>() }
                 })
        {
            var response = await _api.SubmitRawAsync(world.Technician, world.WorkOrderId, body, await _api.ETagAsync(world.WorkOrderId));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await EvidenceApiClient.Json(response);
            Assert.Equal("VALIDATION_FAILED", problem.GetProperty("code").GetString());
            Assert.Single(EvidenceErrors(problem));
        }

        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(summary => summary.WorkOrderId == world.WorkOrderId)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
    }

    [Fact]
    public async Task SubmitWorkSummary_BindsExactlyTheSelectedIds_AnUnselectedCleanUploadIsNotBound_AndSlotsFollowTheRequestOrder()
    {
        var world = await _api.CreateWorldAsync();
        var first = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId, "BEFORE_WORK");
        var second = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId, "AFTER_WORK");
        var third = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId, "OTHER");
        var unselected = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);

        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [third, first, second])).StatusCode);

        var bound = await _factory.WithDbAsync(db => db.WorkSummaryEvidence.AsNoTracking().Where(item => item.WorkOrderId == world.WorkOrderId).OrderBy(item => item.SlotNo).ToListAsync());
        Assert.Equal([third, first, second], bound.Select(item => item.Id));
        Assert.Equal([1, 2, 3], bound.Select(item => item.SlotNo));
        Assert.DoesNotContain(bound, item => item.Id == unselected);

        // The leftover upload is listed as not bound, and the round is closed to further uploads.
        var items = (await EvidenceApiClient.Json(await _api.GetAsync(Evidence(world.WorkOrderId), world.Technician))).GetProperty("items").EnumerateArray().ToList();
        Assert.False(items.Single(item => item.GetProperty("evidenceId").GetGuid() == unselected).GetProperty("bound").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await _api.UploadAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png(Guid.NewGuid().ToString()))).StatusCode);
    }

    [Fact]
    public async Task SubmitWorkSummary_AcceptsOneAndTenSelectedItems_AndRejectsElevenAndDuplicates_WithoutBindingAnything()
    {
        var oneWorld = await _api.CreateWorldAsync();
        var one = await _api.CleanEvidenceAsync(oneWorld.Technician, oneWorld.WorkOrderId);
        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(oneWorld.Technician, oneWorld.WorkOrderId, [one])).StatusCode);

        var world = await _api.CreateWorldAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            ids.Add(await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId)); // eleven CLEAN uploads are fine; only the bound set is limited
        }

        var eleven = await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, ids);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, eleven.StatusCode);
        Assert.Contains("10", Assert.Single(EvidenceErrors(await EvidenceApiClient.Json(eleven))), StringComparison.Ordinal);

        var duplicate = await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [ids[0], ids[1], ids[0]]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Contains("once", Assert.Single(EvidenceErrors(await EvidenceApiClient.Json(duplicate))), StringComparison.Ordinal);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(summary => summary.WorkOrderId == world.WorkOrderId)));

        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, ids.Take(10))).StatusCode);
        var bound = await _factory.WithDbAsync(db => db.WorkSummaryEvidence.AsNoTracking().Where(item => item.WorkOrderId == world.WorkOrderId).OrderBy(item => item.SlotNo).ToListAsync());
        Assert.Equal(Enumerable.Range(1, 10), bound.Select(item => item.SlotNo));
        Assert.DoesNotContain(bound, item => item.Id == ids[10]);
    }

    [Fact]
    public async Task SubmitWorkSummary_AnUnusableSelectedId_GetsOneIdenticalNonDisclosingAnswer_WhateverTheReason()
    {
        var world = await _api.CreateWorldAsync();
        var clean = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);
        var pending = await _api.UploadOkAsync(world.Technician, world.WorkOrderId);
        var failed = await _api.UploadOkAsync(world.Technician, world.WorkOrderId);
        await _api.SetScanStatusAsync(failed, "FAILED");
        var infected = await _api.UploadOkAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png(TestMalwareScanner.InfectedMarker));
        Assert.Equal(FileScanResult.Failed, await _api.ScanAsync(infected));

        // Evidence that exists, but belongs to another tenant's Work Order and Visit (cross-Work-Order of one tenant: see the SQL Server tests).
        var foreign = await _api.CreateWorldAsync();
        var foreignEvidence = await _api.CleanEvidenceAsync(foreign.Technician, foreign.WorkOrderId);
        var foreignSecond = await _factory.CreateCleanWorkEvidenceForCurrentVisitAsync(foreign.WorkOrderId, foreign.Technician.UserId);

        var answers = new List<string>();
        foreach (var probe in new[] { pending, failed, infected, Guid.NewGuid(), foreignEvidence, foreignSecond[0] })
        {
            var response = await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [clean, probe]);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await EvidenceApiClient.Json(response);
            answers.Add(Assert.Single(EvidenceErrors(problem)));
            Assert.Equal(["evidenceIds"], problem.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        }

        // The same words for every reason: nothing says PENDING, FAILED, malware, tenant, Work Order or "unknown".
        Assert.Single(answers.Distinct());
        Assert.All(new[] { "PENDING", "FAILED", "malware", "tenant", "Work Order", "unknown", "not found" }, forbidden =>
            Assert.DoesNotContain(forbidden, answers[0], StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(summary => summary.WorkOrderId == world.WorkOrderId)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkOrderId == world.WorkOrderId || item.WorkOrderId == foreign.WorkOrderId)));
    }

    [Fact]
    public async Task SubmitWorkSummary_WithEvidenceOfAnotherWorkOrderOfTheSameTenant_IsTheSameGeneric422_AsUnknownAndUnusableIds_AndChangesNothing()
    {
        // Work Order A and Work Order B belong to the same tenant and Site, and the same Technician works on both.
        var a = await _api.CreateWorldAsync();
        var (workOrderB, visitB) = await _api.AddWorkOrderInSameTenantAsync(a);
        var evidenceOfA = await _api.CleanEvidenceAsync(a.Technician, a.WorkOrderId, "BEFORE_WORK");

        // B's own evidence: one CLEAN item, one PENDING item (an existing but unusable id of B itself).
        var cleanOfB = await _factory.CreateCleanWorkEvidenceForCurrentVisitAsync(workOrderB, a.Technician.UserId);
        var pendingOfB = await _api.UploadOkAsync(a.Technician, workOrderB);

        var uploadA = await _factory.WithDbAsync(db => db.WorkEvidenceUploads.AsNoTracking().SingleAsync(item => item.Id == evidenceOfA));
        var fileA = await _factory.WithDbAsync(db => db.FileAssets.AsNoTracking().SingleAsync(item => item.Id == uploadA.FileAssetId));
        var workOrderRowVersionB = await _api.ETagAsync(workOrderB);
        var workOrderRowVersionA = await _api.ETagAsync(a.WorkOrderId);

        // Submit of B naming A's evidence — alone, and mixed with B's own CLEAN item — versus an unknown id and B's own PENDING id.
        var probes = new Dictionary<string, Guid[]>
        {
            ["A's evidence only"] = [evidenceOfA],
            ["A's evidence with B's CLEAN item"] = [cleanOfB[0], evidenceOfA],
            ["unknown id"] = [Guid.NewGuid()],
            ["B's own PENDING id"] = [pendingOfB]
        };

        var bodies = new Dictionary<string, string>();
        foreach (var (name, ids) in probes)
        {
            var response = await _api.SubmitSummaryAsync(a.Technician, workOrderB, ids);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await EvidenceApiClient.Json(response);
            Assert.Equal("VALIDATION_FAILED", problem.GetProperty("code").GetString());
            Assert.Equal(["evidenceIds"], problem.GetProperty("errors").EnumerateObject().Select(property => property.Name));
            var message = Assert.Single(EvidenceErrors(problem));
            // The message never reveals that the evidence exists, or that it belongs to another Work Order.
            Assert.All(new[] { "exist", "belong", "another", "other", "Work Order", "tenant", "found", "unknown" }, word =>
                Assert.DoesNotContain(word, message, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(a.WorkOrderId.ToString(), await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            bodies[name] = message;
        }

        // One identical answer for every unusable id: the cross-Work-Order id is indistinguishable from an unknown id and from an existing unusable id.
        Assert.Single(bodies.Values.Distinct());

        // Nothing happened to B: no Work Summary, no snapshot row, same status and row version.
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(item => item.WorkOrderId == workOrderB)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkOrderId == workOrderB)));
        Assert.Equal(
            WorkOrderStatus.InProgress,
            await _factory.WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == workOrderB).Select(item => item.Status).SingleAsync()));
        Assert.Equal(workOrderRowVersionB, await _api.ETagAsync(workOrderB));

        // A's evidence is untouched: not bound, same upload and file rows, still CLEAN, and A itself is unchanged.
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.Id == evidenceOfA)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(item => item.WorkOrderId == a.WorkOrderId)));
        var uploadAfter = await _factory.WithDbAsync(db => db.WorkEvidenceUploads.AsNoTracking().SingleAsync(item => item.Id == evidenceOfA));
        var fileAfter = await _factory.WithDbAsync(db => db.FileAssets.AsNoTracking().SingleAsync(item => item.Id == uploadA.FileAssetId));
        Assert.Equal((uploadA.WorkOrderId, uploadA.ServiceVisitId, uploadA.FileAssetId, uploadA.EvidenceType, uploadA.CreatedAt),
            (uploadAfter.WorkOrderId, uploadAfter.ServiceVisitId, uploadAfter.FileAssetId, uploadAfter.EvidenceType, uploadAfter.CreatedAt));
        Assert.Equal((fileA.MalwareScanStatus, fileA.StorageReference, fileA.ContentHash), (fileAfter.MalwareScanStatus, fileAfter.StorageReference, fileAfter.ContentHash));
        Assert.Equal(MalwareScanStatus.Clean, fileAfter.MalwareScanStatus);
        Assert.Equal(workOrderRowVersionA, await _api.ETagAsync(a.WorkOrderId));
        Assert.Equal(a.VisitId, uploadAfter.ServiceVisitId);
        Assert.NotEqual(visitB, uploadAfter.ServiceVisitId);

        // The same evidence still works for its own Work Order: A's Submit binds it normally.
        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(a.Technician, a.WorkOrderId, [evidenceOfA])).StatusCode);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkOrderId == a.WorkOrderId && item.Id == evidenceOfA)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkOrderId == workOrderB)));
    }

    [Fact]
    public async Task SubmitWorkSummary_WithAnAlreadyBoundSelectedItem_Is409StateConflict()
    {
        var world = await _api.CreateWorldAsync();
        var item = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);
        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [item])).StatusCode);
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = 'IN_PROGRESS' WHERE [work_order_id] = {world.WorkOrderId}"));

        var response = await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [item]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("STATE_CONFLICT", (await EvidenceApiClient.Json(response)).GetProperty("code").GetString());
        Assert.Equal(1, await _factory.WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(row => row.WorkOrderId == world.WorkOrderId)));
    }

    [Fact]
    public async Task SubmitWorkSummary_IgnoresEveryTamperedField_BecauseTheContractHasNoSuchMember()
    {
        var world = await _api.CreateWorldAsync();
        var item = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);
        var hostile = Guid.NewGuid();

        var response = await _api.SubmitRawAsync(
            world.Technician,
            world.WorkOrderId,
            new
            {
                summaryText = "Done.",
                repairOutcomeCode = "REPAIRED",
                evidenceIds = new[] { item },
                tenantId = hostile,
                workOrderId = hostile,
                serviceVisitId = hostile,
                workSummaryId = hostile,
                fileAssetId = hostile,
                scanStatus = "PENDING",
                slotNo = 9,
                uploadedBy = hostile,
                ownerId = hostile,
                boundAt = "2001-01-01T00:00:00Z"
            },
            await _api.ETagAsync(world.WorkOrderId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bound = await _factory.WithDbAsync(db => db.WorkSummaryEvidence.AsNoTracking().SingleAsync(row => row.WorkOrderId == world.WorkOrderId));
        var summary = await _factory.WithDbAsync(db => db.WorkSummaries.AsNoTracking().SingleAsync(row => row.WorkOrderId == world.WorkOrderId));
        Assert.Equal(item, bound.Id);
        Assert.Equal(world.TenantId, bound.TenantId);
        Assert.Equal(world.VisitId, bound.ServiceVisitId);
        Assert.Equal(summary.Id, bound.WorkSummaryId);
        Assert.Equal(1, bound.SlotNo);
        Assert.True(bound.BoundAt > DateTime.UtcNow.AddMinutes(-5));

        // The request contract itself carries exactly the three documented members.
        Assert.Equal(
            new[] { "EvidenceIds", "RepairOutcomeCode", "SummaryText" },
            typeof(SubmitWorkSummaryRequest).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    // ---------------- list ----------------

    [Fact]
    public async Task List_ShowsEachAudienceOnlyWhatItMaySee_AndNothingSensitive()
    {
        var world = await _api.CreateWorldAsync();
        var pending = await _api.UploadOkAsync(world.Technician, world.WorkOrderId);
        var bound = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId, "BEFORE_WORK");
        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [bound])).StatusCode);

        // Technician and Team Lead / Supervisor: all metadata, including the leftover PENDING candidate.
        foreach (var caller in new[] { world.Technician, world.TeamLead, world.Supervisor })
        {
            var response = await _api.GetAsync(Evidence(world.WorkOrderId), caller);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var body = await EvidenceApiClient.Json(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            var pendingItem = items.Single(item => item.GetProperty("evidenceId").GetGuid() == pending);
            Assert.Equal("PENDING", pendingItem.GetProperty("scanStatus").GetString());
            Assert.False(pendingItem.GetProperty("bound").GetBoolean());
            var boundItem = items.Single(item => item.GetProperty("evidenceId").GetGuid() == bound);
            Assert.True(boundItem.GetProperty("bound").GetBoolean());
            Assert.Equal(1, boundItem.GetProperty("slotNo").GetInt32());
            Assert.Equal("CLEAN", boundItem.GetProperty("scanStatus").GetString());

            var names = EvidenceApiClient.PropertyNames(body).Select(name => name.ToLowerInvariant()).ToList();
            Assert.DoesNotContain(names, name => name.Contains("storage") || name.Contains("hash") || name.Contains("tenant") || name.Contains("uploadedby"));
        }

        // The exact Acceptance Contact: only what the submitted summary froze, and only while CLEAN.
        var contactItems = (await EvidenceApiClient.Json(await _api.GetAsync(Evidence(world.WorkOrderId), world.Contact))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(bound, Assert.Single(contactItems).GetProperty("evidenceId").GetGuid());
    }

    [Fact]
    public async Task List_RefusesEveryoneElse_WithTheMatchingNonLeakingStatus()
    {
        var world = await _api.CreateWorldAsync();
        await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);
        var other = await _api.CreateWorldAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync(Evidence(world.WorkOrderId), null)).StatusCode);
        foreach (var caller in new[] { world.Coordinator, world.Approver, world.Administrator })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync(Evidence(world.WorkOrderId), caller)).StatusCode);
        }

        // Not the exact contact / not assigned / out of Site / other tenant / unknown Work Order: one identical 404.
        var codes = new List<string?>();
        foreach (var caller in new[] { world.OtherRequester, world.OtherTechnician, world.OutOfSiteTeamLead, world.OtherTenantSupervisor, other.Technician })
        {
            var response = await _api.GetAsync(Evidence(world.WorkOrderId), caller);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            codes.Add((await EvidenceApiClient.Json(response)).GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync(Evidence(Guid.NewGuid()), world.Supervisor)).StatusCode);
        Assert.Single(codes.Distinct());
    }

    // ---------------- download ----------------

    [Fact]
    public async Task Download_OfBoundCleanEvidence_StreamsTheBytesWithSafeHeaders_ToTeamLeadSupervisorContactAndTechnician()
    {
        var world = await _api.CreateWorldAsync();
        var bytes = EvidenceApiClient.Png("the real evidence");
        var upload = await _api.UploadAsync(world.Technician, world.WorkOrderId, bytes, "Pump After.png");
        var evidenceId = (await EvidenceApiClient.Json(upload)).GetProperty("evidenceId").GetGuid();
        Assert.Equal(FileScanResult.Clean, await _api.ScanAsync(evidenceId));
        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [evidenceId])).StatusCode);

        foreach (var caller in new[] { world.TeamLead, world.Supervisor, world.Contact, world.Technician })
        {
            var response = await _api.GetAsync(Content(world.WorkOrderId, evidenceId), caller);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains("Pump After.png", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        }

        Assert.Equal(4, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == evidenceId && audit.ActionCode == "WORK_EVIDENCE_DOWNLOADED")));
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("FAILED")]
    public async Task Download_OfANonCleanFile_Is409_WithoutLeakingContentOrStorage_ForEveryAudience(string status)
    {
        var world = await _api.CreateWorldAsync();
        var evidenceId = await _api.UploadOkAsync(world.Technician, world.WorkOrderId, EvidenceApiClient.Png("SECRET-CONTENT"));
        await _api.SetScanStatusAsync(evidenceId, status);

        var response = await _api.GetAsync(Content(world.WorkOrderId, evidenceId), world.Technician);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-CONTENT", text, StringComparison.Ordinal);
        Assert.DoesNotContain(world.TenantId.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        // And a Team Lead / Supervisor / Contact cannot reach an unbound candidate at all.
        foreach (var caller in new[] { world.TeamLead, world.Supervisor, world.Contact })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync(Content(world.WorkOrderId, evidenceId), caller)).StatusCode);
        }
    }

    [Fact]
    public async Task Download_OfAnUnboundCleanCandidate_IsOnlyForTheAssignedTechnician()
    {
        var world = await _api.CreateWorldAsync();
        var evidenceId = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync(Content(world.WorkOrderId, evidenceId), world.Technician)).StatusCode);
        foreach (var caller in new[] { world.TeamLead, world.Supervisor, world.Contact, world.OtherRequester, world.OtherTechnician, world.OutOfSiteTeamLead, world.OtherTenantSupervisor })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync(Content(world.WorkOrderId, evidenceId), caller)).StatusCode);
        }

        foreach (var caller in new[] { world.Coordinator, world.Approver, world.Administrator })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync(Content(world.WorkOrderId, evidenceId), caller)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync(Content(world.WorkOrderId, evidenceId), null)).StatusCode);
    }

    [Fact]
    public async Task Download_WithTheWrongWorkOrder_OrAnUnknownEvidenceId_Is404()
    {
        var world = await _api.CreateWorldAsync();
        var other = await _api.CreateWorldAsync();
        var evidenceId = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);

        // The evidence of one Work Order is not reachable through another Work Order, even by that Work Order's own staff.
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync(Content(other.WorkOrderId, evidenceId), other.Technician)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync(Content(world.WorkOrderId, Guid.NewGuid()), world.Technician)).StatusCode);
    }

    [Fact]
    public async Task Download_NeverReflectsAnUnsafeStoredFileNameIntoTheHeaders()
    {
        var world = await _api.CreateWorldAsync();
        var evidenceId = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [file_asset] SET [file_name] = N'na"me é;X-Injected: 1.png'
            WHERE [file_asset_id] = (SELECT [file_asset_id] FROM [work_evidence_upload] WHERE [work_evidence_id] = {evidenceId})
            """));

        var response = await _api.GetAsync(Content(world.WorkOrderId, evidenceId), world.Technician);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Injected"));
        var disposition = string.Join(";", response.Content.Headers.GetValues("Content-Disposition"));
        Assert.DoesNotContain("\r", disposition, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", disposition, StringComparison.Ordinal);
        Assert.StartsWith("attachment", disposition, StringComparison.Ordinal);
    }

    // ---------------- verbs ----------------

    [Fact]
    public async Task OnlyGetAndPostExist_NoDeleteReplaceRescanOrWriteVerb()
    {
        var world = await _api.CreateWorldAsync();
        var evidenceId = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId);

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _api.SendAsync(method, Evidence(world.WorkOrderId), world.Technician)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _api.SendAsync(method, Content(world.WorkOrderId, evidenceId), world.Technician)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _api.SendAsync(HttpMethod.Post, Content(world.WorkOrderId, evidenceId), world.Technician)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.SendAsync(HttpMethod.Delete, $"{Evidence(world.WorkOrderId)}/{evidenceId}", world.Technician)).StatusCode);
    }

    // ---------------- existing behavior does not regress ----------------

    [Fact]
    public async Task ExistingWorkOrderPermissions_AreUnchanged_ByEvidenceAndTheServiceReport()
    {
        var world = await _api.CreateWorldAsync();
        var detail = $"/api/v1/work-orders/{world.WorkOrderId}";

        // Coordinator keeps its Work Order read; the Technician still has no Work Order detail read.
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync(detail, world.Coordinator)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync(detail, world.Supervisor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync(detail, world.Technician)).StatusCode);

        // The Work Summary read keeps Technician / Team Lead / Supervisor only; the Cost Summary read keeps Team Lead / Supervisor only.
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync($"{detail}/work-summary", world.Coordinator)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await _api.GetAsync($"{detail}/work-summary", world.Technician)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync($"{detail}/cost-summary", world.Technician)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync($"{detail}/cost-summary", world.Coordinator)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await _api.GetAsync($"{detail}/cost-summary", world.Supervisor)).StatusCode);
    }
}
