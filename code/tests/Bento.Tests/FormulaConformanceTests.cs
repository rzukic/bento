using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 3.4 conformance suite (Part 5 R5.11): every formula runs on the same
/// four rows on SQLite and on Postgres (<see cref="PostgresFormulaConformanceTests"/>)
/// and must give exactly the expected values — so both providers agree.
/// The rows cover the awkward spots: float noise (0.1 * 3, 0.57 * 100),
/// umlauts, month ends, a leap day, a date-time with an offset, and nulls.
/// </summary>
public sealed class FormulaConformanceTests
{
    internal const string Schema = """
        {
            "tables": [
                {
                    "name": "samples",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "label", "type": "text", "displayName": "Payee name" },
                        { "name": "amount", "type": "currency" },
                        { "name": "ratio", "type": "float" },
                        { "name": "qty", "type": "integer" },
                        { "name": "booked_on", "type": "date" },
                        { "name": "recorded_at", "type": "datetime" },
                        { "name": "paid", "type": "boolean" }
                    ]
                }
            ]
        }
        """;

    internal static async Task SeedAsync(ApiIntegrationTestHost host)
    {
        object[] rows =
        [
            new { label = "REWE Markt", amount = -45.90m, ratio = 0.1, qty = 3, booked_on = "2026-01-31", recorded_at = "2026-01-31T23:30:00Z", paid = true },
            new { label = "Bäckerei Müller", amount = 3000m, ratio = 0.57, qty = -7, booked_on = "2024-02-29", recorded_at = "2026-03-01T00:15:00+02:00", paid = false },
            new { label = "  Ärzte  ", amount = 0.1m, ratio = 2.345, qty = 0, booked_on = "2026-12-15" },
            new { }
        ];
        foreach (var row in rows)
            (await host.Client.PostAsJsonAsync("/api/v1/data/samples", row)).EnsureSuccessStatusCode();
    }

    /// <summary>Formula → result type and the values for rows 1–4.</summary>
    internal static readonly (string Formula, string Type, string[] Values)[] Cases =
    [
        // The acceptance formulas
        ("= amount * -1", "currency", ["45.9", "-3000", "-0.1", "null"]),
        ("= IF(amount < 0, \"expense\", \"income\")", "text", ["expense", "income", "income", "income"]),
        ("= YEAR(booked_on)", "integer", ["2026", "2024", "2026", "null"]),

        // Numbers: float noise, division, rounding
        ("= ratio * 3", "float", ["0.3", "1.71", "7.035", "null"]),
        ("= ratio * 3 = 0.3", "boolean", ["true", "false", "false", "null"]),
        ("= FLOOR(ratio * 100)", "integer", ["10", "57", "234", "null"]),
        ("= CEIL(ratio * 100)", "integer", ["10", "57", "235", "null"]),
        ("= ROUND(ratio, 2)", "float", ["0.1", "0.57", "2.35", "null"]),
        ("= ROUND(amount / 3, 2)", "currency", ["-15.3", "1000", "0.03", "null"]),
        ("= qty / 2", "float", ["1.5", "-3.5", "0", "null"]),
        ("= qty / 0", "float", ["null", "null", "null", "null"]),
        ("= MOD(qty, 2)", "integer", ["1", "-1", "0", "null"]),
        ("= qty % 0", "integer", ["null", "null", "null", "null"]),
        ("= POWER(qty, 2) + SQRT(ABS(qty))", "float", ["10.7320508076", "51.6457513111", "0", "null"]),
        ("= SQRT(qty)", "float", ["1.7320508076", "null", "0", "null"]),
        ("= -2^2 + 7 / 2", "float", ["-0.5", "-0.5", "-0.5", "-0.5"]),
        ("= amount > ratio", "boolean", ["false", "true", "false", "null"]),
        ("= amount = 0.1", "boolean", ["false", "false", "true", "null"]),
        ("= ABS(amount) + qty", "currency", ["48.9", "2993", "0.1", "null"]),

        // Text
        ("= label & \" (\" & qty & \")\"", "text", ["REWE Markt (3)", "Bäckerei Müller (-7)", "  Ärzte   (0)", " ()"]),
        ("= CONCAT(amount, \"|\", ratio, \"|\", paid, \"|\", booked_on)", "text",
            ["-45.9|0.1|TRUE|2026-01-31", "3000|0.57|FALSE|2024-02-29", "0.1|2.345||2026-12-15", "|||"]),
        ("= 1.50 & \"/\" & 'it''s' & \"/\" & \"say \"\"hi\"\"\"", "text", ["1.5/it's/say \"hi\"", "1.5/it's/say \"hi\"", "1.5/it's/say \"hi\"", "1.5/it's/say \"hi\""]),
        ("= UPPER(label)", "text", ["REWE MARKT", "BÄCKEREI MÜLLER", "  ÄRZTE  ", "null"]),
        ("= LOWER(label)", "text", ["rewe markt", "bäckerei müller", "  ärzte  ", "null"]),
        ("= label = \"bäckerei MÜLLER\"", "boolean", ["false", "true", "false", "null"]),
        ("= label < \"c\"", "boolean", ["false", "true", "true", "null"]),
        ("= LEN(TRIM(label))", "integer", ["10", "15", "5", "null"]),
        ("= LEFT(label, 4) & \"/\" & RIGHT(label, 3) & \"/\" & MID(label, 3, 4)", "text",
            ["REWE/rkt/WE M", "Bäck/ler/cker", "  Är/e  /Ärzt", "//"]),
        ("= RIGHT(label, 20)", "text", ["REWE Markt", "Bäckerei Müller", "  Ärzte  ", "null"]),
        ("= LEFT(label, qty)", "text", ["REW", "", "", "null"]),
        ("= RIGHT(label, qty)", "text", ["rkt", "", "", "null"]),
        ("= MID(label, qty, 2)", "text", ["WE", "null", "null", "null"]),
        ("= SEARCH(\"markt\", label)", "integer", ["6", "0", "0", "null"]),
        ("= CONTAINS(label, \"MÜLL\")", "boolean", ["false", "true", "false", "null"]),
        ("= REPLACE(label, \"e\", \"3\")", "text", ["REWE Markt", "Bäck3r3i Müll3r", "  Ärzt3  ", "null"]),

        // Dates and date-times (wall time, UTC)
        ("= DATEADD(booked_on, 1, \"months\")", "date", ["2026-02-28", "2024-03-29", "2027-01-15", "null"]),
        ("= DATEADD(booked_on, 1, \"years\")", "date", ["2027-01-31", "2025-02-28", "2027-12-15", "null"]),
        ("= DATEADD(booked_on, -2, \"weeks\")", "date", ["2026-01-17", "2024-02-15", "2026-12-01", "null"]),
        ("= booked_on + 30", "date", ["2026-03-02", "2024-03-30", "2027-01-14", "null"]),
        ("= booked_on - DATEADD(booked_on, -10, \"days\")", "integer", ["10", "10", "10", "null"]),
        ("= DATEDIFF(booked_on, \"2026-12-31\", \"days\")", "integer", ["334", "1036", "16", "null"]),
        ("= DATEDIFF(booked_on, \"2026-12-31\", \"months\")", "integer", ["11", "34", "0", "null"]),
        ("= DATEDIFF(\"2026-12-31\", booked_on, \"months\")", "integer", ["-11", "-34", "0", "null"]),
        ("= DATEDIFF(booked_on, \"2026-12-31\", \"years\")", "integer", ["0", "2", "0", "null"]),
        ("= DATEDIFF(booked_on, DATEADD(booked_on, 1, \"months\"), \"months\")", "integer", ["0", "1", "1", "null"]),
        ("= recorded_at", "datetime", ["2026-01-31T23:30:00Z", "2026-02-28T22:15:00Z", "null", "null"]),
        ("= HOUR(recorded_at) & \":\" & MINUTE(recorded_at) & \" \" & DAY(recorded_at)", "text", ["23:30 31", "22:15 28", ": ", ": "]),
        ("= DATEADD(recorded_at, 1, \"days\")", "datetime", ["2026-02-01T23:30:00Z", "2026-03-01T22:15:00Z", "null", "null"]),
        ("= recorded_at < \"2026-02-01\"", "boolean", ["true", "false", "null", "null"]),
        ("= recorded_at > booked_on", "boolean", ["true", "true", "null", "null"]),
        ("= booked_on >= \"2026-01-31\"", "boolean", ["true", "false", "true", "null"]),
        ("= booked_on & \" \" & recorded_at", "text", ["2026-01-31 2026-01-31 23:30:00", "2024-02-29 2026-02-28 22:15:00", "2026-12-15 ", " "]),
        ("= YEAR(NOW()) >= 2026 AND TODAY() <= DATEADD(TODAY(), 1, \"days\")", "boolean", ["true", "true", "true", "true"]),

        // Logic and empty values
        ("= SWITCH(MONTH(booked_on), 1, \"Jan\", 2, \"Feb\", \"other\")", "text", ["Jan", "Feb", "other", "other"]),
        ("= SWITCH(label, \"rewe markt\", 1, \"  ärzte  \", 2)", "integer", ["1", "null", "2", "null"]),
        ("= COALESCE(label, \"unknown\")", "text", ["REWE Markt", "Bäckerei Müller", "  Ärzte  ", "unknown"]),
        ("= ISBLANK(label) & \"/\" & ISBLANK(paid)", "text", ["FALSE/FALSE", "FALSE/FALSE", "FALSE/TRUE", "TRUE/TRUE"]),
        ("= IF(paid, amount, 0)", "currency", ["-45.9", "0", "0", "0"]),
        ("= NOT paid", "boolean", ["false", "true", "null", "null"]),
        ("= AND(amount < 0, paid) OR qty = 0", "boolean", ["true", "false", "true", "null"]),
        ("= paid = TRUE || paid == FALSE", "boolean", ["true", "true", "null", "null"]),
        ("= qty <> NULL", "boolean", ["true", "true", "true", "false"]),
        ("= IF(qty > 0, booked_on, \"2000-01-01\")", "date", ["2026-01-31", "2000-01-01", "2000-01-01", "2000-01-01"]),
        ("= [Payee name] & \"!\"", "text", ["REWE Markt!", "Bäckerei Müller!", "  Ärzte  !", "!"]),
    ];

    internal static async Task RunCasesAsync(ApiIntegrationTestHost host)
    {
        var failures = new List<string>();
        foreach (var (formula, type, expected) in Cases)
        {
            using var response = await host.Client.PostAsJsonAsync("/api/v1/formulas/preview", new { table = "samples", formula });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!response.IsSuccessStatusCode)
            {
                failures.Add($"{formula}: {(int)response.StatusCode} {body}");
                continue;
            }
            var data = body.GetProperty("data");
            var actualType = data.GetProperty("type").GetString();
            var values = data.GetProperty("rows").EnumerateArray().Select(r => Text(r.GetProperty("value"))).ToArray();
            if (actualType != type || !values.SequenceEqual(expected))
                failures.Add($"{formula}: expected {type} [{string.Join(", ", expected)}], got {actualType} [{string.Join(", ", values)}]");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    internal static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    [Fact]
    public async Task Cases_OnSqlite_GiveTheExpectedValues()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(Schema);
        await SeedAsync(host);

        await RunCasesAsync(host);
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresFormulaConformanceTests
{
    [PostgresFact]
    public async Task Cases_OnPostgres_GiveTheSameValues()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(FormulaConformanceTests.Schema);
        await FormulaConformanceTests.SeedAsync(host);

        await FormulaConformanceTests.RunCasesAsync(host);
    }
}
