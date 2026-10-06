using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// The credential vault (Step 4.2): API keys for functions that call APIs.
/// Admins only; values go in and never come back out.
/// </summary>
[ApiController]
[Route(ApiRoutes.Credentials)]
[RequireRole("admin")]
public sealed class CredentialsController : ControllerBase
{
    private readonly CredentialService _credentials;

    public CredentialsController(CredentialService credentials)
    {
        _credentials = credentials;
    }

    [HttpGet]
    public async Task<IActionResult> List() => Ok(new { data = await _credentials.ListAsync() });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CredentialRequest request) =>
        StatusCode(StatusCodes.Status201Created, new { data = await _credentials.CreateAsync(request) });

    /// <summary>Replaces the value and/or the description.</summary>
    [HttpPut("{name}")]
    public async Task<IActionResult> Update(string name, [FromBody] CredentialRequest request) =>
        Ok(new { data = await _credentials.UpdateAsync(name, request) });

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name)
    {
        await _credentials.DeleteAsync(name);
        return NoContent();
    }
}
