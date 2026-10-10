using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;
using Bento.Api.Workers;

namespace Bento.Tests;

/// <summary>Step 4.1: the JavaScript sandbox custom functions run in.</summary>
public sealed class JsSandboxTests
{
    private static CustomFunctionDef Def(string code, string result = "float", params (string Name, string Type)[] args) =>
        new("f", null, args.Select(a => new CustomFunctionArgument(a.Name, a.Type)).ToList(), result, code, 1);

    private static JsCallResult Run(CustomFunctionDef definition, params object?[] values) => Run(definition, null, values);

    private static JsCallResult Run(CustomFunctionDef definition, JsSandboxLimits? limits, params object?[] values)
    {
        var (function, error) = JsFunction.Create(definition, limits);
        Assert.True(function is not null, error);
        using (function)
            return function!.Call(values);
    }

    [Fact]
    public void ArgumentsAndResults_UseTheDeclaredTypes()
    {
        var risk = Def("return volatility > 0.2 ? \"high\" : \"low\";", "text", ("volatility", "float"));
        Assert.Equal("high", Run(risk, 0.35m).Value);
        Assert.Equal("low", Run(risk, 0.1m).Value);
        Assert.Null(Run(risk, [null]).Error);

        Assert.Equal(42L, Run(Def("return n * 2;", "integer", ("n", "integer")), 21L).Value);
        Assert.Equal(2.5m, Run(Def("return a / b;", "float", ("a", "float"), ("b", "float")), 5m, 2m).Value);
        Assert.Equal(true, Run(Def("return flag && true;", "boolean", ("flag", "boolean")), true).Value);
        Assert.Equal(new DateOnly(2026, 10, 31), Run(Def("const d = new Date(day + \"T00:00:00Z\"); d.setUTCMonth(d.getUTCMonth() + 1, 0); return d;", "date", ("day", "date")), "2026-10-05").Value);
        Assert.Equal(new DateOnly(2026, 1, 2), Run(Def("return \"2026-01-02\";", "date")).Value);
        Assert.Equal("2026-10-05|10", Run(Def("return day + \"|\" + day.slice(5, 7);", "text", ("day", "date")), new DateOnly(2026, 10, 5)).Value);
        Assert.Equal("number|string", Run(Def("return typeof x + \"|\" + typeof y;", "text", ("x", "any"), ("y", "any")), 2m, "a").Value);
    }

    [Fact]
    public void WrongResults_AreErrors()
    {
        Assert.Equal("The function returned text (\"x\"), not a number.", Run(Def("return \"x\";")).Error);
        Assert.Equal("The function returned 2.5, not a whole number; round it with Math.round(…).", Run(Def("return 2.5;", "integer")).Error);
        Assert.Equal("The function returned a number (1), not true or false.", Run(Def("return 1;", "boolean")).Error);
        Assert.Null(Run(Def("return undefined;")).Value);
        Assert.Null(Run(Def("return 1 / 0;")).Value);
    }

    [Fact]
    public void Errors_NameTheLineInTheBody()
    {
        var (function, error) = JsFunction.Create(Def("const a = 1;\nreturn a +;"));
        Assert.Null(function);
        Assert.StartsWith("Syntax error:", error);
        Assert.EndsWith("(line 2)", error);
        Assert.Equal(error, JsFunction.CheckSyntax(Def("const a = 1;\nreturn a +;")));
        Assert.Null(JsFunction.CheckSyntax(Def("return 1;")));

        var thrown = Run(Def("const a = 1;\nthrow new Error(\"no price\");"));
        Assert.Equal("Error: no price (line 2)", thrown.Error);
        Assert.Contains("undefinedName is not defined", Run(Def("return undefinedName;")).Error);
    }

    [Fact]
    public void Limits_StopRunawayCode()
    {
        Assert.Equal("The function took longer than 1 s and was stopped (time limit).", Run(Def("while (true) {}")).Error);
        // The memory and time limits on their own, so a slow machine does not hit the other one first.
        var noClock = JsSandboxLimits.Default with { Timeout = TimeSpan.FromSeconds(60) };
        Assert.Equal("The function allocated more than 256 MB and was stopped (memory limit).", Run(Def("const a = []; while (true) a.push(\"x\".repeat(1000000) + a.length);"), noClock).Error);
        Assert.Equal("The function calls itself too deeply and was stopped.", Run(Def("function f(n) { return f(n + 1); } return f(0);")).Error);

        // Beta feedback: a loop that keeps nothing ran into "too much memory" after ~20,000 steps,
        // because every allocation of the call counts. A lean simulation now runs through.
        Assert.Null(Run(Def("let s = 0; for (let i = 0; i < 500000; i++) { const o = { a: i, b: [i, i] }; s += o.b[1]; } return s;"), noClock).Error);

        // The same engine keeps working after a call was stopped.
        var (function, _) = JsFunction.Create(Def("if (n > 0) { while (true) {} } return 7;", "integer", ("n", "integer")));
        using (function)
        {
            Assert.NotNull(function!.Call([1L]).Error);
            Assert.Equal(7L, function.Call([0L]).Value);
        }
    }

    [Fact]
    public void Sandbox_HasNoHostAccess()
    {
        Assert.Contains("is not defined", Run(Def("return System.IO.File.Exists(\"/etc/passwd\") ? 1 : 0;")).Error);
        Assert.Contains("is not defined", Run(Def("return importNamespace(\"System\") ? 1 : 0;")).Error);
        Assert.Contains("is not defined", Run(Def("return require(\"fs\") ? 1 : 0;")).Error);
        Assert.Contains("is not defined", Run(Def("return fetch(\"https://example.com\") ? 1 : 0;")).Error);
        Assert.NotNull(Run(Def("return eval(\"1 + 1\");")).Error);
        Assert.NotNull(Run(Def("return new Function(\"return 1\")();")).Error);
        Assert.Equal("undefined", Run(Def("return typeof __bento_fn_host;", "text")).Value);
    }

    [Fact]
    public void ConsoleLog_IsCaptured()
    {
        var result = Run(Def("console.log(\"price\", n, { a: 1 }); return n;", "float", ("n", "float")), 3m);
        Assert.Equal(["price 3 {\"a\":1}"], result.Logs);
    }
}

/// <summary>
/// Step 4.1 acceptance: write a function, use it as a computed column (stored,
/// recalculated in the background) and in dashboard measures and variables.
/// The same scenario runs on Postgres (<see cref="PostgresCustomFunctionTests"/>).
/// </summary>
public sealed class CustomFunctionTests
{
    private const string Schema = """
        {
            "tables": [
                {
                    "name": "assets",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "ticker", "type": "text" },
                        { "name": "volatility", "type": "float" },
                        { "name": "bought_on", "type": "date" },
                        { "name": "risk", "type": "computed", "formula": "CUSTOM(\"risk_class\", volatility)" },
                        { "name": "risk_label", "type": "computed", "formula": "UPPER(risk) & \"!\"" },
                        { "name": "doubled", "type": "computed", "formula": "CUSTOM(\"double_it\", volatility * 100)" },
                        { "name": "month_end", "type": "computed", "formula": "CUSTOM(\"month_end\", bought_on)" }
                    ]
                }
            ]
        }
        """;

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        await FunctionsApi(host);
        await host.ApplySchemaAsync(Schema);
        await StoredValues(host);
        await Changes(host);
        await DashboardFormulas(host);
        await Usages(host);
    }

    private static async Task FunctionsApi(ApiIntegrationTestHost host)
    {
        var created = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "risk_class",
            description = "Low, medium or high by volatility",
            arguments = new[] { new { name = "volatility", type = "number" } },
            resultType = "text",
            code = "if (volatility === null) return null;\nreturn volatility > 0.3 ? \"high\" : volatility > 0.15 ? \"medium\" : \"low\";"
        }, HttpStatusCode.Created);
        Assert.Equal("risk_class", created.GetProperty("name").GetString());
        Assert.Equal("float", created.GetProperty("arguments")[0].GetProperty("type").GetString());
        Assert.Equal(1, created.GetProperty("version").GetInt32());

        await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "double_it",
            arguments = new[] { new { name = "n", type = "float" } },
            resultType = "float",
            code = "if (n > 1000) throw new Error(\"too big\");\nreturn n * 2;"
        }, HttpStatusCode.Created);
        await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "month_end",
            arguments = new[] { new { name = "day", type = "date" } },
            resultType = "date",
            code = "if (!day) return null;\nconst d = new Date(day + \"T00:00:00Z\");\nd.setUTCMonth(d.getUTCMonth() + 1, 0);\nreturn d;"
        }, HttpStatusCode.Created);

        var duplicate = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new { name = "risk_class", resultType = "text", code = "return 1;" }, HttpStatusCode.Conflict);
        Assert.Equal("A function named 'risk_class' already exists.", duplicate.GetProperty("message").GetString());
        var builtIn = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new { name = "round", code = "return 1;" }, HttpStatusCode.BadRequest);
        Assert.Equal("ROUND is a built-in formula function; choose another name.", builtIn.GetProperty("message").GetString());
        var syntax = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new { name = "broken", code = "return (1;" }, HttpStatusCode.BadRequest);
        Assert.Equal("code", syntax.GetProperty("details").GetProperty("field").GetString());
        Assert.EndsWith("(line 1)", syntax.GetProperty("message").GetString());
        var argument = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new { name = "bad", arguments = new[] { new { name = "class", type = "float" } }, code = "return 1;" }, HttpStatusCode.BadRequest);
        Assert.Equal("arguments[0].name", argument.GetProperty("details").GetProperty("field").GetString());

        var test = await SendAsync(host, HttpMethod.Post, "/api/v1/functions/test", new
        {
            arguments = new[] { new { name = "a", type = "float" }, new { name = "day", type = "date" } },
            resultType = "text",
            code = "console.log(\"a is\", a);\nreturn day + \":\" + (a * 2);",
            values = new object[] { 1.5, "2026-10-05" }
        });
        Assert.Equal("2026-10-05:3", test.GetProperty("value").GetString());
        Assert.Equal(["a is 1.5"], test.GetProperty("logs").EnumerateArray().Select(l => l.GetString()));
        var failing = await SendAsync(host, HttpMethod.Post, "/api/v1/functions/test", new { resultType = "float", code = "while (true) {}" });
        Assert.Equal("The function took longer than 1 s and was stopped (time limit).", failing.GetProperty("error").GetString());
    }

    private static async Task StoredValues(ApiIntegrationTestHost host)
    {
        foreach (var (ticker, volatility, day) in new[] { ("VWCE", 0.12, "2026-01-15"), ("TSLA", 0.55, "2026-02-03"), ("SAP", 0.2, "2026-03-31"), ("BIG", 20.0, "2026-04-01") })
            await PostAsync(host, "assets", new { ticker, volatility, bought_on = day });

        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(
            ["VWCE low LOW! 24 2026-01-31", "TSLA high HIGH! 110 2026-02-28", "SAP medium MEDIUM! 40 2026-03-31", "BIG high HIGH! null 2026-04-30"],
            await AssetsAsync(host));

        // Stored values filter and sort like other columns.
        Assert.Equal(["TSLA", "BIG"], (await ListAsync(host, "assets?filter[risk]=high&sort=id")).Select(a => a.GetProperty("ticker").GetString()));
        Assert.Equal(["TSLA", "SAP", "VWCE"], (await ListAsync(host, "assets?filter[doubled][notnull]=true&sort=-doubled")).Select(a => a.GetProperty("ticker").GetString()));

        // BIG's doubled value failed: the status names it.
        var status = await GetDataAsync(host, "/api/v1/functions/status");
        var failure = status.GetProperty("failures").EnumerateArray().Single();
        Assert.Equal("assets.doubled double_it 1", $"{failure.GetProperty("table").GetString()}.{failure.GetProperty("column").GetString()} {failure.GetProperty("function").GetString()} {failure.GetProperty("rows").GetInt32()}");
        Assert.Equal("Error: too big (line 1)", failure.GetProperty("error").GetString());

        // A second run computes nothing new.
        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(0, (await GetDataAsync(host, "/api/v1/functions/status")).GetProperty("lastUpdated").GetInt32());
    }

    private static async Task Changes(ApiIntegrationTestHost host)
    {
        // A changed row answers with its recalculated values; a deleted row's values go.
        using (var patch = await host.Client.PatchAsJsonAsync("/api/v1/data/assets/1", new { volatility = 0.31 }))
        {
            Assert.True(patch.IsSuccessStatusCode);
            var row = (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("high HIGH! 62", $"{row.GetProperty("risk").GetString()} {row.GetProperty("risk_label").GetString()} {FormulaConformanceTests.Text(row.GetProperty("doubled"))}");
        }
        using (var created = await host.Client.PostAsJsonAsync("/api/v1/data/assets", new { ticker = "NEW", volatility = 0.01 }))
        {
            var row = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("low", row.GetProperty("risk").GetString());
            var id = row.GetProperty("id").GetInt64();
            using var removed = await host.Client.DeleteAsync($"/api/v1/data/assets/{id}");
            Assert.True(removed.IsSuccessStatusCode);
        }
        using (var delete = await host.Client.DeleteAsync("/api/v1/data/assets/4"))
            Assert.True(delete.IsSuccessStatusCode);
        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(["VWCE high HIGH! 62 2026-01-31", "TSLA high HIGH! 110 2026-02-28", "SAP medium MEDIUM! 40 2026-03-31"], await AssetsAsync(host));
        Assert.Empty((await GetDataAsync(host, "/api/v1/functions/status")).GetProperty("failures").EnumerateArray());

        // New code is a new version and recalculates every row.
        var changed = await SendAsync(host, HttpMethod.Put, "/api/v1/functions/risk_class", new
        {
            code = "return volatility > 0.5 ? \"high\" : \"normal\";"
        });
        Assert.Equal(2, changed.GetProperty("version").GetInt32());
        await host.GetService<StoredCustomValues>().RunAsync();
        Assert.Equal(["VWCE normal NORMAL! 62 2026-01-31", "TSLA high HIGH! 110 2026-02-28", "SAP normal NORMAL! 40 2026-03-31"], await AssetsAsync(host));

        var versions = await GetDataAsync(host, "/api/v1/functions/risk_class/versions");
        Assert.Equal([2, 1], versions.EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));

        // A description alone is no new version.
        Assert.Equal(2, (await SendAsync(host, HttpMethod.Put, "/api/v1/functions/risk_class", new { description = "By volatility" })).GetProperty("version").GetInt32());

        // A signature that breaks a computed column is refused.
        var breaking = await SendAsync(host, HttpMethod.Put, "/api/v1/functions/risk_class", new
        {
            arguments = new[] { new { name = "volatility", type = "float" }, new { name = "limit", type = "float" } }
        }, HttpStatusCode.Conflict);
        Assert.StartsWith("The change would break 1 computed column: assets.risk: risk_class takes 2 arguments", breaking.GetProperty("message").GetString());
        var renamed = await SendAsync(host, HttpMethod.Put, "/api/v1/functions/risk_class", new { name = "risk" }, HttpStatusCode.BadRequest);
        Assert.Equal("name", renamed.GetProperty("details").GetProperty("field").GetString());
    }

    private static async Task DashboardFormulas(ApiIntegrationTestHost host)
    {
        var result = await QueryAsync(host, new
        {
            source = "assets",
            groupBy = new[] { new { column = "risk" } },
            measures = new object[]
            {
                new { name = "assets", fn = "count" },
                new { name = "score", formula = "CUSTOM(\"double_it\", SUM(volatility) * 10)" }
            },
            sort = new[] { "-score" }
        });
        Assert.Equal(["high 1 11", "normal 2 10.2"], Rows(result, "risk", "assets", "score"));
        Assert.Equal("score:float", result.GetProperty("meta").GetProperty("columns").EnumerateArray()
            .Select(c => $"{c.GetProperty("name").GetString()}:{c.GetProperty("type").GetString()}").Last());

        var failed = await QueryAsync(host, new
        {
            source = "assets",
            measures = new object[] { new { name = "score", formula = "CUSTOM(\"double_it\", SUM(volatility) * 10000)" } }
        });
        Assert.Equal(["null"], Rows(failed, "score"));
        Assert.Equal("score: double_it failed on 1 of 1 rows — Error: too big (line 1)", failed.GetProperty("meta").GetProperty("warnings")[0].GetString());

        using var nested = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "assets",
            measures = new[] { new { name = "x", formula = "CUSTOM(\"double_it\", SUM(volatility)) + 1" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);
        Assert.Equal("CUSTOM(…) must be the whole formula. Use its result in another computed column or dashboard variable.",
            (await nested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("message").GetString());

        using var running = await host.Client.PostAsJsonAsync("/api/v1/query", new
        {
            source = "assets",
            groupBy = new[] { new { column = "bought_on", bucket = "month" } },
            measures = new[] { new { name = "x", formula = "RUNNING_SUM(CUSTOM(\"double_it\", SUM(volatility)))" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, running.StatusCode);

        // Measures over the stored column work like any other column.
        var stored = await QueryAsync(host, new
        {
            source = "assets",
            measures = new object[] { new { name = "total", formula = "SUM(doubled)" }, new { name = "normal", formula = "COUNT(WHERE risk = \"normal\")" } }
        });
        Assert.Equal(["212 2"], Rows(stored, "total", "normal"));

        var variables = await SendAsync(host, HttpMethod.Post, "/api/v1/query/variables", new
        {
            variables = new object[]
            {
                new { name = "average", source = "assets", formula = "AVG(volatility)" },
                new { name = "twice", formula = "CUSTOM(\"double_it\", average * 100)" },
                new { name = "plus_one", formula = "twice + 1" },
                new { name = "broken", formula = "CUSTOM(\"double_it\", 5000)" }
            }
        }, wrap: true);
        Assert.Equal(["average 0.3533333333 -", "twice 70.66666666 -", "plus_one 71.66666666 -", "broken null Error: too big (line 1)"],
            variables.EnumerateArray().Select(v => $"{v.GetProperty("name").GetString()} {FormulaConformanceTests.Text(v.GetProperty("value"))} {(v.GetProperty("error").ValueKind == JsonValueKind.Null ? "-" : v.GetProperty("error").GetString())}"));

        // The editor validates and previews CUSTOM as typed; the preview runs the function.
        var validated = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/validate", new { table = "assets", formula = "CUSTOM(\"risk_class\", volatility)" });
        Assert.True(validated.GetProperty("valid").GetBoolean());
        Assert.Equal("text", validated.GetProperty("type").GetString());
        var unknown = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/validate", new { table = "assets", formula = "CUSTOM(\"risk_clas\", volatility)" });
        Assert.Equal("There is no custom function 'risk_clas' — did you mean 'risk_class'?", unknown.GetProperty("errors")[0].GetProperty("message").GetString());
        var arity = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/validate", new { table = "assets", formula = "CUSTOM(\"risk_class\")" });
        Assert.Equal("risk_class takes 1 argument (volatility), not 0.", arity.GetProperty("errors")[0].GetProperty("message").GetString());
        var typed = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/validate", new { table = "assets", formula = "CUSTOM(\"risk_class\", ticker)" });
        Assert.Equal("'volatility' of risk_class takes a number, not text.", typed.GetProperty("errors")[0].GetProperty("message").GetString());

        var preview = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/preview", new { table = "assets", formula = "CUSTOM(\"double_it\", volatility * 2000)", limit = 3 });
        Assert.Equal(["1240 -", "null Error: too big (line 1)", "800 -"], preview.GetProperty("rows").EnumerateArray()
            .Select(r => $"{FormulaConformanceTests.Text(r.GetProperty("value"))} {(r.GetProperty("error").ValueKind == JsonValueKind.Null ? "-" : r.GetProperty("error").GetString())}"));
    }

    private static async Task Usages(ApiIntegrationTestHost host)
    {
        var listed = await GetDataAsync(host, "/api/v1/functions");
        Assert.Equal(["double_it", "month_end", "risk_class"], listed.EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        var risk = await GetDataAsync(host, "/api/v1/functions/risk_class");
        Assert.Equal(["column:assets.risk"], risk.GetProperty("usedBy").EnumerateArray().Select(u => $"{u.GetProperty("kind").GetString()}:{u.GetProperty("name").GetString()}"));

        var used = await SendAsync(host, HttpMethod.Delete, "/api/v1/functions/risk_class", null, HttpStatusCode.Conflict);
        Assert.Equal("'risk_class' is used by assets.risk; change those computed columns first.", used.GetProperty("message").GetString());

        await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new { name = "unused", code = "return 1;" }, HttpStatusCode.Created);
        using (var deleted = await host.Client.DeleteAsync("/api/v1/functions/unused"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var gone = await host.Client.GetAsync("/api/v1/functions/unused"))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task CustomFunctions_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await RunAsync(host);
    }

    [Fact]
    public async Task UnknownFunction_BlocksTheSchema()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ApplySchemaAsync(Schema));
        Assert.Contains("There is no custom function 'risk_class'; create one under Functions first.", error.Message);
    }

    // ── Helpers ──

    private static async Task<string[]> AssetsAsync(ApiIntegrationTestHost host) =>
        (await ListAsync(host, "assets?sort=id"))
            .Select(a => string.Join(" ", new[] { "ticker", "risk", "risk_label", "doubled", "month_end" }.Select(c => FormulaConformanceTests.Text(a.GetProperty(c)))))
            .ToArray();

    private static async Task<List<JsonElement>> ListAsync(ApiIntegrationTestHost host, string path) =>
        (await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/data/{path}")).GetProperty("data").EnumerateArray().ToList();

    private static async Task<JsonElement> GetDataAsync(ApiIntegrationTestHost host, string path) =>
        (await host.Client.GetFromJsonAsync<JsonElement>(path)).GetProperty("data");

    private static async Task PostAsync(ApiIntegrationTestHost host, string table, object body)
    {
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/data/{table}", body);
        Assert.True(response.IsSuccessStatusCode, $"{table}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>The response's data, or its error for an expected failure status.</summary>
    private static async Task<JsonElement> SendAsync(ApiIntegrationTestHost host, HttpMethod method, string path, object? body,
        HttpStatusCode expected = HttpStatusCode.OK, bool wrap = false)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        var json = JsonDocument.Parse(text).RootElement.Clone();
        return json.TryGetProperty("error", out var error) ? error : json.GetProperty("data");
    }

    private static async Task<JsonElement> QueryAsync(ApiIntegrationTestHost host, object query)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/query", query);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return body;
    }

    private static string[] Rows(JsonElement result, params string[] columns) =>
        result.GetProperty("data").EnumerateArray()
            .Select(row => string.Join(" ", columns.Select(c => FormulaConformanceTests.Text(row.GetProperty(c)))))
            .ToArray();
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresCustomFunctionTests
{
    [PostgresFact]
    public async Task CustomFunctions_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await CustomFunctionTests.RunAsync(host);
    }
}
