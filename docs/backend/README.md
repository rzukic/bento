# Backend

> ASP.NET Core API server — structure, patterns, and key subsystems.
> Tech decision rationale: [`wiki/plan/tech-decisions.md`](../../wiki/plan/tech-decisions.md).

---

## Project Structure

```
src/backend/
├── Controllers/          ← API route handlers (one per resource area)
├── Services/             ← Business logic, injected into controllers
├── Db/
│   ├── IDbPool.cs        ← Pool interfaces (ISystemPool, IDataPool)
│   ├── DatabaseConfig.cs ← Configuration model (provider, connections, pool sizing)
│   ├── SystemPool.cs     ← Privileged connection pool (_system schema)
│   ├── DataPool.cs       ← Restricted connection pool (public schema)
│   ├── DatabaseBootstrap.cs ← Creates system tables on first run (IHostedService)
│   ├── QueryService.cs   ← Scoped QueryFactory provider (SqlKata + Dapper)
│   ├── ColumnType.cs     ← Column type enum, DDL mappings, coercion matrix, validation
│   ├── Schema/
│   │   ├── ModelJson.cs  ← Model JSON type hierarchy (tables, columns, relations, indexes)
│   │   ├── ModelJsonJsonConverters.cs ← Custom JSON converters for schema enums
│   │   ├── ModelJsonSerializer.cs ← Pretty-printing and round-trip parsing helpers
│   │   ├── ModelJsonStore.cs ← Versioned persistence in `_system.schema_versions`
│   │   ├── SchemaValidator.cs ← Validation + normalization before diffing/migrations
│   │   └── RelationResolver.cs ← Relation and junction-table logic (Step 1.2)
│   ├── DdlBlocklist.cs   ← SQL statement type checker (Step 1.5)
│   └── Migrations/       ← Database migration engine (Step 1.2)
├── Formula/
│   ├── Evaluator.cs      ← Tier dispatch and evaluation orchestration (Phase 3)
│   ├── SqlTier.cs        ← Tier 1: inline SQL computed columns
│   ├── MaterializedTier.cs ← Tier 2: dirty flag + sync materialization
│   ├── AsyncTier.cs      ← Tier 3: background worker + WebSocket push
│   └── Sandbox/
│       └── JintSandbox.cs ← Jint JS interpreter with resource limits (Phase 4)
├── Workers/              ← Background services (dirty flag processor, etc.)
├── Hubs/                 ← SignalR WebSocket hubs
├── Models/               ← DTOs and domain models
└── Program.cs            ← Entry point, DI registration, middleware pipeline
```

---

## Key Subsystems

| Subsystem | Entry point | Doc |
|-----------|-------------|-----|
| Database access | `Db/SystemPool.cs`, `Db/DataPool.cs` | [database.md](database.md) |
| Schema model, validator & version store | `Db/Schema/ModelJson.cs`, `Db/Schema/SchemaValidator.cs`, `Db/Schema/ModelJsonStore.cs` | [`../../wiki/parts/01-core-database/README.md`](../../wiki/parts/01-core-database/README.md) |
| Formula evaluation | `Formula/Evaluator.cs` | [formula-engine.md](formula-engine.md) |
| JS sandbox | `Formula/Sandbox/JintSandbox.cs` | [formula-engine.md](formula-engine.md) |
| Background workers | `Workers/` | [background-workers.md](background-workers.md) |
| WebSocket push | `Hubs/` | [background-workers.md](background-workers.md) |

---

## Conventions

### Controllers

Thin. Route validation, auth check, call service, return result. No business logic.

```csharp
[ApiController]
[Route("api/v1/tables")]
public class TablesController : ControllerBase
{
    private readonly ITableService _tables;

    public TablesController(ITableService tables) => _tables = tables;

    [HttpGet("{tableId}/rows")]
    public async Task<IActionResult> GetRows(string tableId, [FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        var rows = await _tables.GetRowsAsync(tableId, limit, offset);
        return Ok(rows);
    }
}
```

### Services

All business logic lives here. Injected as interfaces for testability.

### Query builder

SqlKata is used for dynamic query construction. Dapper is used for raw SQL execution. Do not use Entity Framework.

```csharp
// Dynamic query — use SqlKata
var query = new Query("users").Where("active", true).Limit(100);

// Raw SQL — use Dapper via pool
var results = await _dataPool.QueryAsync<UserRow>("SELECT * FROM users WHERE id = @id", new { id });
```

### Error handling

Use a global exception middleware. Controllers do not catch exceptions — let them propagate.

### Schema engine

Always validate parsed Model JSON before diffing or generating SQL. `SchemaValidator` is the first gate and is responsible for:
- collecting blocking errors with stable codes and paths
- collecting non-blocking warnings for risky-but-allowed names
- normalizing tables by auto-adding an integer `id` primary key when one is missing

---

## Authentication

All endpoints except `/api/v1/auth/*` require authentication.

Two auth methods are supported:
1. **JWT Bearer token** — for interactive sessions
2. **API key** — for programmatic/external access (header: `X-API-Key`)

Auth middleware checks both and sets `HttpContext.User` accordingly. Controllers access the current user via `User.GetUserId()` extension.

> **Not yet implemented** — API key auth planned for Phase 1.
