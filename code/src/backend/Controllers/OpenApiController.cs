using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/openapi.json</c> (Part 6 R6.8): the API of this
/// installation as OpenAPI 3.0, generated from the current schema. Signed-in
/// users and API keys may read it; it lists table and column names.
/// </summary>
[ApiController]
[RequireRole("viewer", "api")]
public sealed class OpenApiController : ControllerBase
{
    private readonly OpenApiDocumentBuilder _builder;

    public OpenApiController(OpenApiDocumentBuilder builder)
    {
        _builder = builder;
    }

    [HttpGet("/" + ApiRoutes.OpenApi)]
    public IActionResult Get() => Content(_builder.Build().ToJsonString(), "application/json");
}
