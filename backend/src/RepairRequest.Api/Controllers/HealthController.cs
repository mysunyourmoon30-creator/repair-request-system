using Microsoft.AspNetCore.Mvc;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Sprint 0 smoke-test endpoint: confirms the API is reachable and returns the
/// correlation id, so the Angular shell / CI pipeline can verify wiring before
/// any business endpoint exists.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "Healthy",
            correlationId = HttpContext.TraceIdentifier,
            timestampUtc = DateTime.UtcNow
        });
    }
}
