using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>Slow queries, timeouts and server errors (<see cref="ProblemLog"/>), for admins.</summary>
[ApiController]
[Route(ApiRoutes.Problems)]
[RequireRole("admin")]
public sealed class ProblemsController : ControllerBase
{
    private readonly ProblemLog _problems;

    public ProblemsController(ProblemLog problems)
    {
        _problems = problems;
    }

    /// <summary>The newest problems first, with the threshold for slow queries.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? limit) =>
        Ok(new { data = await _problems.ListAsync(limit ?? 200), meta = new { slowMs = _problems.SlowMs } });

    [HttpDelete]
    public async Task<IActionResult> Clear() => Ok(new { data = new { deleted = await _problems.ClearAsync() } });
}
