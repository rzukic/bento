using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Repere.Api.Authorization;
using Repere.Api.Db.Schema;
using Repere.Api.Models;
using Repere.Api.Services;

namespace Repere.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Admin)]
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
    [ProducesResponseType<AdminHealthPublicResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AdminHealthPublicResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHealthAsync(CancellationToken cancellationToken)
    {
        var result = await _adminHealthService.GetHealthAsync(cancellationToken);
        var statusCode = result.IsHealthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

        return StatusCode(statusCode, BuildPublicHealthResponse(result));
    }

    [HttpGet("health/details")]
    [RequireRole("admin")]
    [ProducesResponseType<AdminHealthDetailedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AdminHealthDetailedResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHealthDetailsAsync(CancellationToken cancellationToken)
    {
        var result = await _adminHealthService.GetHealthAsync(cancellationToken);
        var statusCode = result.IsHealthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

        return StatusCode(statusCode, BuildDetailedHealthResponse(result));
    }

    /// <summary>
    /// Return the current Model JSON schema.
    /// </summary>
    [HttpGet("schema")]
    [RequireRole("admin")]
    public async Task<IActionResult> GetSchema()
    {
        var model = await _modelJsonStore.GetCurrentAsync();
        model ??= new ModelJson { Version = 0 };

        var json = BuildSchemaSnapshotJson(model);
        return Content(json, "application/json");
    }

    /// <summary>
    /// Apply a new Model JSON schema. Validates, diffs, generates migration, and applies it.
    /// Pass ?confirm_destructive=true to confirm destructive changes.
    /// </summary>
    [HttpPut("schema")]
    [RequireRole("admin")]
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
    [RequireRole("admin")]
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
    [RequireRole("admin")]
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

    private static AdminHealthPublicResponse BuildPublicHealthResponse(AdminHealthResult result)
    {
        return new AdminHealthPublicResponse(
            Status: result.IsHealthy ? "healthy" : "unhealthy",
            Database: result.Database,
            Version: result.Version,
            Error: result.IsHealthy ? null : "unhealthy");
    }

    private static AdminHealthDetailedResponse BuildDetailedHealthResponse(AdminHealthResult result)
    {
        return new AdminHealthDetailedResponse(
            Status: result.IsHealthy ? "healthy" : "unhealthy",
            Database: result.Database,
            Runtime: result.Runtime,
            Uptime: result.Uptime,
            Version: result.Version,
            Error: result.Error);
    }

    private static string BuildSchemaSnapshotJson(ModelJson model)
    {
        var serialized = ModelJsonSerializer.Serialize(model);
        var root = JsonNode.Parse(serialized)?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse schema snapshot JSON.");

        if (root["relations"] is not JsonArray relationsArray || relationsArray.Count == 0)
            return root.ToJsonString();

        var resolvedRelations = new RelationResolver().Resolve(model).Relations;
        var count = Math.Min(relationsArray.Count, resolvedRelations.Count);

        for (var index = 0; index < count; index++)
        {
            if (relationsArray[index] is not JsonObject relationObject)
                continue;

            var resolvedRelation = resolvedRelations[index];
            relationObject["resolvedFromAccessor"] = resolvedRelation.FromAccessor;
            relationObject["resolvedToAccessor"] = resolvedRelation.ToAccessor;

            if (!string.IsNullOrWhiteSpace(resolvedRelation.JunctionTableName))
            {
                relationObject["resolvedJunctionTableName"] = resolvedRelation.JunctionTableName;
            }
        }

        return root.ToJsonString();
    }
}
