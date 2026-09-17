using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// FILE-API-002 authorized download (RR-ARCH-001 section 11; RR-UI-001 "authorized download API only"). The file must be
/// attached to a Repair Request within the caller's scope and be CLEAN. The response uses the server's canonical content
/// type, forces a download with a framework-encoded filename, disables MIME sniffing and caching, and never exposes the
/// storage reference.
/// </summary>
[ApiController]
[Route("api/v1/files")]
public sealed class FilesController : CommandControllerBase
{
    private readonly RepairRequestAttachmentService _attachments;

    public FilesController(RepairRequestAttachmentService attachments, ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _attachments = attachments;
    }

    [HttpGet("{fileAssetId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download(Guid fileAssetId, CancellationToken cancellationToken)
    {
        var result = await _attachments.DownloadAsync(await CommandContextAsync(cancellationToken), fileAssetId, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, "File");
        }

        var download = result.Value!;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";

        return File(download.Content, download.MimeType, download.FileName);
    }
}
