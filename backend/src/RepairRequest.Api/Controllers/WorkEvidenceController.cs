using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.WorkEvidence;
using RepairRequest.Api.Http;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Work Order evidence (`docs/13` §4.28; `docs/16` D3/D4, OD1..OD3): WEV-API-001 upload, WEV-API-002 metadata list and WEV-API-003
/// authorized content download — a dedicated contract that never reuses FILE-API-001/002. Upload is Technician-only (policy
/// <c>WorkEvidence.Upload</c>); list and download use <c>WorkEvidence.Read</c> and the audience rule of the Application service (assigned
/// Technician, in-scope Team Lead/Supervisor, exactly the Acceptance Contact; anyone else gets the same non-leaking 404). The upload body is
/// capped slightly above the 10 MB file limit; the download streams only CLEAN content with a canonical content type, a framework-encoded
/// attachment name, <c>nosniff</c> and <c>no-store</c>, and never exposes the storage reference. There is no delete, replace, rescan or write
/// endpoint.
/// </summary>
[ApiController]
[Route("api/v1/work-orders/{workOrderId:guid}/evidence")]
public sealed class WorkEvidenceController : CommandControllerBase
{
    private const long MaxRequestBytes = AttachmentFileRules.MaxSizeBytes + (1024 * 1024);
    private const string ResourceType = "WorkOrder";
    private const string EvidenceResourceType = "WorkEvidence";

    private readonly WorkEvidenceService _evidence;

    public WorkEvidenceController(WorkEvidenceService evidence, ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _evidence = evidence;
    }

    /// <summary>WEV-API-002: evidence metadata of the Work Order visible to the caller; 404 when the caller has no access to the Work Order.</summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkEvidenceRead)]
    [ProducesResponseType<WorkEvidenceListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid workOrderId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await _evidence.ListAsync(await CallerAsync(cancellationToken), workOrderId, cancellationToken);
        return result is null
            ? ApiProblemResults.ResourceNotFound(HttpContext, ResourceType)
            : Ok(new WorkEvidenceListResponse(result.Value.Items.Select(WorkEvidenceResponses.ToResponse).ToList()));
    }

    /// <summary>WEV-API-001: multipart upload of one evidence file with its evidence type; the file starts PENDING.</summary>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkEvidenceUpload)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<WorkEvidenceItemResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload(Guid workOrderId, [FromForm] WorkEvidenceUploadForm form, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (form.File is null)
        {
            return ApiProblemResults.BadRequest(HttpContext, "A multipart/form-data field named 'file' is required.");
        }

        var file = form.File;
        var upload = new WorkEvidenceFile(file.FileName, file.ContentType, file.Length, file.OpenReadStream);
        var result = await _evidence.UploadAsync(await CommandContextAsync(cancellationToken), workOrderId, form.EvidenceType, upload, cancellationToken);

        return CommandResult(
            result,
            ResourceType,
            WorkEvidenceResponses.ToResponse,
            rowVersion: null,
            dto => WorkEvidenceResponses.ContentLocation(workOrderId, dto.EvidenceId));
    }

    /// <summary>WEV-API-003: the evidence file content; only when it is CLEAN and the caller's audience may open it.</summary>
    [HttpGet("{evidenceId:guid}/content")]
    [Authorize(Policy = AuthorizationPolicies.WorkEvidenceRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download(Guid workOrderId, Guid evidenceId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";

        var result = await _evidence.DownloadAsync(await CommandContextAsync(cancellationToken), workOrderId, evidenceId, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, EvidenceResourceType);
        }

        var download = result.Value!;
        return File(download.Content, download.MimeType, download.FileName);
    }
}
