using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Repere.Api.Authorization;
using Repere.Api.Db.Schema;
using Repere.Api.Models;
using Repere.Api.Services;

namespace Repere.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Admin)]
[RequireRole("admin")]
public sealed class AdminController : ControllerBase
{
    private readonly AdminHealthService _adminHealthService;
    private readonly SchemaService _schemaService;
    private readonly ModelJsonStore _modelJsonStore;
    private readonly SchemaCache _schemaCache;

    public AdminController(
        AdminHealthService adminHealthService,
        SchemaService schemaService,
        ModelJsonStore modelJsonStore,
        SchemaCache schemaCache)
    {
        _adminHealthService = adminHealthService;
        _schemaService = schemaService;
        _modelJsonStore = modelJsonStore;
        _schemaCache = schemaCache;
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

    /// <summary>
    /// Return the current Model JSON schema.
    /// </summary>
    [HttpGet("schema")]
    public async Task<IActionResult> GetSchema()
    {
        var model = await _modelJsonStore.GetCurrentAsync();
        model ??= new ModelJson { Version = 0 };

        var json = ModelJsonSerializer.Serialize(model);
        return Content(json, "application/json");
    }

    /// <summary>
    /// Apply a new Model JSON schema. Validates, diffs, generates migration, and applies it.
    /// Pass ?confirm_destructive=true to confirm destructive changes.
    /// </summary>
    [HttpPut("schema")]
    public async Task<IActionResult> PutSchema(
        [FromBody] JsonElement body,
        [FromQuery(Name = "confirm_destructive")] bool confirmDestructive = false)
    {
        ModelJson requestedModel;
        try
        {
            requestedModel = ModelJsonSerializer.Parse(body.GetRawText());
        }
        catch (JsonException ex)
        {
            return BadRequest(new
            {
                error = new
                {
                    code = "VALIDATION_FAILED",
                    message = $"Invalid Model JSON: {ex.Message}"
                }
            });
        }

        var result = await _schemaService.ApplySchemaAsync(requestedModel, confirmDestructive);

        // Validation errors → 400
        if (result.Errors.Count > 0)
        {
            return BadRequest(new
            {
                error = new
                {
                    code = "VALIDATION_FAILED",
                    message = "Schema validation failed.",
                    details = new
                    {
                        errors = result.Errors.Select(e => new { e.Code, e.Message, e.Path })
                    }
                }
            });
        }

        // Destructive changes require confirmation → 409
        if (result.RequiresConfirmation)
        {
            return Conflict(new
            {
                error = new
                {
                    code = "CONFIRMATION_REQUIRED",
                    message = "Schema change includes destructive operations. Resend with ?confirm_destructive=true to apply.",
                    details = new
                    {
                        changes = result.Changes.Select(c => new
                        {
                            changeType = c.ChangeType.ToString(),
                            table = c.TableName,
                            column = c.ColumnName,
                            isDestructive = c.IsDestructive
                        })
                    }
                }
            });
        }

        // Reload schema cache so data routes reflect the new schema immediately
        if (result.Applied)
        {
            await _schemaCache.ReloadAsync();
        }

        return Ok(new
        {
            applied = result.Applied,
            version = result.Applied ? result.TargetModel.Version : result.CurrentModel.Version,
            changes = result.Changes.Select(c => new
            {
                changeType = c.ChangeType.ToString(),
                table = c.TableName,
                column = c.ColumnName,
                isDestructive = c.IsDestructive
            }),
            warnings = result.Warnings.Select(w => new { w.Code, w.Message, w.Path })
        });
    }

    /// <summary>
    /// Return the migration history in reverse chronological order.
    /// </summary>
    [HttpGet("migrations")]
    public async Task<IActionResult> GetMigrations()
    {
        var history = await _schemaService.GetHistoryAsync();

        return Ok(new
        {
            data = history.Select(h => new
            {
                id = h.Id,
                versionFrom = h.VersionFrom,
                versionTo = h.VersionTo,
                appliedAt = h.AppliedAt
            })
        });
    }

    /// <summary>
    /// Rollback the last migration.
    /// </summary>
    [HttpPost("migrations/rollback")]
    public async Task<IActionResult> RollbackMigration()
    {
        var result = await _schemaService.RollbackLastAsync();

        if (result is null)
        {
            return BadRequest(new
            {
                error = new
                {
                    code = "NO_MIGRATIONS",
                    message = "No migrations to rollback."
                }
            });
        }

        // Reload schema cache after rollback
        await _schemaCache.ReloadAsync();

        return Ok(new
        {
            rolledBack = true,
            migration = new
            {
                versionFrom = result.VersionFrom,
                versionTo = result.VersionTo
            }
        });
    }
}
