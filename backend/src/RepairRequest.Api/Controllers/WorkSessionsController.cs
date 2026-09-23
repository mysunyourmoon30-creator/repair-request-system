using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Work Session endpoints (S3-002/S3-003/S3-004; ST-WS-002/003/004; UC-WO-012/016): Pause (WS-API-002), Resume
/// (WS-API-003), Check-out (WS-API-004) and the read of the caller's own current session. Technician only, and
/// only the caller's own session within their tenant and current Site scope — anyone else's, cross-tenant or
/// nonexistent id is the same non-leaking 404. If-Match is checked against the session's own RowVersion.
/// Check-in (WS-API-001) lives on <see cref="ServiceVisitsController"/> since it acts on a Visit and creates the
/// session.
/// </summary>
[ApiController]
[Route("api/v1/work-sessions")]
public sealed class WorkSessionsController : CommandControllerBase
{
    private const string ResourceType = "WorkSession";

    private readonly WorkSessionService _sessions;

    public WorkSessionsController(WorkSessionService sessions, ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _sessions = sessions;
    }

    /// <summary>
    /// The caller's one non-CHECKED_OUT Work Session with its pause history, or <c>204 No Content</c> when there is
    /// none. The ETag is the session's RowVersion. Needed because "My Visits" lists only SCHEDULED visits, so a
    /// technician who has checked in would otherwise have no way to reach their session.
    /// </summary>
    [HttpGet("current")]
    [Authorize(Policy = AuthorizationPolicies.WorkSessionRead)]
    [ProducesResponseType<WorkSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Current(CancellationToken cancellationToken)
    {
        var session = await _sessions.GetCurrentAsync(await CallerAsync(cancellationToken), cancellationToken);
        if (session is null)
        {
            return NoContent();
        }

        return DetailResult(session, ResourceType, WorkSessionResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// WS-API-002 Pause (ST-WS-002): the owning Technician only, a CHECKED_IN session only, with a non-blank
    /// <c>reason</c>. The pause time and the resulting status are server-derived.
    /// </summary>
    [HttpPost("{workSessionId:guid}/pause")]
    [Authorize(Policy = AuthorizationPolicies.WorkSessionPause)]
    [ProducesResponseType<WorkSessionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Pause(Guid workSessionId, [FromBody] PauseWorkSessionRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _sessions.PauseAsync(context, workSessionId, rowVersion, request.Reason, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var session = await _sessions.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(session, ResourceType, WorkSessionResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// WS-API-003 Resume (ST-WS-003): the owning Technician only, a PAUSED session with an open pause only. Empty
    /// request body — the client supplies neither status nor time. Closes the open pause period and returns the
    /// session to CHECKED_IN.
    /// </summary>
    [HttpPost("{workSessionId:guid}/resume")]
    [Authorize(Policy = AuthorizationPolicies.WorkSessionResume)]
    [ProducesResponseType<WorkSessionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid workSessionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _sessions.ResumeAsync(context, workSessionId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var session = await _sessions.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(session, ResourceType, WorkSessionResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// WS-API-004 Check-out (ST-WS-004): the owning Technician only, a CHECKED_IN session only. Empty request
    /// body — the client supplies neither status nor time. Moves the session to CHECKED_OUT and the Visit to
    /// COMPLETED (ST-SV-003); BR-06's summary/outcome/evidence half is not part of this ticket (see
    /// <see cref="RepairRequest.Domain.WorkOrders.WorkSession.CheckOut"/>'s own doc comment).
    /// </summary>
    [HttpPost("{workSessionId:guid}/check-out")]
    [Authorize(Policy = AuthorizationPolicies.WorkSessionCheckOut)]
    [ProducesResponseType<WorkSessionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckOut(Guid workSessionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _sessions.CheckOutAsync(context, workSessionId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var session = await _sessions.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(session, ResourceType, WorkSessionResponses.ToResponse, dto => dto.RowVersion);
    }
}
