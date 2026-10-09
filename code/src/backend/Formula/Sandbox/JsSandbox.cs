using System.Globalization;
using Acornima;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace Bento.Api.Formula.Sandbox;

/// <summary>The limits every call of a custom function runs under (Part 5 R5.5).</summary>
public sealed record JsSandboxLimits
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(100);
    public long MemoryBytes { get; init; } = 16 * 1024 * 1024;
    public int RecursionDepth { get; init; } = 200;
    public int MaxLogLines { get; init; } = 50;

    public static readonly JsSandboxLimits Default = new();

    /// <summary>Functions that call APIs (Step 4.2): the clock includes waiting for answers.</summary>
    public static readonly JsSandboxLimits Network = new() { Timeout = TimeSpan.FromSeconds(30), MemoryBytes = 64 * 1024 * 1024 };

    /// <summary>Functions over rows (Step 4.3b): one call handles a whole group.</summary>
    public static readonly JsSandboxLimits Rows = new() { Timeout = TimeSpan.FromSeconds(2), MemoryBytes = 64 * 1024 * 1024 };
}

/// <summary>One call's outcome: a value of the declared type, or an error.</summary>
public sealed record JsCallResult(object? Value, string? Error, IReadOnlyList<string> Logs, double DurationMs)
{
    public bool Ok => Error is null;
}

/// <summary>
/// A custom function compiled into a sandboxed JavaScript engine (Jint, managed
/// .NET: no native binaries in the images). The user writes the function's
/// body; its arguments are the declared names. The engine has no access to
/// .NET, the file system or the network, cannot compile strings
/// (<c>eval</c>, <c>new Function</c>), and each call is stopped at the time,
/// memory and recursion limits.
///
/// One instance may run many calls (a recalculation of a whole table); it is
/// not thread-safe. Functions should not keep state between calls.
///
/// Step 4.2: a function with allowed hosts gets <c>fetch</c> (requests go through
/// <see cref="JsNetwork"/>) and a frozen <c>credentials</c> object with the
/// secrets it declared; its body runs as an async function, so it can
/// <c>await</c>. Secret values are masked in logs and errors.
/// </summary>
public sealed class JsFunction : IDisposable
{
    public const string EntryName = "__bento_fn";

    private readonly Engine _engine;
    private readonly JsValue _entry;
    private readonly CustomFunctionDef _definition;
    private readonly JsSandboxLimits _limits;
    private readonly List<string> _logs = [];
    private readonly JsHost _host;
    private readonly string[] _secretValues;
    private int _requests;

    private JsFunction(CustomFunctionDef definition, JsSandboxLimits limits, JsHost host)
    {
        _definition = definition;
        _limits = limits;
        _host = host;
        _secretValues = host.Secrets.Values.Where(v => v.Length >= 4).OrderByDescending(v => v.Length).ToArray();
        _engine = new Engine(options => options
            .Strict()
            .DisableStringCompilation()
            .TimeoutInterval(limits.Timeout)
            .LimitMemory(limits.MemoryBytes)
            .LimitRecursion(limits.RecursionDepth)
            .RegexTimeoutInterval(limits.Timeout)
            .CatchClrExceptions(e => e is JsFetchException));

        _engine.SetValue("__bento_log", new Action<string>(Log));
        _engine.Execute("""
            const console = Object.freeze({
              log: (...values) => __bento_log(values.map(v => typeof v === "string" ? v : JSON.stringify(v)).join(" ")),
            });
            """);
        if (definition.UsesNetwork)
        {
            _engine.SetValue("__bento_fetch", new Func<string, string?, string>(Fetch));
            _engine.Execute("""
                const fetch = async (url, options) => {
                  const r = JSON.parse(__bento_fetch(String(url), options === undefined || options === null ? null : JSON.stringify(options)));
                  return Object.freeze({
                    ok: r.status >= 200 && r.status < 300, status: r.status, statusText: r.statusText, url: r.url,
                    headers: Object.freeze({ get: (name) => r.headers[String(name).toLowerCase()] ?? null }),
                    text: async () => r.body,
                    json: async () => JSON.parse(r.body),
                  });
                };
                """);
            var secrets = definition.Credentials.ToDictionary(n => n, n => host.Secrets.TryGetValue(n, out var v) ? v : null);
            _engine.Execute($"const credentials = Object.freeze(JSON.parse({System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(secrets))}));");
        }
        _engine.Constraints.Reset();
        _engine.Execute(Source(definition));
        _entry = _engine.GetValue(EntryName);
    }

    /// <summary>The requests this instance has made (for a price source's daily budget).</summary>
    public int Requests => _requests;

    /// <summary>The script the body runs in: line 1 is the header, the body starts on line 2.</summary>
    public static string Source(CustomFunctionDef definition) =>
        $"{(IsAsync(definition) ? "async " : "")}function {EntryName}({string.Join(", ", definition.Arguments.Select(a => a.Name))}) {{\n{definition.Code}\n}}";

    private static bool IsAsync(CustomFunctionDef definition) =>
        definition.UsesNetwork || definition.Code.Contains("await", StringComparison.Ordinal);

    /// <summary>The engine for a function, or the syntax error in its code (with the line in the body).</summary>
    public static (JsFunction? Function, string? Error) Create(CustomFunctionDef definition, JsSandboxLimits? limits = null, JsHost? host = null)
    {
        if (CheckSyntax(definition) is { } syntax)
            return (null, syntax);
        try
        {
            return (new JsFunction(definition, limits ?? (definition.UsesNetwork ? JsSandboxLimits.Network : definition.RowsMode ? JsSandboxLimits.Rows : JsSandboxLimits.Default), host ?? JsHost.None), null);
        }
        catch (Exception e)
        {
            return (null, Describe(e));
        }
    }

    /// <summary>The syntax error in a function's code, or null.</summary>
    public static string? CheckSyntax(CustomFunctionDef definition)
    {
        try
        {
            new Parser().ParseScript(Source(definition));
            return null;
        }
        catch (ParseErrorException e)
        {
            return SyntaxMessage(e);
        }
    }

    /// <summary>Calls the function with values of its declared argument types.</summary>
    public JsCallResult Call(IReadOnlyList<object?> values)
    {
        _logs.Clear();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var args = new JsValue[_definition.Arguments.Count];
            for (var i = 0; i < args.Length; i++)
                args[i] = ToJs(i < values.Count ? values[i] : null, _definition.Arguments[i].Type);

            _engine.Constraints.Reset();
            _engine.Advanced.ResetCallStack();
            var result = _engine.Invoke(_entry, JsValue.Undefined, args).UnwrapIfPromise(_limits.Timeout);
            var (value, error) = FromJs(result, _definition.ResultType);
            return new JsCallResult(value, Redact(error), _logs.ToList(), Elapsed(started));
        }
        catch (Exception e)
        {
            return new JsCallResult(null, Redact(Describe(e)), _logs.ToList(), Elapsed(started));
        }
    }

    /// <summary>
    /// Rows mode (Step 4.3b): one call for a whole group in order. Each argument
    /// is a list with a value per row; the function returns a list as long.
    /// </summary>
    public (IReadOnlyList<object?>? Values, string? Error, IReadOnlyList<string> Logs) CallRows(IReadOnlyList<IReadOnlyList<object?>> columns, int count)
    {
        _logs.Clear();
        try
        {
            var args = new JsValue[_definition.Arguments.Count];
            for (var i = 0; i < args.Length; i++)
            {
                var type = _definition.Arguments[i].Type;
                var column = i < columns.Count ? columns[i] : [];
                args[i] = new Jint.Native.JsArray(_engine, Enumerable.Range(0, count).Select(r => ToJs(r < column.Count ? column[r] : null, type)).ToArray());
            }

            _engine.Constraints.Reset();
            _engine.Advanced.ResetCallStack();
            var result = _engine.Invoke(_entry, JsValue.Undefined, args).UnwrapIfPromise(_limits.Timeout);
            if (result is not Jint.Native.JsArray list)
                return (null, $"A function over rows returns a list with a value per row, not {Kind(result)}.", _logs.ToList());
            if ((int)list.Length != count)
                return (null, $"The function returned {list.Length} values for {count} rows; return a list with a value per row.", _logs.ToList());
            var values = new List<object?>(count);
            for (uint r = 0; r < count; r++)
            {
                var (value, error) = FromJs(list[r], _definition.ResultType);
                if (error is not null)
                    return (null, $"Row {r + 1}: {Redact(error)}", _logs.ToList());
                values.Add(value);
            }
            return (values, null, _logs.ToList());
        }
        catch (Exception e)
        {
            return (null, Redact(Describe(e)), _logs.ToList());
        }
    }

    private string Fetch(string url, string? options)
    {
        if (_host.Network is not { } network)
            throw new JsFetchException("This function cannot use the network here.");
        if (++_requests > network.Options.MaxRequestsPerCall * 50)
            throw new JsFetchException("This function made too many requests.");
        return JsHost.SerializeResult(network.Fetch(url, options, _definition.AllowedHosts));
    }

    /// <summary>Secret values never appear in logs or errors.</summary>
    private string? Redact(string? text)
    {
        if (text is null || _secretValues.Length == 0)
            return text;
        foreach (var secret in _secretValues)
            text = text.Replace(secret, "•••", StringComparison.Ordinal);
        return text;
    }

    public void Dispose() => _engine.Dispose();

    private void Log(string line)
    {
        line = Redact(line)!;
        if (_logs.Count < _limits.MaxLogLines)
            _logs.Add(line.Length > 1000 ? line[..1000] + "…" : line);
    }

    private static double Elapsed(long started) =>
        Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2);

    // ── Values in and out ────────────────────────────────────────────────

    /// <summary>
    /// Numbers as JS numbers, text as strings, yes/no as booleans, dates as
    /// "yyyy-MM-dd" and date-times as ISO strings in UTC ("…Z"), empty as null.
    /// </summary>
    private JsValue ToJs(object? value, string type)
    {
        if (value is null or DBNull)
            return JsValue.Null;
        if (value is System.Text.Json.JsonElement json)
            return json.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined
                ? JsValue.Null
                : _engine.Invoke(_engine.GetValue("JSON").Get("parse"), JsValue.Undefined, [json.GetRawText()]);
        switch (type)
        {
            case "float" or "currency" or "integer":
                return value switch
                {
                    decimal d => (double)d,
                    double d => d,
                    float f => f,
                    long l => l,
                    int n => n,
                    bool b => b ? 1 : 0,
                    string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => JsValue.Null
                };
            case "boolean":
                return value switch
                {
                    bool b => b,
                    long l => l != 0,
                    int n => n != 0,
                    decimal d => d != 0,
                    string s => s.Trim().ToLowerInvariant() is "true" or "1",
                    _ => JsValue.Null
                };
            case "date":
                return CustomFunctionValues.DateText(value) is { } date ? date : JsValue.Null;
            case "datetime":
                return CustomFunctionValues.DateTimeText(value) is { } stamp ? stamp : JsValue.Null;
            case "text":
                return value is string text ? text : System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            default:
                return JsValue.FromObject(_engine, value is decimal m ? (double)m : value);
        }
    }

    /// <summary>The returned value as the declared result type (Part 5: integer, float, currency, text, boolean, date, datetime).</summary>
    private (object? Value, string? Error) FromJs(JsValue value, string resultType)
    {
        if (value.IsNull() || value.IsUndefined())
            return (null, null);

        switch (resultType)
        {
            case "any":
                var json = _engine.Invoke(_engine.GetValue("JSON").Get("stringify"), JsValue.Undefined, [value]);
                return json.IsString()
                    ? (System.Text.Json.JsonDocument.Parse(json.AsString()).RootElement.Clone(), null)
                    : (null, $"The function returned {Kind(value)}, which has no JSON form.");
            case "float" or "currency":
                if (!value.IsNumber())
                    return (null, $"The function returned {Kind(value)}, not a number.");
                var number = value.AsNumber();
                if (double.IsNaN(number) || double.IsInfinity(number))
                    return (null, null);
                return ((decimal)number, null);
            case "integer":
                if (!value.IsNumber())
                    return (null, $"The function returned {Kind(value)}, not a whole number.");
                var whole = value.AsNumber();
                if (double.IsNaN(whole) || double.IsInfinity(whole))
                    return (null, null);
                if (Math.Abs(whole - Math.Round(whole)) > 1e-9)
                    return (null, $"The function returned {whole.ToString(CultureInfo.InvariantCulture)}, not a whole number; round it with Math.round(…).");
                return ((long)Math.Round(whole), null);
            case "boolean":
                return value.IsBoolean() ? (value.AsBoolean(), null) : (null, $"The function returned {Kind(value)}, not true or false.");
            case "date":
                if (value.IsDate())
                    return (DateOnly.FromDateTime(value.AsDate().ToDateTime().ToUniversalTime()), null);
                if (value.IsString() && DateOnly.TryParseExact(value.AsString().Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    return (day, null);
                return (null, $"The function returned {Kind(value)}, not a date (a Date or \"yyyy-MM-dd\").");
            case "datetime":
                if (value.IsDate())
                    return (value.AsDate().ToDateTime().ToUniversalTime(), null);
                if (value.IsString() && DateTimeOffset.TryParse(value.AsString().Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp))
                    return (stamp.UtcDateTime, null);
                return (null, $"The function returned {Kind(value)}, not a date and time (a Date or an ISO string).");
            default:
                if (value.IsString())
                    return (value.AsString(), null);
                if (value.IsNumber() || value.IsBoolean())
                    return (value.ToString(), null);
                return (null, $"The function returned {Kind(value)}, not text.");
        }
    }

    private static string Kind(JsValue value) =>
        value.IsString() ? $"text (\"{Shorten(value.AsString())}\")"
        : value.IsNumber() ? $"a number ({value})"
        : value.IsBoolean() ? $"{(value.AsBoolean() ? "true" : "false")}"
        : value.IsDate() ? "a date"
        : value.IsArray() ? "a list"
        : "an object";

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;

    // ── Errors ───────────────────────────────────────────────────────────

    private static string Describe(Exception e) => e switch
    {
        ParseErrorException parse => SyntaxMessage(parse),
        JsFetchException fetch => fetch.Message,
        _ when e.GetType().Name == "PromiseRejectedException" &&
               e.GetType().GetProperty("RejectedValue")?.GetValue(e) is JsValue rejected => RejectedText(rejected),
        JavaScriptException js => $"{ErrorText(js)}{Line(js.Location.Start.Line)}",
        TimeoutException => "The function took too long and was stopped (time limit).",
        ExecutionCanceledException => "The function was stopped.",
        _ when e.GetType().Name == "MemoryLimitExceededException" => "The function used too much memory and was stopped.",
        _ when e.GetType().Name == "RecursionDepthOverflowException" => "The function calls itself too deeply and was stopped.",
        _ when e.GetType().Name == "StatementsCountOverflowException" => "The function ran too many steps and was stopped.",
        JintException => e.Message,
        _ => $"The function failed: {e.Message}"
    };

    /// <summary>"TypeError: x is not a function"; a thrown value that is no Error as itself.</summary>
    private static string ErrorText(JavaScriptException e)
    {
        if (e.Error is Jint.Native.Object.ObjectInstance error && error.Get("name") is { } name && name.IsString())
            return $"{name.AsString()}: {error.Get("message")}";
        return e.Message;
    }

    private static string RejectedText(JsValue rejected)
    {
        if (rejected is not Jint.Native.Object.ObjectInstance error || error.Get("name") is not { } name || !name.IsString())
            return rejected.ToString();
        // Async functions reject with the Error; its stack names the line ("at __bento_fn (…:4:11)").
        var stack = error.Get("stack");
        var line = stack.IsString() ? System.Text.RegularExpressions.Regex.Match(stack.AsString(), @":(\d+):\d+") : null;
        return $"{name.AsString()}: {error.Get("message")}{(line is { Success: true } ? Line(int.Parse(line.Groups[1].Value, CultureInfo.InvariantCulture)) : "")}";
    }

    private static string SyntaxMessage(ParseErrorException e)
    {
        var message = e.Error?.Description ?? e.Message;
        return $"Syntax error: {message}{Line(e.LineNumber)}";
    }

    /// <summary>The line in the user's body (the script's line 1 is the header).</summary>
    private static string Line(int scriptLine) => scriptLine > 1 ? $" (line {scriptLine - 1})" : "";
}
