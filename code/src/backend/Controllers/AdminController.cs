using Microsoft.AspNetCore.Mvc;
using Repere.Api.Authorization;
using Repere.Api.Models;
using Repere.Api.Services;

namespace Repere.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Admin)]
[RequireRole("admin")]
public sealed class AdminController : ControllerBase
{
    private readonly AdminHealthService _adminHealthService;

    public AdminController(AdminHealthService adminHealthService)
    {
        _adminHealthService = adminHealthService;
    }

    [HttpGet("health")]
    [ProducesResponseType<AdminHealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AdminHealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHealthAsync(CancellationToken cancellationToken)
    {
        var result = await _adminHealthService.GetHealthAsync(cancellationToken);
        var statusCode = result.IsHealthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

        return StatusCode(statusCode, result.Response);
    }
}
