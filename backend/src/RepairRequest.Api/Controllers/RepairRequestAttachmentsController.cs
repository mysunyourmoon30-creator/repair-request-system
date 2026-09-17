using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.Attachments;
using RepairRequest.Api.Http;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Repair Request attachments: FILE-API-001 upload (UC-RR-001; TC-RR-002) and a bounded, paged metadata list. Upload uses
/// the REQUESTER policy; ownership, DRAFT state, file validation and private storage are enforced by the Application service.
/// The upload body is capped slightly above the 10 MB file limit to leave room for multipart framing. The list follows the
/// Repair Request read scope and the configured paging limits; there is no business attachment-count limit (decision F1).
/// </summary>
[ApiController]
[Route("api/v1/repair-requests/{repairRequestId:guid}/attachments")]
public sealed class RepairRequestAttachmentsController : CommandControllerBase
{
    private const long MaxRequestBytes = AttachmentFileRules.MaxSizeBytes + (1024 * 1024);

    private readonly RepairRequestAttachmentService _attachments;
    private readonly PagingOptions _paging;

    public RepairRequestAttachmentsController(
        RepairRequestAttachmentService attachments,
        ICurrentUserAccessor currentUserAccessor,
        IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _attachments = attachments;
        _paging = paging.Value;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestRead)]
    [ProducesResponseType<PagedResponse<AttachmentResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid repairRequestId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(page, pageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        var result = await _attachments.ListAsync(await CallerAsync(cancellationToken), repairRequestId, paging, cancellationToken);
        if (result is null)
        {
            return ApiProblemResults.ResourceNotFound(HttpContext, "RepairRequest");
        }

        return Ok(new PagedResponse<AttachmentResponse>(
            result.Items.Select(AttachmentResponses.ToResponse).ToList(),
            result.Page,
            result.PageSize,
            result.TotalCount));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestDraft)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<AttachmentResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload(Guid repairRequestId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return ApiProblemResults.BadRequest(HttpContext, "A multipart/form-data field named 'file' is required.");
        }

        var upload = new AttachmentUpload(file.FileName, file.ContentType, file.Length, file.OpenReadStream);
        var result = await _attachments.UploadAsync(await CommandContextAsync(cancellationToken), repairRequestId, upload, cancellationToken);

        return CommandResult(
            result,
            "RepairRequest",
            AttachmentResponses.ToResponse,
            rowVersion: null,
            dto => AttachmentResponses.DownloadLocation(dto.FileAssetId));
    }
}
