using System.Text;
using System.Text.Json;
using Bento.Api.Exceptions;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;

namespace Bento.Api.Services.Import;

/// <summary>
/// A function as the source of an import (Step 5.11): the user's JavaScript
/// calls any API it is allowed to (its hosts, its credentials) and returns
/// the rows — a list of objects, an object holding one, or text (CSV). Its
/// result is read like a fetched file: the same mapping, keys and schedule.
/// </summary>
public sealed class ImportFunctionRunner
{
    private readonly CustomFunctionCatalog _functions;
    private readonly CustomFunctionService _functionService;
    private readonly ImportFetcher _fetcher;

    public ImportFunctionRunner(CustomFunctionCatalog functions, CustomFunctionService functionService, ImportFetcher fetcher)
    {
        _functions = functions;
        _functionService = functionService;
        _fetcher = fetcher;
    }

    /// <summary>The function by name, checked for an import: no argument or one (when it last ran), a result of any or text.</summary>
    public CustomFunctionDef Require(string? name, string field = "config.function")
    {
        if (string.IsNullOrWhiteSpace(name) || !_functions.Functions.TryGetValue(name.Trim(), out var function))
            throw new ApiValidationException($"There is no function '{name}'.", new Dictionary<string, object?> { ["field"] = field });
        if (function.Arguments.Count > 1)
            throw new ApiValidationException($"{function.Name} takes {function.Arguments.Count} arguments; an import calls it with none, or with one: when it last ran (an ISO date-time, null the first time).",
                new Dictionary<string, object?> { ["field"] = field });
        if (function.ResultType is not ("any" or "text"))
            throw new ApiValidationException($"{function.Name} gives {function.ResultType}; an import needs \"any\" (a list of rows) or \"text\" (CSV).",
                new Dictionary<string, object?> { ["field"] = field });
        return function;
    }

    /// <summary>Whether the function reads credentials from the vault (then only installation admins may use it in an import).</summary>
    public bool UsesCredentials(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _functions.Functions.TryGetValue(name.Trim(), out var function) && function.Credentials.Count > 0;

    /// <summary>Calls the function and returns its result as a file: JSON for a value, the text as it is for text.</summary>
    public async Task<ImportedFile> RunAsync(string name, string? lastRunAt, CancellationToken cancellationToken)
    {
        var definition = Require(name);
        var (function, error) = JsFunction.Create(definition, host: await _functionService.HostAsync(definition));
        if (function is null)
            throw new ApiUnprocessableEntityException($"{definition.Name}: {error}");
        using (function)
        {
            var result = await Task.Run(() => function.Call(definition.Arguments.Count == 1 ? [lastRunAt] : []), cancellationToken);
            if (!result.Ok)
                throw new ApiUnprocessableEntityException($"{definition.Name}: {result.Error}");
            byte[] bytes;
            string fileName;
            if (result.Value is string text)
            {
                bytes = Encoding.UTF8.GetBytes(text);
                var start = text.TrimStart();
                fileName = start.StartsWith('[') || start.StartsWith('{') ? $"{definition.Name}.json" : $"{definition.Name}.csv";
            }
            else
            {
                if (result.Value is not JsonElement { ValueKind: JsonValueKind.Array or JsonValueKind.Object } json)
                    throw new ApiUnprocessableEntityException($"{definition.Name} returned {Describe(result.Value)}; return a list of rows, an object holding one, or CSV text.");
                bytes = JsonSerializer.SerializeToUtf8Bytes(json);
                fileName = $"{definition.Name}.json";
            }
            if (bytes.Length > _fetcher.Options.MaxBytes)
                throw new ApiUnprocessableEntityException($"{definition.Name} returned {bytes.Length / 1024 / 1024} MB; an import reads up to {_fetcher.Options.MaxBytes / 1024 / 1024} MB.");
            return new ImportedFile(bytes, fileName, fileName.EndsWith(".csv", StringComparison.Ordinal) ? "text/csv" : "application/json", function.Requests);
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "nothing",
        JsonElement element => element.ValueKind.ToString().ToLowerInvariant(),
        _ => value.GetType().Name.ToLowerInvariant()
    };
}

/// <summary>What a function gave an import, as a file.</summary>
public sealed record ImportedFile(byte[] Bytes, string FileName, string ContentType, int Requests);
