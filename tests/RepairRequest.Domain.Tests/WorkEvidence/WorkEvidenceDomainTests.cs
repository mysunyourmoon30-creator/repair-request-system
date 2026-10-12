using System.Reflection;
using RepairRequest.Domain.WorkEvidence;

namespace RepairRequest.Domain.Tests.WorkEvidence;

/// <summary>`docs/16` OD1: the closed evidence-type catalog, the 1..10 rule and the immutable round snapshot (baseline EVD-001/003/004/013).</summary>
public class WorkEvidenceDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static WorkEvidenceUpload Upload(WorkEvidenceType type = WorkEvidenceType.AfterWork) =>
        WorkEvidenceUpload.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), type, Now);

    // ---------------- evidence type allowlist ----------------

    [Fact]
    public void EvidenceTypes_AreExactlyBeforeWorkAfterWorkAndOther()
    {
        Assert.Equal(new[] { "BEFORE_WORK", "AFTER_WORK", "OTHER" }, WorkEvidenceTypeCodes.All);
        Assert.Equal(3, Enum.GetValues<WorkEvidenceType>().Length);
        Assert.Equal("BEFORE_WORK", WorkEvidenceTypeCodes.ToCode(WorkEvidenceType.BeforeWork));
        Assert.Equal("AFTER_WORK", WorkEvidenceTypeCodes.ToCode(WorkEvidenceType.AfterWork));
        Assert.Equal("OTHER", WorkEvidenceTypeCodes.ToCode(WorkEvidenceType.Other));
    }

    [Theory]
    [InlineData("BEFORE_WORK", WorkEvidenceType.BeforeWork)]
    [InlineData(" after_work ", WorkEvidenceType.AfterWork)]
    [InlineData("Other", WorkEvidenceType.Other)]
    public void TryParse_NormalizesCaseAndWhitespace(string code, WorkEvidenceType expected)
    {
        Assert.True(WorkEvidenceTypeCodes.TryParse(code, out var type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DURING_WORK")]
    [InlineData("BEFORE-WORK")]
    [InlineData("PHOTO")]
    [InlineData("0")]
    public void TryParse_RejectsEverythingOutsideTheCatalog(string? code) =>
        Assert.False(WorkEvidenceTypeCodes.TryParse(code, out _));

    // ---------------- rules ----------------

    [Fact]
    public void Rules_AreOneToTenCleanItemsPerSubmission()
    {
        Assert.Equal(1, WorkEvidenceRules.MinCleanPerSubmission);
        Assert.Equal(10, WorkEvidenceRules.MaxPerSubmission);
    }

    // ---------------- upload ----------------

    [Fact]
    public void Upload_Create_SetsStableIdentityAndTheRoundVisit()
    {
        var tenantId = Guid.NewGuid();
        var workOrderId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var upload = WorkEvidenceUpload.Create(tenantId, workOrderId, visitId, fileId, WorkEvidenceType.Other, Now);

        Assert.NotEqual(Guid.Empty, upload.Id);
        Assert.Equal(tenantId, upload.TenantId);
        Assert.Equal(workOrderId, upload.WorkOrderId);
        Assert.Equal(visitId, upload.ServiceVisitId);
        Assert.Equal(fileId, upload.FileAssetId);
        Assert.Equal(WorkEvidenceType.Other, upload.EvidenceType);
        Assert.Equal(Now, upload.CreatedAt);
    }

    [Fact]
    public void Upload_Create_RejectsEmptyIdsNonUtcAndUndefinedType()
    {
        Assert.Throws<ArgumentException>(() => WorkEvidenceUpload.Create(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorkEvidenceType.Other, Now));
        Assert.Throws<ArgumentException>(() => WorkEvidenceUpload.Create(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), WorkEvidenceType.Other, Now));
        Assert.Throws<ArgumentException>(() => WorkEvidenceUpload.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), WorkEvidenceType.Other, Now));
        Assert.Throws<ArgumentException>(() => WorkEvidenceUpload.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, WorkEvidenceType.Other, Now));
        Assert.Throws<ArgumentException>(() => WorkEvidenceUpload.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorkEvidenceType.Other, DateTime.SpecifyKind(Now, DateTimeKind.Local)));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkEvidenceUpload.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), (WorkEvidenceType)99, Now));
    }

    // ---------------- binding (immutable round snapshot) ----------------

    [Fact]
    public void Bind_FreezesTheUpload_UnderTheSameIdentity_IntoTheSubmittedSummary()
    {
        var upload = Upload(WorkEvidenceType.BeforeWork);
        var summaryId = Guid.NewGuid();
        var boundAt = Now.AddMinutes(5);

        var binding = WorkSummaryEvidence.Bind(upload, summaryId, slotNo: 3, boundAt);

        Assert.Equal(upload.Id, binding.Id);
        Assert.Equal(upload.TenantId, binding.TenantId);
        Assert.Equal(upload.WorkOrderId, binding.WorkOrderId);
        Assert.Equal(upload.ServiceVisitId, binding.ServiceVisitId);
        Assert.Equal(summaryId, binding.WorkSummaryId);
        Assert.Equal(upload.FileAssetId, binding.FileAssetId);
        Assert.Equal(WorkEvidenceType.BeforeWork, binding.EvidenceType);
        Assert.Equal(3, binding.SlotNo);
        Assert.Equal(boundAt, binding.BoundAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Bind_AcceptsTheFirstAndTheTenthSlot(int slot) =>
        Assert.Equal(slot, WorkSummaryEvidence.Bind(Upload(), Guid.NewGuid(), slot, Now).SlotNo);

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public void Bind_RejectsAnEleventhOrZeroSlot(int slot) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkSummaryEvidence.Bind(Upload(), Guid.NewGuid(), slot, Now));

    [Fact]
    public void Bind_RejectsAnEmptySummaryANullUploadAndANonUtcTime()
    {
        Assert.Throws<ArgumentException>(() => WorkSummaryEvidence.Bind(Upload(), Guid.Empty, 1, Now));
        Assert.Throws<ArgumentNullException>(() => WorkSummaryEvidence.Bind(null!, Guid.NewGuid(), 1, Now));
        Assert.Throws<ArgumentException>(() => WorkSummaryEvidence.Bind(Upload(), Guid.NewGuid(), 1, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void OtherTypeIsAnOrdinaryCatalogValue_AndBindsLikeAnyOther() =>
        Assert.Equal(WorkEvidenceType.Other, WorkSummaryEvidence.Bind(Upload(WorkEvidenceType.Other), Guid.NewGuid(), 1, Now).EvidenceType);

    [Theory]
    [InlineData(typeof(WorkEvidenceUpload))]
    [InlineData(typeof(WorkSummaryEvidence))]
    public void Entities_ExposeNoMutatorAndNoPublicConstructor(Type type)
    {
        Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.True(property.SetMethod is null || !property.SetMethod.IsPublic, property.Name));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);
    }
}
