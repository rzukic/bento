using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Models;
using Bento.Api.Services;
using Bento.Api.Workers;

namespace Bento.Api.Controllers;

/// <summary>
/// Custom functions (Step 4.1): JavaScript called from formulas with
/// <c>CUSTOM("name", …)</c>. Everyone who reads data may see them; only
/// admins write and test them, because they run code on the server (sandboxed).
/// </summary>
[ApiController]
[Route(ApiRoutes.Functions)]
[RequireRole("viewer", "api")]
public sealed class FunctionsController : ControllerBase
{
    private readonly CustomFunctionService _functions;
    private readonly StoredCustomValues _stored;

    public FunctionsController(CustomFunctionService functions, StoredCustomValues stored)
    {
        _functions = functions;
        _stored = stored;
    }

    [HttpGet]
    public async Task<IActionResult> List() => Ok(new { data = await _functions.ListAsync() });

    /// <summary>The background recalculation of computed columns that call functions: pending, last run, failures.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new { data = _stored.Status });

    [HttpGet("{name}")]
    public async Task<IActionResult> Get(string name) => Ok(new { data = await _functions.GetAsync(name) });

    [HttpGet("{name}/versions")]
    public async Task<IActionResult> Versions(string name) => Ok(new { data = await _functions.VersionsAsync(name) });

    [HttpPost]
    [RequireRole("admin")]
    public async Task<IActionResult> Create([FromBody] CustomFunctionRequest request) =>
        StatusCode(StatusCodes.Status201Created, new { data = await _functions.CreateAsync(request) });

    [HttpPut("{name}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Update(string name, [FromBody] CustomFunctionRequest request) =>
        Ok(new { data = await _functions.UpdateAsync(name, request) });

    [HttpDelete("{name}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Delete(string name)
    {
        await _functions.DeleteAsync(name);
        return NoContent();
    }

    /// <summary>Runs unsaved code with test values in the sandbox.</summary>
    [HttpPost("test")]
    [RequireRole("admin")]
    public IActionResult Test([FromBody] CustomFunctionTestRequest request) => Ok(new { data = _functions.Test(request) });
}
