using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.Tests.WorkEvidence;

/// <summary>
/// The CLEAN evidence guard inside Submit/Resubmit Work Summary (`docs/16` D3, OD1; `docs/13` §4.28) against in-memory ports. The client names
/// the evidence with <c>evidenceIds</c>; the guard order (404 → 409 stale → 409 visit → 409 state → lock → 422 fields + evidence) holds, 1 and 10
/// selected CLEAN items pass, 0, 11 and duplicate ids fail, a selected PENDING/FAILED item or an id of another tenant, Work Order or Visit fails
/// with one identical non-disclosing message, an unselected CLEAN upload is never bound, slots follow the request order, any evidence type —
/// including OTHER — satisfies the minimum, and a failed Submit binds and commits nothing.
/// </summary>
public class WorkSummarySubmitEvidenceGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, 321, TimeSpan.Zero);

    private readonly FakeWorkSummaryStore _summaries = new();
    private readonly FakeWorkEvidenceStore _evidence = new();
    private readonly WorkSummaryService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly CommandContext _context;
    private readonly WorkOrder _workOrder;
    private readonly ServiceVisit _visit;

    public WorkSummarySubmitEvidenceGuardTests()
    {
        _service = new WorkSummaryService(_summaries, _evidence, new FixedClock(Now));
        _context = new CommandContext(new CurrentUser(Guid.NewGuid(), _tenantId, [RoleCodes.Technician]), Guid.NewGuid());

        _workOrder = WorkOrder.Create(_tenantId, Guid.NewGuid(), "WO-1", DateTime.UtcNow);
        SetId(_workOrder, Guid.NewGuid());
        _workOrder.Schedule(Guid.NewGuid());
        _workOrder.BeginWork();

        _visit = NewVisit();
        _summaries.Loaded = new WorkOrderForSummarySubmit(_workOrder, _visit);
    }

    private ServiceVisit NewVisit()
    {
        var visit = ServiceVisit.Create(
            _tenantId, _workOrder.Id == Guid.Empty ? Guid.NewGuid() : _workOrder.Id, ServiceVisitType.Initial, Guid.NewGuid(), Guid.NewGuid(),
            DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2));
        SetId(visit, Guid.NewGuid());
        return visit;
    }

    private static void SetId(object entity, Guid id) => entity.GetType().GetProperty("Id")!.SetValue(entity, id);

    /// <summary>An upload of the current round (tenant, Work Order and Visit as the Submit resolves them) with a file in the given scan state.</summary>
    private WorkEvidenceUpload Upload(
        MalwareScanStatus scan = MalwareScanStatus.Clean,
        WorkEvidenceType type = WorkEvidenceType.AfterWork,
        bool bound = false,
        Guid? tenantId = null,
        Guid? workOrderId = null,
        Guid? visitId = null)
    {
        var upload = WorkEvidenceUpload.Create(
            tenantId ?? _tenantId, workOrderId ?? _workOrder.Id, visitId ?? _visit.Id, Guid.NewGuid(), type, DateTime.UtcNow);
        _evidence.SelectionRows.Add(new WorkEvidenceSelectionRow(upload, scan, bound));
        return upload;
    }

    private List<Guid> CleanUploads(int count) => Enumerable.Range(0, count).Select(_ => Upload().Id).ToList();

    private Task<CommandResult<Guid>> SubmitAsync(
        IReadOnlyList<Guid>? ids, string? text = "Replaced the bearing.", string? outcome = "REPAIRED") =>
        _service.SubmitAsync(_context, _workOrder.Id, _workOrder.RowVersion, text, outcome, ids, CancellationToken.None);

    private void AssertNothingBound()
    {
        Assert.Empty(_evidence.Bindings);
        Assert.Empty(_summaries.Committed);
        Assert.Empty(_summaries.CommittedAudits);
        Assert.Equal(WorkOrderStatus.InProgress, _workOrder.Status);
    }

    // ---------------- 0 / 1 / 10 / 11 ----------------

    [Theory]
    [MemberData(nameof(MissingSelections))]
    public async Task Submit_WithNullOrNoEvidenceIds_Is422OnEvidenceIds_AndBindsNothing(IReadOnlyList<Guid>? ids)
    {
        CleanUploads(3); // even with CLEAN uploads waiting, nothing is bound automatically

        var result = await SubmitAsync(ids);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal([WorkEvidenceFields.EvidenceIds], result.Error!.Errors.Keys);
        Assert.Equal(WorkEvidenceGuard.MissingMessage, result.Error.Errors[WorkEvidenceFields.EvidenceIds][0]);
        Assert.Equal(0, _evidence.SelectionCalls);
        AssertNothingBound();
    }

    public static IEnumerable<object?[]> MissingSelections() =>
    [
        [null],
        [Array.Empty<Guid>()]
    ];

    [Fact]
    public async Task Submit_WithExactlyOneSelectedCleanItem_Succeeds_AndBindsItInSlotOne()
    {
        var id = Upload().Id;

        var result = await SubmitAsync([id]);

        Assert.True(result.Succeeded);
        var binding = Assert.Single(_evidence.Bindings);
        Assert.Equal(id, binding.Id);
        Assert.Equal(1, binding.SlotNo);
        Assert.Equal(Now.UtcDateTime, binding.BoundAt);
        Assert.Equal(WorkOrderStatus.AwaitingSupervisorReview, _workOrder.Status);
    }

    [Fact]
    public async Task Submit_WithExactlyTenSelectedCleanItems_BindsAllTen_InRequestOrder_WithSlotsOneToTen()
    {
        var ids = CleanUploads(10);

        var result = await SubmitAsync(ids);

        Assert.True(result.Succeeded);
        Assert.Equal(Enumerable.Range(1, 10), _evidence.Bindings.Select(binding => binding.SlotNo));
        Assert.Equal(ids, _evidence.Bindings.Select(binding => binding.Id));
    }

    [Fact]
    public async Task Submit_WithElevenSelectedItems_Is422OnEvidenceIds_WithoutQueryingTheStore_AndBindsNothing()
    {
        var result = await SubmitAsync(CleanUploads(11));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal(WorkEvidenceGuard.TooManyMessage, result.Error!.Errors[WorkEvidenceFields.EvidenceIds][0]);
        Assert.Equal(0, _evidence.SelectionCalls);
        AssertNothingBound();
    }

    [Fact]
    public async Task Submit_WithDuplicateIds_Is422OnEvidenceIds_AndBindsNothing()
    {
        var id = Upload().Id;
        var other = Upload().Id;

        var result = await SubmitAsync([id, other, id]);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal(WorkEvidenceGuard.DuplicateMessage, result.Error!.Errors[WorkEvidenceFields.EvidenceIds][0]);
        Assert.Equal(0, _evidence.SelectionCalls);
        AssertNothingBound();
    }

    [Fact]
    public async Task Submit_WithAnEmptyGuid_Is422WithTheGenericMessage()
    {
        var result = await SubmitAsync([Upload().Id, Guid.Empty]);

        Assert.Equal(WorkEvidenceGuard.InvalidSetMessage, result.Error!.Errors[WorkEvidenceFields.EvidenceIds][0]);
        AssertNothingBound();
    }

    [Theory]
    [InlineData(WorkEvidenceType.BeforeWork)]
    [InlineData(WorkEvidenceType.AfterWork)]
    [InlineData(WorkEvidenceType.Other)]
    public async Task Submit_IsSatisfiedByAnyApprovedType_IncludingOther(WorkEvidenceType type)
    {
        var id = Upload(type: type).Id;

        Assert.True((await SubmitAsync([id])).Succeeded);
        Assert.Equal(type, Assert.Single(_evidence.Bindings).EvidenceType);
    }

    // ---------------- selection is exact ----------------

    [Fact]
    public async Task Submit_BindsExactlyTheSelectedIds_AnUnselectedCleanUploadIsNeverBoundAutomatically()
    {
        var selected = Upload().Id;
        var unselected = Upload().Id;

        var result = await SubmitAsync([selected]);

        Assert.True(result.Succeeded);
        Assert.Equal(selected, Assert.Single(_evidence.Bindings).Id);
        Assert.DoesNotContain(_evidence.Bindings, binding => binding.Id == unselected);
        // The unselected upload stays selectable; no state of it changed.
        Assert.Contains(_evidence.SelectionRows, row => row.Upload.Id == unselected && !row.Bound);
    }

    [Fact]
    public async Task Submit_PreservesTheRequestOrderAsDeterministicSlots_IndependentOfUploadOrder()
    {
        var first = Upload().Id;
        var second = Upload().Id;
        var third = Upload().Id;

        var result = await SubmitAsync([third, first, second]);

        Assert.True(result.Succeeded);
        Assert.Equal([third, first, second], _evidence.Bindings.OrderBy(binding => binding.SlotNo).Select(binding => binding.Id));
        Assert.Equal([1, 2, 3], _evidence.Bindings.Select(binding => binding.SlotNo));
    }

    [Fact]
    public async Task Submit_ResolvesTheIdsAgainstTheServerDerivedTenantWorkOrderAndVisit_NeverAgainstTheRequest()
    {
        var id = Upload().Id;

        Assert.True((await SubmitAsync([id])).Succeeded);

        Assert.Equal(_workOrder.TenantId, _evidence.LastTenantId);
        Assert.Equal(_workOrder.Id, _evidence.LastWorkOrderId);
        Assert.Equal(_visit.Id, _evidence.LastVisitId);
        Assert.Equal([id], _evidence.LastRequestedIds!);
    }

    // ---------------- invalid sets: one non-disclosing answer ----------------

    public static IEnumerable<object[]> UnusableSelections() =>
    [
        ["pending"],
        ["failed"],
        ["malware"],
        ["unknown"],
        ["other-tenant"],
        ["other-work-order"],
        ["other-visit"]
    ];

    [Theory]
    [MemberData(nameof(UnusableSelections))]
    public async Task Submit_WithAnUnusableSelectedId_Is422_WithTheSameGenericMessage_AndBindsNothing(string kind)
    {
        var good = Upload().Id;
        var bad = kind switch
        {
            "pending" => Upload(MalwareScanStatus.Pending).Id,
            "failed" => Upload(MalwareScanStatus.Failed).Id,
            "malware" => Upload(MalwareScanStatus.Failed).Id, // malware-detected files are persisted as FAILED by the scan service
            "unknown" => Guid.NewGuid(),
            "other-tenant" => Upload(tenantId: Guid.NewGuid()).Id,
            "other-work-order" => Upload(workOrderId: Guid.NewGuid()).Id,
            _ => Upload(visitId: Guid.NewGuid()).Id
        };

        var result = await SubmitAsync([good, bad]);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal([WorkEvidenceFields.EvidenceIds], result.Error!.Errors.Keys);
        Assert.Equal([WorkEvidenceGuard.InvalidSetMessage], result.Error.Errors[WorkEvidenceFields.EvidenceIds]);
        AssertNothingBound();
    }

    [Fact]
    public void Submit_TheInvalidSetMessage_DoesNotSayWhyOrWhere()
    {
        var message = WorkEvidenceGuard.InvalidSetMessage;

        foreach (var forbidden in new[] { "PENDING", "FAILED", "malware", "tenant", "Work Order", "Site", "another", "unknown", "not found", "exist" })
        {
            Assert.DoesNotContain(forbidden, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Submit_WithOnlyPendingItemsSelected_DoesNotSatisfyTheGuard()
    {
        var result = await SubmitAsync([Upload(MalwareScanStatus.Pending).Id]);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        AssertNothingBound();
    }

    [Fact]
    public async Task Submit_WithAnAlreadyBoundSelectedItem_IsAStateConflict_NotAValidationError()
    {
        var bound = Upload(bound: true).Id;

        var result = await SubmitAsync([Upload().Id, bound]);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        AssertNothingBound();
    }

    // ---------------- round identity ----------------

    [Fact]
    public async Task Submit_BindsTheSetOfTheVisitItSelected_AndTheBindingsPointAtTheSubmittedWorkSummary()
    {
        var ids = CleanUploads(2);

        var result = await SubmitAsync(ids);

        Assert.True(result.Succeeded);
        Assert.Equal(_visit.Id, _evidence.LastVisitId);
        var summary = Assert.Single(_summaries.Committed);
        Assert.Equal(_visit.Id, summary.ServiceVisitId);
        Assert.All(_evidence.Bindings, binding =>
        {
            Assert.Equal(summary.Id, binding.WorkSummaryId);
            Assert.Equal(summary.WorkOrderId, binding.WorkOrderId);
            Assert.Equal(summary.ServiceVisitId, binding.ServiceVisitId);
        });

        var bound = _summaries.CommittedAudits.Where(audit => audit.ActionCode == WorkEvidenceAudit.BoundAction).ToList();
        Assert.Equal(2, bound.Count);
        Assert.All(bound, audit =>
        {
            Assert.Equal(WorkEvidenceAudit.EntityType, audit.EntityType);
            Assert.Equal(_context.User.UserId, audit.ActorId);
            Assert.Equal(_context.CorrelationId, audit.CorrelationId);
            Assert.Contains(summary.Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Single(_summaries.CommittedAudits, audit => audit.ActionCode == "WORK_SUMMARY_SUBMITTED");
    }

    [Fact]
    public async Task Resubmit_OnTheCorrectiveVisit_BindsOnlyTheSelectedEvidenceOfThatVisit()
    {
        // Round one's evidence belongs to the first Visit and is already frozen; a later upload belongs to the corrective Visit.
        var firstRound = Upload(bound: true).Id;
        var corrective = NewVisit();
        _summaries.Loaded = new WorkOrderForSummarySubmit(_workOrder, corrective);
        var roundTwo = Upload(visitId: corrective.Id).Id;
        var roundTwoUnselected = Upload(visitId: corrective.Id).Id;

        var result = await SubmitAsync([roundTwo]);

        Assert.True(result.Succeeded);
        Assert.Equal(corrective.Id, _evidence.LastVisitId);
        Assert.Equal(corrective.Id, Assert.Single(_summaries.Committed).ServiceVisitId);
        Assert.Equal(roundTwo, Assert.Single(_evidence.Bindings).Id);
        Assert.DoesNotContain(_evidence.Bindings, binding => binding.Id == roundTwoUnselected || binding.Id == firstRound);
    }

    [Fact]
    public async Task Resubmit_WhoseSelectionNamesTheEarlierRoundsEvidence_IsTheGenericInvalidSet()
    {
        var firstVisitEvidence = Upload(bound: true).Id; // another Visit than the corrective one below
        var corrective = NewVisit();
        _summaries.Loaded = new WorkOrderForSummarySubmit(_workOrder, corrective);
        var current = Upload(visitId: corrective.Id).Id;

        var result = await SubmitAsync([current, firstVisitEvidence]);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal([WorkEvidenceGuard.InvalidSetMessage], result.Error!.Errors[WorkEvidenceFields.EvidenceIds]);
        AssertNothingBound();
    }

    // ---------------- failures bind nothing ----------------

    [Fact]
    public async Task Submit_WithInvalidFieldsAndNoEvidence_ReportsEverythingTogether_AndBindsNothing()
    {
        var result = await SubmitAsync(null, text: "  ", outcome: "NOPE");

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal(
            new[] { WorkEvidenceFields.EvidenceIds, WorkSummaryFields.RepairOutcomeCode, WorkSummaryFields.SummaryText },
            result.Error!.Errors.Keys.Order(StringComparer.Ordinal));
        AssertNothingBound();
    }

    [Fact]
    public async Task Submit_WithInvalidFieldsButCleanSelectedEvidence_Is422OnTheFieldsOnly_AndBindsNothing()
    {
        var result = await SubmitAsync([Upload().Id], text: "", outcome: "REPAIRED");

        Assert.Equal([WorkSummaryFields.SummaryText], result.Error!.Errors.Keys);
        AssertNothingBound();
    }

    [Fact]
    public async Task Submit_WhenTheEvidenceLockIsNotGranted_IsAConcurrencyConflict_WithoutReadingOrBindingEvidence()
    {
        var id = Upload().Id;
        _evidence.LockGranted = false;

        var result = await SubmitAsync([id]);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Equal(0, _evidence.SelectionCalls);
        AssertNothingBound();
    }

    [Fact]
    public async Task Submit_WhenTheSaveConflicts_CommitsNeitherTheSummaryNorAnyBinding()
    {
        var ids = CleanUploads(3);
        _summaries.SaveOutcome = WorkOrderSaveOutcome.ConcurrencyConflict;

        var result = await SubmitAsync(ids);

        // The real store disposes its transaction without a commit, which discards the summary, the bindings and their audits together.
        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_summaries.Committed);
        Assert.Empty(_summaries.CommittedAudits);
    }

    [Fact]
    public async Task Submit_EarlierGuardsRunBeforeTheEvidenceLock()
    {
        var ids = CleanUploads(1);

        // 404 (no session on the Work Order).
        _summaries.Loaded = null;
        Assert.Equal(CommandFailure.NotFound, (await SubmitAsync(ids)).Error?.Failure);

        // 409 stale row version.
        _summaries.Loaded = new WorkOrderForSummarySubmit(_workOrder, _visit);
        Assert.Equal(
            CommandFailure.ConcurrencyConflict,
            (await _service.SubmitAsync(_context, _workOrder.Id, [9, 9, 9, 9, 9, 9, 9, 9], "text", "REPAIRED", ids, CancellationToken.None)).Error?.Failure);

        // 409 not checked out.
        _summaries.Loaded = new WorkOrderForSummarySubmit(_workOrder, null);
        Assert.Equal(CommandFailure.StateConflict, (await SubmitAsync(ids)).Error?.Failure);

        // 409 wrong Work Order state.
        _summaries.Loaded = new WorkOrderForSummarySubmit(_workOrder, _visit);
        _workOrder.SubmitWorkSummary();
        Assert.Equal(CommandFailure.StateConflict, (await SubmitAsync(ids)).Error?.Failure);

        Assert.Equal(0, _evidence.LockCalls);
        Assert.Empty(_evidence.Bindings);
    }

    // ---------------- Check-out stays a pure state transition ----------------

    [Fact]
    public void CheckOut_DoesNotDependOnEvidence()
    {
        foreach (var constructor in typeof(WorkSessionService).GetConstructors())
        {
            Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IWorkEvidenceStore));
        }

        foreach (var constructor in typeof(TechnicianCheckInService).GetConstructors())
        {
            Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IWorkEvidenceStore));
        }
    }
}

/// <summary>In-memory Work Summary Submit port; staged rows become committed only when the command succeeds and the save outcome is Saved.</summary>
internal sealed class FakeWorkSummaryStore : IWorkSummaryStore
{
    private readonly List<WorkSummary> _staged = [];
    private readonly List<AuditHistory> _stagedAudits = [];

    public WorkOrderForSummarySubmit? Loaded { get; set; }

    public WorkOrderSaveOutcome SaveOutcome { get; set; } = WorkOrderSaveOutcome.Saved;

    public List<WorkSummary> Committed { get; } = [];

    public List<AuditHistory> CommittedAudits { get; } = [];

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        var result = await command();
        if (result.Succeeded)
        {
            Committed.AddRange(_staged);
            CommittedAudits.AddRange(_stagedAudits);
        }

        _staged.Clear();
        _stagedAudits.Clear();
        return result;
    }

    public Task<WorkOrderForSummarySubmit?> LoadForSubmitAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(Loaded);

    public Task<WorkOrder?> LoadForAcceptanceSubmitAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public int Reloads { get; private set; }

    public Task ReloadAsync(WorkOrder workOrder, CancellationToken cancellationToken)
    {
        Reloads++;
        return Task.CompletedTask;
    }

    public void Add(WorkSummary summary)
    {
        // The Id is assigned on insert by EF (sequential GUID) and has a private setter; the bindings and audit rows need it.
        typeof(WorkSummary).GetProperty(nameof(WorkSummary.Id))!.SetValue(summary, Guid.NewGuid());
        _staged.Add(summary);
    }

    public void AddAudit(AuditHistory audit) => _stagedAudits.Add(audit);

    public Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        Task.FromResult(SaveOutcome);

    public Task<WorkSummaryDto?> GetWorkSummaryAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string?> ResolveAcceptanceContactSnapshotAsync(Guid tenantId, Guid repairRequestId, Guid acceptanceContactId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<EligibleAcceptanceContactDto>> ListEligibleAcceptanceContactsAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
