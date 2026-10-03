using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Formula;
using Bento.Api.Models;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// The formula engine (Step 3.4): validate a formula as it is typed, preview
/// its values on real rows, and list the functions for autocomplete. Nothing
/// is changed, so every role that may read data may use it.
/// </summary>
[ApiController]
[Route(ApiRoutes.Formulas)]
[RequireRole("viewer", "api")]
public sealed class FormulasController : ControllerBase
{
    private readonly FormulaService _formulas;

    public FormulasController(FormulaService formulas)
    {
        _formulas = formulas;
    }

    /// <summary>Syntax, columns and types; always 200 — <c>valid</c> and <c>errors</c> tell the result.</summary>
    [HttpPost("validate")]
    public IActionResult Validate([FromBody] FormulaRequest request) => Ok(new { data = _formulas.Validate(request) });

    /// <summary>The formula's value on the first rows of the table, with the column values it used.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] FormulaRequest request) => Ok(new { data = await _formulas.PreviewAsync(request) });

    [HttpGet("functions")]
    public IActionResult Functions() => Ok(new { data = FormulaFunctions.Catalog, meta = new { keywords = FormulaFunctions.Keywords } });
}
