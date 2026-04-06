using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Repere.Api.Models;

namespace Repere.Api.Validation;

public static class ApiValidationResponseFactory
{
    public static IActionResult CreateActionResult(ActionContext context)
    {
        var (field, message) = GetFirstValidationError(context);
        var envelope = new ApiErrorEnvelope(new ApiError(
            ApiErrorCode.ValidationFailed.ToStringCode(),
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["field"] = field,
                ["requestId"] = context.HttpContext.TraceIdentifier
            }));

        return new BadRequestObjectResult(envelope);
    }

    private static (string Field, string Message) GetFirstValidationError(ActionContext context)
    {
        var fieldPriorities = BuildFieldPriorities(context);

        var orderedEntries = context.ModelState
            .OrderBy(entry => GetPriority(fieldPriorities, NormalizeField(entry.Key)))
            .ThenBy(entry => entry.Key, StringComparer.Ordinal);

        foreach (var entry in orderedEntries)
        {
            if (entry.Value is null)
            {
                continue;
            }

            foreach (var error in entry.Value.Errors)
            {
                return (NormalizeField(entry.Key), NormalizeMessage(error));
            }
        }

        return ("body", "Request validation failed.");
    }

    private static Dictionary<string, int> BuildFieldPriorities(ActionContext context)
    {
        var bodyParameterType = context.ActionDescriptor.Parameters
            .OfType<ControllerParameterDescriptor>()
            .Select(parameter => parameter.ParameterInfo.ParameterType)
            .FirstOrDefault();

        if (bodyParameterType is null)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        return bodyParameterType
            .GetProperties()
            .Select((property, index) => new { property.Name, Index = index })
            .ToDictionary(
                entry => char.ToLowerInvariant(entry.Name[0]) + entry.Name[1..],
                entry => entry.Index,
                StringComparer.Ordinal);
    }

    private static int GetPriority(IReadOnlyDictionary<string, int> fieldPriorities, string field)
    {
        if (field == "body")
        {
            return int.MaxValue - 1;
        }

        return fieldPriorities.TryGetValue(field, out var priority)
            ? priority
            : int.MaxValue;
    }

    private static string NormalizeField(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == "$")
        {
            return "body";
        }

        var normalized = key;
        if (normalized.StartsWith("$.", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var lastSegment = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastSegment))
        {
            return "body";
        }

        return char.ToLowerInvariant(lastSegment[0]) + lastSegment[1..];
    }

    private static string NormalizeMessage(ModelError error)
    {
        if (!string.IsNullOrWhiteSpace(error.ErrorMessage))
        {
            return error.ErrorMessage;
        }

        return error.Exception is System.Text.Json.JsonException
            ? "Request body is invalid JSON."
            : "Request validation failed.";
    }
}
