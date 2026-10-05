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
/// </summary>
public sealed class JsFunction : IDisposable
{
    public const string EntryName = "__bento_fn";

    private readonly Engine _engine;
    private readonly JsValue _entry;
    private readonly CustomFunctionDef _definition;
    private readonly JsSandboxLimits _limits;
    private readonly List<string> _logs = [];

    private JsFunction(CustomFunctionDef definition, JsSandboxLimits limits)
    {
        _definition = definition;
        _limits = limits;
        _engine = new Engine(options => options
            .Strict()
            .DisableStringCompilation()
            .TimeoutInterval(limits.Timeout)
            .LimitMemory(limits.MemoryBytes)
            .LimitRecursion(limits.RecursionDepth)
            .RegexTimeoutInterval(limits.Timeout));

        _engine.SetValue("__bento_log", new Action<string>(Log));
        _engine.Execute("""
            const console = Object.freeze({
              log: (...values) => __bento_log(values.map(v => typeof v === "string" ? v : JSON.stringify(v)).join(" ")),
            });
            """);
        _engine.Constraints.Reset();
        _engine.Execute(Source(definition));
        _entry = _engine.GetValue(EntryName);
    }

    /// <summary>The script the body runs in: line 1 is the header, the body starts on line 2.</summary>
    public static string Source(CustomFunctionDef definition) =>
        $"function {EntryName}({string.Join(", ", definition.Arguments.Select(a => a.Name))}) {{\n{definition.Code}\n}}";

    /// <summary>The engine for a function, or the syntax error in its code (with the line in the body).</summary>
    public static (JsFunction? Function, string? Error) Create(CustomFunctionDef definition, JsSandboxLimits? limits = null)
    {
        if (CheckSyntax(definition) is { } syntax)
            return (null, syntax);
        try
        {
            return (new JsFunction(definition, limits ?? JsSandboxLimits.Default), null);
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
            var result = _engine.Invoke(_entry, JsValue.Undefined, args);
            var (value, error) = FromJs(result, _definition.ResultType);
            return new JsCallResult(value, error, _logs.ToList(), Elapsed(started));
        }
        catch (Exception e)
        {
            return new JsCallResult(null, Describe(e), _logs.ToList(), Elapsed(started));
        }
    }

    public void Dispose() => _engine.Dispose();

    private void Log(string line)
    {
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
    private static (object? Value, string? Error) FromJs(JsValue value, string resultType)
    {
        if (value.IsNull() || value.IsUndefined())
            return (null, null);

        switch (resultType)
        {
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

    private static string SyntaxMessage(ParseErrorException e)
    {
        var message = e.Error?.Description ?? e.Message;
        return $"Syntax error: {message}{Line(e.LineNumber)}";
    }

    /// <summary>The line in the user's body (the script's line 1 is the header).</summary>
    private static string Line(int scriptLine) => scriptLine > 1 ? $" (line {scriptLine - 1})" : "";
}
