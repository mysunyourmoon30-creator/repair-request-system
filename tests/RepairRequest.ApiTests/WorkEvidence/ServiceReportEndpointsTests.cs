using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.ApiTests.WorkEvidence;

/// <summary>API host with its own disposable LocalDB database for the Service Report endpoint tests.</summary>
public sealed class ServiceReportApiFactory : WorkEvidenceApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_ServiceReportApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// SVR-API-001 Service Report end to end (`docs/13` §4.28; `docs/16` D1, OD4/OD5): the access matrix, the read-only shape, and — above all — that
/// the Cost Summary is <b>structurally absent</b> from the serialized JSON of the Technician and the Acceptance Contact: no cost property name,
/// no cost value, no flag or metadata. The Technician-or-contact response is a different type from the Team Lead / Supervisor one.
/// </summary>
public sealed class ServiceReportEndpointsTests : IClassFixture<ServiceReportApiFactory>
{
    // Sentinels planted in every cost field: none may ever appear in a response for an audience without cost access.
    private const string CostNote = "COST-NOTE-SENTINEL-7f3a";
    private const decimal CostTotal = 4821.37m;
    private const string CostCurrency = "THB";

    private static readonly string[] ForbiddenCostNames =
    [
        "cost", "costsummary", "cost_summary", "totalamount", "total", "amount", "currency", "currencycode", "note", "preparedby", "preparedat",
        "reviewedby", "reviewedat", "hascost", "costavailable", "coststatus", "costreviewed", "pricing", "price"
    ];

    private readonly ServiceReportApiFactory _factory;
    private readonly EvidenceApiClient _api;

    public ServiceReportEndpointsTests(ServiceReportApiFactory factory)
    {
        _factory = factory;
        _api = new EvidenceApiClient(factory);
    }

    private static string Report(Guid workOrderId) => $"/api/v1/work-orders/{workOrderId}/service-report";

    /// <summary>A Work Order whose Work Summary was submitted through the API with two bound CLEAN items, plus acceptance rounds and a reviewed Cost Summary.</summary>
    private async Task<(EvidenceApiWorld World, Guid BoundBefore, Guid BoundAfter, Guid Leftover)> FullWorldAsync()
    {
        var world = await _api.CreateWorldAsync();
        var before = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId, "BEFORE_WORK");
        var after = await _api.CleanEvidenceAsync(world.Technician, world.WorkOrderId, "AFTER_WORK");
        var leftover = await _api.UploadOkAsync(world.Technician, world.WorkOrderId);
        Assert.Equal(HttpStatusCode.OK, (await _api.SubmitSummaryAsync(world.Technician, world.WorkOrderId, [before, after], "Replaced the worn bearing and re-aligned the shaft.")).StatusCode);

        await _factory.WithDbAsync(async db =>
        {
            db.CustomerAcceptances.Add(CustomerAcceptance.Reject(world.TenantId, world.WorkOrderId, 1, world.Contact.UserId, "Still noisy.", DateTime.UtcNow.AddMinutes(-20)));
            db.CustomerAcceptances.Add(CustomerAcceptance.Accept(world.TenantId, world.WorkOrderId, 2, world.Contact.UserId, DateTime.UtcNow.AddMinutes(-10)));
            var cost = CostSummary.Create(world.TenantId, world.WorkOrderId, CostTotal, CostCurrency, CostNote, world.TeamLead.UserId, DateTime.UtcNow.AddMinutes(-8));
            cost.Review(world.Supervisor.UserId, DateTime.UtcNow.AddMinutes(-5));
            db.CostSummaries.Add(cost);
            await db.SaveChangesAsync();
            return 0;
        });

        return (world, before, after, leftover);
    }

    // ---------------- access matrix ----------------

    [Fact]
    public async Task AccessMatrix_GrantsTechnicianTeamLeadSupervisorAndTheExactContact_AndNoOneElse()
    {
        var (world, _, _, _) = await FullWorldAsync();
        var other = await _api.CreateWorldAsync();

        foreach (var caller in new[] { world.Technician, world.TeamLead, world.Supervisor, world.Contact })
        {
            var response = await _api.GetAsync(Report(world.WorkOrderId), caller);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        // Role gate: Coordinator, Approver and Administrator are not granted by OD5 (403); anonymous is 401.
        foreach (var caller in new[] { world.Coordinator, world.Approver, world.Administrator })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync(Report(world.WorkOrderId), caller)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync(Report(world.WorkOrderId), null)).StatusCode);

        // Scope gate: a Requester who is not the exact contact, an unassigned Technician, a Team Lead of another Site, another tenant's
        // Supervisor and a missing Work Order are one identical non-leaking 404.
        var codes = new List<string?>();
        foreach (var caller in new[] { world.OtherRequester, world.OtherTechnician, world.OutOfSiteTeamLead, world.OtherTenantSupervisor, other.Supervisor })
        {
            var response = await _api.GetAsync(Report(world.WorkOrderId), caller);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            codes.Add((await EvidenceApiClient.Json(response)).GetProperty("code").GetString());
        }

        var missing = await _api.GetAsync(Report(Guid.NewGuid()), world.Supervisor);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        codes.Add((await EvidenceApiClient.Json(missing)).GetProperty("code").GetString());
        Assert.Single(codes.Distinct());
    }

    [Fact]
    public async Task OnlyGetExists_TheServiceReportHasNoWritePath()
    {
        var world = await _api.CreateWorldAsync();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _api.SendAsync(method, Report(world.WorkOrderId), world.Supervisor)).StatusCode);
        }
    }

    // ---------------- structural cost redaction ----------------

    [Fact]
    public async Task ForTheTechnicianAndTheAcceptanceContact_TheCostSummaryIsStructurallyAbsent_FromTheSerializedJson()
    {
        var (world, _, _, _) = await FullWorldAsync();

        var texts = new List<string>();
        var nameSets = new List<List<string>>();
        foreach (var caller in new[] { world.Technician, world.Contact })
        {
            var response = await _api.GetAsync(Report(world.WorkOrderId), caller);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            texts.Add(text);

            var names = EvidenceApiClient.PropertyNames(JsonDocument.Parse(text).RootElement).Select(name => name.ToLowerInvariant()).ToList();
            nameSets.Add(names);

            // No forbidden property name anywhere in the document (exact and substring), at any depth.
            foreach (var forbidden in ForbiddenCostNames)
            {
                Assert.DoesNotContain(names, name => name == forbidden);
            }

            Assert.DoesNotContain(names, name => name.Contains("cost") || name.Contains("amount") || name.Contains("currency") || name.Contains("price"));

            // No forbidden value anywhere in the raw text (amount, currency, note sentinel, the preparer / reviewer ids).
            Assert.DoesNotContain("4821.37", text, StringComparison.Ordinal);
            Assert.DoesNotContain(CostCurrency, text, StringComparison.Ordinal);
            Assert.DoesNotContain(CostNote, text, StringComparison.Ordinal);
            Assert.DoesNotContain(world.TeamLead.UserId.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.Supervisor.UserId.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"cost", text, StringComparison.OrdinalIgnoreCase);
        }

        // Both audiences get exactly the same cost-free shape.
        Assert.Equal(nameSets[0].Distinct().Order(), nameSets[1].Distinct().Order());
    }

    [Fact]
    public async Task ForTheTeamLeadAndTheSupervisor_TheFullReportIncludesTheCostSummary()
    {
        var (world, _, _, _) = await FullWorldAsync();
        var technician = await EvidenceApiClient.Json(await _api.GetAsync(Report(world.WorkOrderId), world.Technician));

        foreach (var caller in new[] { world.TeamLead, world.Supervisor })
        {
            var full = await EvidenceApiClient.Json(await _api.GetAsync(Report(world.WorkOrderId), caller));

            var cost = full.GetProperty("costSummary");
            Assert.Equal(CostTotal, cost.GetProperty("totalAmount").GetDecimal());
            Assert.Equal(CostCurrency, cost.GetProperty("currencyCode").GetString());
            Assert.Equal(CostNote, cost.GetProperty("note").GetString());
            Assert.Equal(world.TeamLead.UserId, cost.GetProperty("preparedBy").GetGuid());
            Assert.Equal(world.Supervisor.UserId, cost.GetProperty("reviewedBy").GetGuid());
            Assert.False(cost.GetProperty("reviewedAt").ValueKind == JsonValueKind.Null);

            // Everything except the cost section is identical to the operational report.
            var fullWithoutCost = EvidenceApiClient.PropertyNames(full).Where(name => !ForbiddenCostNames.Contains(name.ToLowerInvariant())).Order().ToList();
            Assert.Equal(EvidenceApiClient.PropertyNames(technician).Order(), fullWithoutCost);
            Assert.Equal(technician.GetProperty("workOrder").GetRawText(), full.GetProperty("workOrder").GetRawText());
            Assert.Equal(technician.GetProperty("workSummaries").GetRawText(), full.GetProperty("workSummaries").GetRawText());
        }
    }

    [Fact]
    public async Task AFullReportWithoutAPreparedCostSummary_CarriesANullCostSection_OnlyForTeamLeadAndSupervisor()
    {
        var world = await _api.CreateWorldAsync();

        var supervisor = await EvidenceApiClient.Json(await _api.GetAsync(Report(world.WorkOrderId), world.Supervisor));
        var technician = await EvidenceApiClient.Json(await _api.GetAsync(Report(world.WorkOrderId), world.Technician));

        Assert.Equal(JsonValueKind.Null, supervisor.GetProperty("costSummary").ValueKind);
        Assert.False(technician.TryGetProperty("costSummary", out _));
    }

    // ---------------- content ----------------

    [Fact]
    public async Task TheReport_ComposesTheExistingFacts_AndReportsOnlyTheBoundCleanEvidence()
    {
        var (world, before, after, leftover) = await FullWorldAsync();

        var body = await EvidenceApiClient.Json(await _api.GetAsync(Report(world.WorkOrderId), world.Supervisor));

        var header = body.GetProperty("workOrder");
        Assert.Equal(world.WorkOrderId, header.GetProperty("workOrderId").GetGuid());
        Assert.Equal("AWAITING_SUPERVISOR_REVIEW", header.GetProperty("status").GetString());
        Assert.Equal("SITE-A", header.GetProperty("siteCode").GetString());

        var visit = Assert.Single(body.GetProperty("visits").EnumerateArray());
        Assert.Equal(world.VisitId, visit.GetProperty("serviceVisitId").GetGuid());
        Assert.Equal("INITIAL", visit.GetProperty("visitType").GetString());
        var session = Assert.Single(visit.GetProperty("sessions").EnumerateArray());
        Assert.Equal("CHECKED_OUT", session.GetProperty("status").GetString());
        Assert.False(session.GetProperty("checkOutAt").ValueKind == JsonValueKind.Null);

        var summary = Assert.Single(body.GetProperty("workSummaries").EnumerateArray());
        Assert.Equal("Replaced the worn bearing and re-aligned the shaft.", summary.GetProperty("summaryText").GetString());
        Assert.Equal("REPAIRED", summary.GetProperty("repairOutcomeCode").GetString());
        Assert.Equal(world.VisitId, summary.GetProperty("serviceVisitId").GetGuid());

        var evidence = summary.GetProperty("evidence").EnumerateArray().ToList();
        Assert.Equal(new[] { before, after }, evidence.Select(item => item.GetProperty("evidenceId").GetGuid()));
        Assert.Equal(new[] { 1, 2 }, evidence.Select(item => item.GetProperty("slotNo").GetInt32()));
        Assert.Equal(new[] { "BEFORE_WORK", "AFTER_WORK" }, evidence.Select(item => item.GetProperty("evidenceType").GetString()));
        Assert.DoesNotContain(evidence, item => item.GetProperty("evidenceId").GetGuid() == leftover);

        var acceptance = body.GetProperty("acceptance").EnumerateArray().ToList();
        Assert.Equal(new[] { "REJECT", "ACCEPT" }, acceptance.Select(item => item.GetProperty("decision").GetString()));
        Assert.Equal("Still noisy.", acceptance[0].GetProperty("decisionReason").GetString());
        Assert.Equal(world.Contact.UserId, acceptance[1].GetProperty("acceptanceContactId").GetGuid());

        // No internal security or storage value, e-mail, snapshot or free-text pause/cancel reason is part of the report.
        var names = EvidenceApiClient.PropertyNames(body).Select(name => name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, name => name.Contains("storage") || name.Contains("hash") || name.Contains("tenant") || name.Contains("email")
                                             || name.Contains("snapshot") || name.Contains("rowversion") || name.Contains("scanstatus") || name == "reason"
                                             || name.Contains("pausereason") || name.Contains("cancelreason") || name.Contains("missedreason"));
    }

    [Fact]
    public async Task AWorkOrderWithoutASubmittedSummary_ReturnsAnEmptyButValidReport()
    {
        var world = await _api.CreateWorldAsync();

        var body = await EvidenceApiClient.Json(await _api.GetAsync(Report(world.WorkOrderId), world.Contact));

        Assert.Empty(body.GetProperty("workSummaries").EnumerateArray());
        Assert.Empty(body.GetProperty("acceptance").EnumerateArray());
        Assert.Single(body.GetProperty("visits").EnumerateArray());
    }

    [Fact]
    public async Task TheTimeline_IsUnchanged_EvidenceAndSlaRowsNeverAppearInIt()
    {
        var (world, _, _, _) = await FullWorldAsync();

        var response = await _api.GetAsync($"/api/v1/entities/WORK_ORDER/{world.WorkOrderId}/timeline", world.Supervisor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await EvidenceApiClient.Json(response)).GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.NotEqual("WORK_EVIDENCE", item.GetProperty("entityType").GetString());
            Assert.NotEqual("SLA_RECORD", item.GetProperty("entityType").GetString());
            Assert.False(item.GetProperty("actionCode").GetString()!.StartsWith("WORK_EVIDENCE_", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task EveryReportIsReadOnly_AGetNeverWritesAnAuditRowOrChangesAnyState()
    {
        var (world, _, _, _) = await FullWorldAsync();
        var auditsBefore = await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.TenantId == world.TenantId));
        var summariesBefore = await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(summary => summary.TenantId == world.TenantId));

        foreach (var caller in new[] { world.Technician, world.TeamLead, world.Supervisor, world.Contact, world.Technician })
        {
            Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync(Report(world.WorkOrderId), caller)).StatusCode);
        }

        Assert.Equal(auditsBefore, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.TenantId == world.TenantId)));
        Assert.Equal(summariesBefore, await _factory.WithDbAsync(db => db.WorkSummaries.CountAsync(summary => summary.TenantId == world.TenantId)));
    }
}
