# Connections

**Connections** in the sidebar is where Bento meets scripts, spreadsheets and other tools: the API of your installation, explained and testable, the API keys those tools use, and **Claude** over MCP.

## The API Page

The page lists every endpoint of your installation, generated from your current schema — each table has its own list, get, create, replace, change and delete endpoints with its own fields. Endpoints are grouped by table and area (query, saved queries, dashboards, import, schema); the search field narrows the list.

Open an endpoint to see:

- what it does, its parameters and, for writes, the fields of the body (required ones marked)
- which keys may call it: **Read keys**, **Read-write keys**, or **No key needed**
- **Send**: try it right there, as yourself (your session) or with an API key; the answer shows status, time and body
- the matching **curl** command, with the key as `$BENTO_API_KEY` so it never ends up in your shell history

Filters on list endpoints take `column=value&column[op]=value`, e.g. `betrag_eur[lt]=0&buchungstag[gte]=2026-09-01`; operators are `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `in`, `null`, `notnull`.

**Download openapi.json** saves the description (OpenAPI 3.0) for other tools — Postman, Insomnia, Bruno, code generators or an AI assistant.

## API Keys

Admins create keys under **API keys**:

1. Give the key a name (what uses it), choose **Read** or **Read and write**, and optionally an expiry
2. **Create key** shows the key **once** — copy it now; Bento keeps only a fingerprint and cannot show it again
3. Use it in the `X-API-Key` header (or `Authorization: Bearer bento_…`):

   ```bash
   curl "http://localhost/api/v1/data/girokonto?per_page=10" -H "X-API-Key: $BENTO_API_KEY"
   ```

| Access | Allows |
|--------|--------|
| Read | Reading records, the schema and dashboards; running queries |
| Read and write | Also creating, changing and deleting records, importing files, saving queries and dashboards |

No key can change the schema, manage users or manage keys — that needs a sign-in. A key acts as the admin who created it and never does more than that user may.

The list shows when each key was created and last used, when it expires, and its status. **Revoke** stops a key immediately; it stays in the list as revoked. To replace a key, create a new one and revoke the old one.

Keys need sign-in to be switched on (`AUTH_MODE=standard`, the default).

## Claude (MCP)

Bento is an **MCP server**: connect it once and Claude can answer questions about your data ("how much did I spend on groceries last quarter compared to a year earlier?"), add a transaction ("add 12,40 € at the bakery today") and — if you allow it — build dashboards and forms, change the schema or write functions. **Connections → Claude** shows the address, the steps for each client, the apps connected with your account, and every call Claude made.

The address is your Bento's address plus `/mcp`, e.g. `https://bento.example.com/mcp`.

### Connect with your Bento account

- **Claude app, web and phone**: Settings → Connectors → *Add custom connector*, name it *Bento*, paste the address, **Connect**. Bento's sign-in opens; sign in and tick what Claude may do. Claude on the web reaches Bento from the internet, so Bento needs a public address (e.g. on Coolify).
- **Claude Code**: `claude mcp add --transport http bento https://bento.example.com/mcp`, then run `/mcp` in Claude Code and choose *Authenticate*; the browser opens Bento's sign-in.

On the sign-in page you tick what the app may do, area by area (**All** and **Read only** set them in one click):

| Permission | Allows | Needs |
|------------|--------|-------|
| Read everything | Tables and rows, the schema, dashboards, forms, saved queries and functions; analyses | always on |
| Add and change rows | New and changed records, CSV imports | editor |
| Delete rows | Removing records | editor |
| Dashboards and saved queries | Creating, changing and deleting them | viewer |
| Forms | Creating, changing and deleting entry forms | editor |
| Tables and columns | Changing the schema — this can delete data | admin |
| Functions and price sources | JavaScript functions, running price sources | admin |

The app acts as you and never does more than your role allows; Bento checks every call it makes against what you ticked, so an app allowed to add rows cannot change the schema even when you are an admin. Users, API keys, credentials and connections are never reachable for apps. Each connected app appears under **Connected apps** with its permissions and when it was last used; **Disconnect** ends its access at once; to change what it may do, connect again. Admins see every user's apps.

Claude also gets a **guide** (`bento_guide`): how Bento works and the exact formats of the schema, formulas, queries, dashboards, forms, the finance template and functions. It reads the matching part before building something, and asks before changing the schema or deleting.

### Claude Desktop extension

**Connections → Claude → Download bento.mcpb** gives you a Claude Desktop extension with your Bento's address already filled in. Double-click it (or drag it into Claude Desktop) and install it. On first use your browser opens Bento's sign-in, just like for the connector: sign in and tick what Claude may do. The extension is a small bridge that runs inside Claude Desktop and forwards to `/mcp`; it keeps the sign-in in `~/.bento` (readable only by you) and renews it by itself. It appears under **Connected apps** as *Claude Desktop*; after a *Disconnect* the next use asks you to sign in again. The file is also at `/api/v1/mcp/bento.mcpb` and holds no secret.

### Connect with an API key

For other clients without sign-in, pass an [API key](#api-keys) as a header:

```bash
claude mcp add --transport http bento https://bento.example.com/mcp \
  --header "Authorization: Bearer $BENTO_API_KEY"
```

A read key only reads; a read-write key may also add and change rows.

### What Claude can do

| Tool | Does | Permission |
|------|------|------------|
| `bento_guide` | How Bento works, topic by topic | read |
| `describe_schema`, `get_model` | The tables at a glance; the full Model JSON | read |
| `list_rows`, `get_row` | Rows with filters, search, sort and paging | read |
| `aggregate`, `check_formula`, `list_formula_functions` | Analyses with the query API; checking formulas | read |
| `list_saved_queries`, `run_saved_query`, `list_dashboards`, `get_dashboard`, `list_forms` | What exists | read |
| `list_functions`, `get_function`, `list_price_sources` | Functions and price sources | read |
| `call_api` | Any other API call, within the same permissions | read (and what the call needs) |
| `create_row`, `update_row` | Add or change a row | add and change rows |
| `delete_row` | Delete a row | delete rows |
| `create_dashboard`, `update_dashboard`, `delete_dashboard`, `save_query`, `delete_saved_query` | Dashboards and saved queries | dashboards |
| `create_form`, `update_form`, `delete_form` | Forms | forms |
| `preview_schema_change`, `apply_schema_change` | Schema changes, previewed first | tables and columns |
| `save_function`, `test_function`, `delete_function`, `run_price_source` | Functions and price sources | functions |

A connection only sees the tools it may use. API keys work like a connection with read only (read keys) or with rows, dashboards and forms (read-write keys).

**Recent calls** lists every tool call: when, which tool and arguments, through which app or key, the result and how long it took. Calls are kept 90 days.

Behind a proxy that hides the scheme or host (for example Coolify, where HTTPS ends before Bento), set `PUBLIC_URL` so the address and the sign-in links point to the right place; the Coolify compose file does that already.
