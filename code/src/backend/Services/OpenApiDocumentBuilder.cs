using System.Text.Json.Nodes;
using Bento.Api.Db;
using Bento.Api.Db.Schema;

namespace Bento.Api.Services;

/// <summary>
/// The OpenAPI 3.0 description of this installation's API (Part 6 R6.8),
/// built from the current schema: every table gets its CRUD endpoints with
/// its own fields, types, required fields and examples, next to the fixed
/// routes (query, saved queries, dashboards, import, schema, auth, health).
/// Each operation carries <c>x-bento-scope</c>: the API key scope that may
/// call it (read, write, or none for sign-in only).
/// </summary>
public sealed class OpenApiDocumentBuilder
{
    private readonly SchemaCache _schemaCache;
    private readonly BackendRuntimeInfo _runtimeInfo;

    public OpenApiDocumentBuilder(SchemaCache schemaCache, BackendRuntimeInfo runtimeInfo)
    {
        _schemaCache = schemaCache;
        _runtimeInfo = runtimeInfo;
    }

    public JsonObject Build()
    {
        var tables = _schemaCache.GetTableNames()
            .Select(_schemaCache.GetTable)
            .OfType<TableDef>()
            .OrderBy(t => t.Name.StartsWith("_jt_", StringComparison.Ordinal))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var paths = new JsonObject();
        var schemas = new JsonObject
        {
            ["Error"] = Obj(new JsonObject
            {
                ["error"] = Obj(new JsonObject
                {
                    ["code"] = new JsonObject { ["type"] = "string", ["example"] = "VALIDATION_FAILED" },
                    ["message"] = new JsonObject { ["type"] = "string" },
                    ["details"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
                })
            }),
            ["ListMeta"] = Obj(new JsonObject
            {
                ["page"] = Int(1), ["per_page"] = Int(25), ["total"] = Int(120), ["total_pages"] = Int(5)
            }),
            ["AggregationQuery"] = AggregationQuerySchema(tables),
            ["QueryResult"] = Obj(new JsonObject
            {
                ["data"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true } },
                ["meta"] = Obj(new JsonObject
                {
                    ["columns"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = Obj(new JsonObject
                        {
                            ["name"] = Str(), ["type"] = Str(), ["role"] = Str(["group", "label", "measure"])
                        })
                    },
                    ["truncated"] = new JsonObject { ["type"] = "boolean" }
                })
            })
        };

        var tags = new JsonArray();
        foreach (var table in tables)
        {
            var tag = Tag(table);
            tags.Add(new JsonObject
            {
                ["name"] = tag,
                ["description"] = table.Name.StartsWith("_jt_", StringComparison.Ordinal)
                    ? $"Links between records (N:M junction `{table.Name}`)."
                    : table.Description ?? $"Records of `{table.Name}`."
            });
            AddTable(paths, schemas, table, tag);
        }

        foreach (var (name, description) in new[]
        {
            ("Query", "Aggregate a table without SQL: groups, date buckets, sums, running totals."),
            ("Saved queries", "Named aggregation queries, private or shared."),
            ("Formulas", "Check spreadsheet-style formulas against a table and preview their values."),
            ("Functions", "Your own JavaScript functions, called from formulas with CUSTOM(…)."),
            ("Prices", "Price sources: your functions fetch prices into a table of your model."),
            ("Dashboards", "Dashboard JSON: widgets, layout, date range."),
            ("Import", "CSV import with locale-aware parsing and duplicate skip."),
            ("Schema", "The data model (Model JSON)."),
            ("Auth", "Sign in for a session token. Scripts use API keys instead."),
            ("Health", "Liveness probe.")
        })
        {
            tags.Add(new JsonObject { ["name"] = name, ["description"] = description });
        }
        AddFixedRoutes(paths, tables);

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = "Bento API",
                ["version"] = _runtimeInfo.Version,
                ["description"] =
                    "The API of this Bento installation, generated from its current schema. " +
                    "Authenticate with an API key (`X-API-Key: bento_…` or `Authorization: Bearer bento_…`) " +
                    "or a session token from `POST /auth/login`. Read keys may call operations marked `read`; " +
                    "read-write keys also `write`. Errors use `{ \"error\": { \"code\", \"message\", \"details\" } }`."
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" + ApiRoutes.Prefix }),
            ["tags"] = tags,
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["apiKey"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-API-Key" },
                    ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "An API key or a session access token." }
                },
                ["schemas"] = schemas
            },
            ["security"] = new JsonArray(new JsonObject { ["apiKey"] = new JsonArray() }, new JsonObject { ["bearer"] = new JsonArray() })
        };
    }

    // ── Tables ────────────────────────────────────────────────────────────

    private static string Tag(TableDef table) =>
        table.Name.StartsWith("_jt_", StringComparison.Ordinal) ? $"Links · {table.Name}" : $"Data · {table.DisplayName ?? table.Name}";

    private static string SchemaName(TableDef table) =>
        string.Concat(table.Name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static void AddTable(JsonObject paths, JsonObject schemas, TableDef table, string tag)
    {
        var name = SchemaName(table);
        var columns = table.Columns.ToList();
        var record = new JsonObject();
        var input = new JsonObject();
        var required = new JsonArray();
        foreach (var column in columns)
        {
            var property = ColumnSchema(column);
            record[column.Name] = property.DeepClone();
            var writable = CrudService.IsWritableColumn(column) && column.Type != ColumnType.Computed;
            if (!writable)
            {
                ((JsonObject)record[column.Name]!)["readOnly"] = true;
                continue;
            }
            input[column.Name] = property;
            if (!column.Nullable && column.DefaultValue is null && column.InternalDefaultSql is null)
                required.Add(column.Name);
        }
        schemas[name] = new JsonObject { ["type"] = "object", ["properties"] = record };
        schemas[$"{name}Input"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = input,
            ["required"] = required
        };

        var refRecord = Ref(name);
        var refInput = Ref($"{name}Input");
        var display = table.DisplayName ?? table.Name;
        var basePath = $"/data/{table.Name}";
        var filterColumns = string.Join(", ", columns.Where(c => ColumnTypes.HasPhysicalColumn(c.Type)).Select(c => $"`{c.Name}`"));

        paths[basePath] = new JsonObject
        {
            ["get"] = Operation(tag, $"List {display}", "read",
                $"Paginated, filterable and sortable. Filter columns: {filterColumns}.",
                parameters: new JsonArray(
                    QueryParam("page", "Page number, from 1.", Int(1)),
                    QueryParam("per_page", "Rows per page (max 1000).", Int(25)),
                    QueryParam("sort", "Column names, `-` for descending, comma-separated.", Str(example: "-id")),
                    QueryParam("search", "Text search across text columns and linked records' names.", Str()),
                    QueryParam("fields", "Only these columns, comma-separated.", Str()),
                    QueryParam("include", "Embed related records by accessor, comma-separated.", Str()),
                    new JsonObject
                    {
                        ["name"] = "filter",
                        ["in"] = "query",
                        ["style"] = "deepObject",
                        ["explode"] = true,
                        ["description"] = "`filter[column]=value` or `filter[column][op]=value`; op is eq, neq, gt, gte, lt, lte, like, in (comma-separated), null, notnull.",
                        ["schema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
                    }),
                responses: Responses(200, "The page of records.", Obj(new JsonObject
                {
                    ["data"] = new JsonObject { ["type"] = "array", ["items"] = refRecord.DeepClone() },
                    ["meta"] = Ref("ListMeta")
                }))),
            ["post"] = Operation(tag, $"Create a {display} record", "write", "Fields as in the input schema; required fields must be present.",
                body: refInput.DeepClone(),
                responses: Responses(201, "The created record.", Obj(new JsonObject { ["data"] = refRecord.DeepClone() })))
        };

        var idParam = new JsonObject
        {
            ["name"] = "id",
            ["in"] = "path",
            ["required"] = true,
            ["description"] = table.Columns.Count(c => c.PrimaryKey) > 1
                ? "The key values joined with `,` in key order."
                : "The record's id.",
            ["schema"] = Str(example: "1")
        };
        paths[$"{basePath}/{{id}}"] = new JsonObject
        {
            ["get"] = Operation(tag, $"Get a {display} record", "read", null, parameters: new JsonArray(idParam.DeepClone()),
                responses: Responses(200, "The record.", Obj(new JsonObject { ["data"] = refRecord.DeepClone() }))),
            ["put"] = Operation(tag, $"Replace a {display} record", "write", "All required fields.", parameters: new JsonArray(idParam.DeepClone()),
                body: refInput.DeepClone(),
                responses: Responses(200, "The record.", Obj(new JsonObject { ["data"] = refRecord.DeepClone() }))),
            ["patch"] = Operation(tag, $"Change fields of a {display} record", "write", "Only the fields to change.", parameters: new JsonArray(idParam.DeepClone()),
                body: new JsonObject { ["type"] = "object", ["properties"] = input.DeepClone() },
                responses: Responses(200, "The record.", Obj(new JsonObject { ["data"] = refRecord.DeepClone() }))),
            ["delete"] = Operation(tag, $"Delete a {display} record", "write", null, parameters: new JsonArray(idParam.DeepClone()),
                responses: new JsonObject { ["204"] = new JsonObject { ["description"] = "Deleted." }, ["default"] = ErrorResponse() })
        };
    }

    private static JsonObject ColumnSchema(ColumnDef column)
    {
        JsonObject schema = column.Type switch
        {
            ColumnType.Integer => new JsonObject { ["type"] = "integer", ["format"] = "int64", ["example"] = 1 },
            ColumnType.Float => new JsonObject { ["type"] = "number", ["format"] = "double", ["example"] = 12.5 },
            ColumnType.Currency => new JsonObject { ["type"] = "number", ["example"] = 19.99, ["description"] = "Amount" },
            ColumnType.Boolean => new JsonObject { ["type"] = "boolean", ["example"] = true },
            ColumnType.Date => new JsonObject { ["type"] = "string", ["format"] = "date", ["example"] = "2026-10-04" },
            ColumnType.DateTime => new JsonObject { ["type"] = "string", ["format"] = "date-time", ["example"] = "2026-10-04T12:00:00" },
            ColumnType.Json => new JsonObject { ["description"] = "Any JSON value", ["example"] = new JsonObject() },
            ColumnType.Reference => new JsonObject
            {
                ["type"] = "integer",
                ["example"] = 1,
                ["description"] = $"Id of a `{column.References}` record"
            },
            ColumnType.Enum => new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(column.Values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["example"] = column.Values.FirstOrDefault() ?? ""
            },
            ColumnType.Uuid => new JsonObject { ["type"] = "string", ["format"] = "uuid", ["example"] = "3fa85f64-5717-4562-b3fc-2c963f66afa6" },
            ColumnType.Computed => new JsonObject { ["description"] = "Computed from a formula" },
            _ => new JsonObject { ["type"] = "string", ["example"] = column.DisplayName ?? column.Name }
        };
        if (column.Nullable && column.Type != ColumnType.Computed)
            schema["nullable"] = true;
        var description = column.Description ?? (column.DisplayName is not null && column.DisplayName != column.Name ? column.DisplayName : null);
        if (description is not null)
            schema["description"] = schema["description"] is { } existing ? $"{description} — {existing}" : description;
        return schema;
    }

    // ── Fixed routes ──────────────────────────────────────────────────────

    private static void AddFixedRoutes(JsonObject paths, List<TableDef> tables)
    {
        var anyObject = new JsonObject { ["type"] = "object", ["additionalProperties"] = true };
        var firstTable = tables.FirstOrDefault(t => !t.Name.StartsWith("_jt_", StringComparison.Ordinal))?.Name ?? "table";
        var idParam = new JsonObject { ["name"] = "id", ["in"] = "path", ["required"] = true, ["schema"] = Int(1) };
        var tableParam = new JsonObject
        {
            ["name"] = "table",
            ["in"] = "path",
            ["required"] = true,
            ["schema"] = Str(tables.Where(t => !t.Name.StartsWith("_jt_", StringComparison.Ordinal)).Select(t => t.Name).ToArray(), firstTable)
        };

        paths["/query"] = new JsonObject
        {
            ["post"] = Operation("Query", "Run an aggregation query", "read",
                "Groups by columns, linked records or date buckets; measures count, sum, avg, min, max, count_distinct, median, percentile (`percentile`: 0–100) or a `formula` (`SUM(amount WHERE amount > 0)`, `RUNNING_SUM(…)`, returns: `XIRR(amount, date)`, `TWR(value, flow, 100)` with `asOf`, `REBASE(measure)`); `running` and `carryIn` for running totals; `asOf` for values at the end of each period (BUCKET_END()).",
                body: Ref("AggregationQuery"),
                responses: Responses(200, "Rows and column metadata.", Ref("QueryResult")))
        };
        paths["/query/variables"] = new JsonObject
        {
            ["post"] = Operation("Query", "Evaluate dashboard variables", "read",
                "Each variable is a measure formula over a `source` table (with an optional `filter`) or a formula over other variables; they are evaluated in dependency order. A broken variable gets an `error`, the others still get values.",
                body: Obj(new JsonObject
                {
                    ["variables"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = Obj(new JsonObject { ["name"] = Str(example: "burn_rate"), ["source"] = Str(), ["formula"] = Str(example: "spent / 3"), ["filter"] = anyObject.DeepClone() })
                    }
                }),
                responses: Responses(200, "Name, type, value or error of each variable.", anyObject.DeepClone()))
        };
        paths["/queries"] = new JsonObject
        {
            ["get"] = Operation("Saved queries", "List saved queries", "read", "Own queries and shared ones.",
                parameters: new JsonArray(QueryParam("source", "Only queries on this table.", Str())),
                responses: Responses(200, "Saved queries.", anyObject.DeepClone())),
            ["post"] = Operation("Saved queries", "Save a query", "write", null,
                body: Obj(new JsonObject { ["name"] = Str(example: "Monthly spending"), ["isShared"] = new JsonObject { ["type"] = "boolean" }, ["query"] = Ref("AggregationQuery") }),
                responses: Responses(201, "The saved query.", anyObject.DeepClone()))
        };
        paths["/queries/{id}/run"] = new JsonObject
        {
            ["post"] = Operation("Saved queries", "Run a saved query", "read", "Extra filters are added to the saved ones.",
                parameters: new JsonArray(idParam.DeepClone()),
                body: Obj(new JsonObject { ["filter"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true, ["example"] = new JsonObject() } }),
                responses: Responses(200, "Rows and column metadata.", Ref("QueryResult")))
        };
        var formulaBody = Obj(new JsonObject
        {
            ["table"] = (tableParam["schema"] as JsonObject)!.DeepClone(),
            ["formula"] = Str(example: "= IF(amount < 0, \"expense\", \"income\")"),
            ["resultType"] = Str(["text", "integer", "float", "currency", "boolean", "date", "datetime"]),
            ["limit"] = Int(20),
            ["columns"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Validate only: draft column definitions (Model JSON) to check against instead of the applied table.",
                ["items"] = anyObject.DeepClone()
            },
            ["model"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Validate only: a whole draft Model JSON (tables and relations) to check against.",
                ["additionalProperties"] = true
            },
            ["column"] = Str(example: "balance")
        });
        paths["/formulas/validate"] = new JsonObject
        {
            ["post"] = Operation("Formulas", "Validate a formula", "read",
                "Always 200: `valid`, the result `type`, `errors` with character positions, and the `columns` it uses. `limit` is ignored.",
                body: formulaBody.DeepClone(),
                responses: Responses(200, "The check result.", anyObject.DeepClone()))
        };
        paths["/formulas/preview"] = new JsonObject
        {
            ["post"] = Operation("Formulas", "Preview a formula's values", "read",
                "Computes the formula on the first `limit` rows (1–100) and returns each value with the column values it used.",
                body: formulaBody.DeepClone(),
                responses: Responses(200, "Sample values.", anyObject.DeepClone()))
        };
        paths["/formulas/functions"] = new JsonObject
        {
            ["get"] = Operation("Formulas", "List formula functions", "read", "Name, signature, description and example of each function.",
                responses: Responses(200, "The function catalog.", anyObject.DeepClone()))
        };
        paths["/functions"] = new JsonObject
        {
            ["get"] = Operation("Functions", "List custom functions", "read",
                "Your own JavaScript functions, called from formulas with `CUSTOM(\"name\", …)` — or `CUSTOM_ROWS(\"name\", PARTITION(…), ORDER(…), …)` for those with mode `rows`: arguments, result type, mode, code, version and where each is used. Admins create and change them in the app.",
                responses: Responses(200, "The custom functions.", anyObject.DeepClone()))
        };
        paths["/functions/{name}"] = new JsonObject
        {
            ["get"] = Operation("Functions", "Get a custom function", "read", null,
                parameters: new JsonArray(new JsonObject { ["name"] = "name", ["in"] = "path", ["required"] = true, ["schema"] = Str(example: "risk_class") }),
                responses: Responses(200, "The function.", anyObject.DeepClone()))
        };
        paths["/functions/status"] = new JsonObject
        {
            ["get"] = Operation("Functions", "Stored values of computed columns", "read",
                "Computed columns that call a function store their values and recalculate them in the background: whether a run is `pending`, when the last ran, and the columns whose function failed on some rows.",
                responses: Responses(200, "The recalculation status.", anyObject.DeepClone()))
        };
        paths["/functions/templates"] = new JsonObject
        {
            ["get"] = Operation("Functions", "List function templates", "read",
                "Starting points for new functions, e.g. prices from Yahoo Finance, Xetra symbols for ISINs, or realised gains by average cost and FIFO (mode `rows`).",
                responses: Responses(200, "The templates.", anyObject.DeepClone()))
        };
        paths["/price-sources"] = new JsonObject
        {
            ["get"] = Operation("Prices", "List price sources", "read",
                "Each source with its function, target table and columns, the symbol per key, the latest price per key and its status (last quote, last error, requests today).",
                responses: Responses(200, "The price sources.", anyObject.DeepClone()))
        };
        paths["/price-sources/refresh"] = new JsonObject
        {
            ["post"] = Operation("Prices", "Refresh stale prices", "read",
                "Runs every enabled source writing to one of `tables` (all when empty) whose last fetch is older than its freshness window, as a dashboard does when it opens. `changed` tells whether any price was written.",
                body: Obj(new JsonObject
                {
                    ["tables"] = new JsonObject { ["type"] = "array", ["items"] = Str(example: "finance_prices") }
                }),
                responses: Responses(200, "Whether prices changed, and each source's state.", anyObject.DeepClone()))
        };
        paths["/dashboards"] = new JsonObject
        {
            ["get"] = Operation("Dashboards", "List dashboards", "read", "Own dashboards and shared ones.",
                responses: Responses(200, "Dashboards with their Dashboard JSON.", anyObject.DeepClone()))
        };
        paths["/dashboards/{id}"] = new JsonObject
        {
            ["get"] = Operation("Dashboards", "Get a dashboard", "read", null, parameters: new JsonArray(idParam.DeepClone()),
                responses: Responses(200, "The dashboard.", anyObject.DeepClone()))
        };
        var importBody = Obj(new JsonObject
        {
            ["content"] = Str(example: "base64 of the file"),
            ["settings"] = anyObject.DeepClone(),
            ["mapping"] = new JsonObject { ["type"] = "array", ["items"] = Obj(new JsonObject { ["source"] = Str(), ["target"] = Str(), ["value"] = Str(example: "Checking") }) },
            ["keyColumns"] = new JsonObject { ["type"] = "array", ["items"] = Str() },
            ["distinctKeys"] = new JsonObject { ["type"] = "boolean" }
        });
        paths["/import/analyze"] = new JsonObject
        {
            ["post"] = Operation("Import", "Read a CSV file", "write", "Detects encoding, separator, header and number/date format.",
                body: Obj(new JsonObject { ["content"] = Str(example: "base64 of the file") }),
                responses: Responses(200, "Settings, columns and samples.", anyObject.DeepClone()))
        };
        paths["/import/{table}/preview"] = new JsonObject
        {
            ["post"] = Operation("Import", "Check a CSV import", "write", "Nothing is written.", parameters: new JsonArray(tableParam.DeepClone()),
                body: importBody.DeepClone(), responses: Responses(200, "Rows by status.", anyObject.DeepClone()))
        };
        paths["/import/{table}"] = new JsonObject
        {
            ["post"] = Operation("Import", "Import a CSV file", "write", "New, valid rows in one transaction.", parameters: new JsonArray(tableParam.DeepClone()),
                body: importBody.DeepClone(), responses: Responses(200, "Counts and rows by status.", anyObject.DeepClone()))
        };
        paths["/admin/schema"] = new JsonObject
        {
            ["get"] = Operation("Schema", "Get the data model", "read", "Model JSON with tables, columns and relations.",
                responses: Responses(200, "The model.", anyObject.DeepClone()))
        };
        paths["/auth/login"] = new JsonObject
        {
            ["post"] = Operation("Auth", "Sign in", "none", "Returns a short-lived access token; for scripts prefer an API key.",
                body: Obj(new JsonObject { ["username"] = Str(example: "admin"), ["password"] = new JsonObject { ["type"] = "string", ["format"] = "password" } }),
                responses: Responses(200, "Access token and user.", anyObject.DeepClone()), secured: false)
        };
        paths["/admin/health"] = new JsonObject
        {
            ["get"] = Operation("Health", "Check that Bento is up", "none", null,
                responses: Responses(200, "Status.", anyObject.DeepClone()), secured: false)
        };
    }

    private static JsonObject AggregationQuerySchema(List<TableDef> tables) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("source"),
        ["properties"] = new JsonObject
        {
            ["source"] = Str(tables.Select(t => t.Name).ToArray(), tables.FirstOrDefault()?.Name),
            ["filter"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = true,
                ["description"] = "{ column: value } or { column: { op: value } }"
            },
            ["groupBy"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = Obj(new JsonObject
                {
                    ["column"] = Str(),
                    ["bucket"] = Str(["day", "week", "month", "quarter", "year"]),
                    ["as"] = Str(),
                    ["top"] = Int(10),
                    ["other"] = new JsonObject { ["type"] = "boolean", ["description"] = "With top: the remaining values as one group, flagged in <as>_other." },
                    ["rank"] = Str(["desc", "asc", "abs"])
                })
            },
            ["measures"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = Obj(new JsonObject
                {
                    ["name"] = Str(),
                    ["fn"] = Str(["count", "sum", "avg", "min", "max", "count_distinct", "median", "percentile"]),
                    ["column"] = Str(),
                    ["percentile"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 100, ["description"] = "With fn percentile, e.g. 90" },
                    ["formula"] = Str(example: "SUM(amount WHERE amount > 0) / COUNT()"),
                    ["running"] = new JsonObject { ["type"] = "boolean" },
                    ["carryIn"] = new JsonObject { ["type"] = "boolean" }
                })
            },
            ["sort"] = new JsonObject { ["type"] = "array", ["items"] = Str() },
            ["limit"] = Int(1000),
            ["asOf"] = Obj(new JsonObject
            {
                ["bucket"] = Str(["day", "week", "month", "quarter", "year"]),
                ["from"] = Str(example: "2026-01-01"),
                ["to"] = Str(example: "2026-12-31"),
                ["as"] = Str(example: "month")
            }),
            ["variables"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = true,
                ["description"] = "Dashboard variables used in measure formulas: { name: number | text | boolean }"
            }
        },
        ["example"] = new JsonObject
        {
            ["source"] = tables.FirstOrDefault(t => !t.Name.StartsWith("_jt_", StringComparison.Ordinal))?.Name ?? "table",
            ["measures"] = new JsonArray(new JsonObject { ["name"] = "rows", ["fn"] = "count" })
        }
    };

    // ── Helpers ───────────────────────────────────────────────────────────

    private static JsonObject Operation(
        string tag,
        string summary,
        string scope,
        string? description,
        JsonArray? parameters = null,
        JsonNode? body = null,
        JsonObject? responses = null,
        bool secured = true)
    {
        var operation = new JsonObject
        {
            ["tags"] = new JsonArray(tag),
            ["summary"] = summary,
            ["x-bento-scope"] = scope
        };
        if (description is not null)
            operation["description"] = description;
        if (parameters is not null)
            operation["parameters"] = parameters;
        if (body is not null)
        {
            operation["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = body } }
            };
        }
        operation["responses"] = responses ?? new JsonObject { ["200"] = new JsonObject { ["description"] = "OK" } };
        if (!secured)
            operation["security"] = new JsonArray();
        return operation;
    }

    private static JsonObject Responses(int status, string description, JsonNode schema) => new()
    {
        [status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
        {
            ["description"] = description,
            ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } }
        },
        ["default"] = ErrorResponse()
    };

    private static JsonObject ErrorResponse() => new()
    {
        ["description"] = "Error",
        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref("Error") } }
    };

    private static JsonObject QueryParam(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["in"] = "query",
        ["description"] = description,
        ["schema"] = schema
    };

    private static JsonObject Ref(string name) => new() { ["$ref"] = $"#/components/schemas/{name}" };

    private static JsonObject Obj(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties };

    private static JsonObject Int(long example) => new() { ["type"] = "integer", ["example"] = example };

    private static JsonObject Str(string[]? values = null, string? example = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (values is { Length: > 0 })
            schema["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        if (example is not null)
            schema["example"] = example;
        return schema;
    }
}
