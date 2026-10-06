using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Models;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// Price sources (Step 4.2). Everyone who reads data sees them and may
/// refresh stale prices (a dashboard does when it opens); admins set them up
/// and run them by hand.
/// </summary>
[ApiController]
[Route(ApiRoutes.PriceSources)]
[RequireRole("viewer", "api")]
public sealed class PriceSourcesController : ControllerBase
{
    private readonly PriceSourceService _sources;

    public PriceSourcesController(PriceSourceService sources)
    {
        _sources = sources;
    }

    [HttpGet]
    public async Task<IActionResult> List() => Ok(new { data = await _sources.ListAsync() });

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) => Ok(new { data = await _sources.GetAsync(id) });

    [HttpGet("{id:long}/runs")]
    public async Task<IActionResult> Runs(long id) => Ok(new { data = await _sources.RunsAsync(id) });

    /// <summary>Fetches prices that are older than their source's freshness window (or all with force, admins only).</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] PriceRefreshRequest? request, CancellationToken cancellationToken)
    {
        request ??= new PriceRefreshRequest();
        if (request.Force && HttpContext.User.GetRole() != "admin")
            request = new PriceRefreshRequest { Tables = request.Tables };
        return Ok(new { data = await _sources.RefreshAsync(request, cancellationToken) });
    }

    [HttpPost]
    [RequireRole("admin")]
    public async Task<IActionResult> Create([FromBody] PriceSourceRequest request) =>
        StatusCode(StatusCodes.Status201Created, new { data = await _sources.CreateAsync(request) });

    [HttpPut("{id:long}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Update(long id, [FromBody] PriceSourceRequest request) =>
        Ok(new { data = await _sources.UpdateAsync(id, request) });

    [HttpDelete("{id:long}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Delete(long id)
    {
        await _sources.DeleteAsync(id);
        return NoContent();
    }

    /// <summary>Runs a source now: latest (the last days), history (keys without history) or daily (both).</summary>
    [HttpPost("{id:long}/run")]
    [RequireRole("admin")]
    public async Task<IActionResult> Run(long id, [FromQuery] string kind = "daily", CancellationToken cancellationToken = default) =>
        Ok(new { data = await _sources.RunAsync(id, kind, cancellationToken) });

    /// <summary>Asks the source's lookup function for symbols of keys that have none; nothing is saved.</summary>
    [HttpPost("{id:long}/suggest")]
    [RequireRole("admin")]
    public async Task<IActionResult> Suggest(long id) => Ok(new { data = await _sources.SuggestAsync(id) });
}
