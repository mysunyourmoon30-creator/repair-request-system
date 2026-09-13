using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.RepairRequests;

/// <summary>
/// Link between a Repair Request and a shared private FileAsset
/// (RR-DD-001 ATT-001/003/013/014; RR-ERD-001 RepairRequest 1:N RequestAttachment).
/// Only the attachment fields defined by the baseline are modelled.
/// </summary>
public sealed class RepairRequestAttachment
{
    public const int AccessScopeCodeMaxLength = 50;

    private RepairRequestAttachment()
    {
        AccessScopeCode = null!;
    }

    public RepairRequestAttachment(Guid repairRequestId, Guid fileAssetId, string accessScopeCode)
    {
        RepairRequestId = DomainGuard.NotEmpty(repairRequestId, nameof(repairRequestId));
        FileAssetId = DomainGuard.NotEmpty(fileAssetId, nameof(fileAssetId));
        AccessScopeCode = DomainGuard.RequiredText(accessScopeCode, AccessScopeCodeMaxLength, nameof(accessScopeCode));
    }

    /// <summary>ATT-001.</summary>
    public Guid Id { get; private set; }

    /// <summary>ATT-003.</summary>
    public Guid RepairRequestId { get; private set; }

    /// <summary>ATT-013.</summary>
    public Guid FileAssetId { get; private set; }

    /// <summary>ATT-014. Role/site policy key.</summary>
    public string AccessScopeCode { get; private set; }
}
