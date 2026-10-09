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

Bento is an **MCP server**: connect it once and Claude can answer questions about your data ("how much did I spend on groceries last quarter compared to a year earlier?") and, if you allow it, add a transaction ("add 12,40 € at the bakery today"). **Connections → Claude** shows the address, the steps for each client, the apps connected with your account, and every call Claude made.

The address is your Bento's address plus `/mcp`, e.g. `https://bento.example.com/mcp`.

### Connect with your Bento account

- **Claude app, web and phone**: Settings → Connectors → *Add custom connector*, name it *Bento*, paste the address, **Connect**. Bento's sign-in opens; sign in and choose the access. Claude on the web reaches Bento from the internet, so Bento needs a public address (e.g. on Coolify).
- **Claude Code**: `claude mcp add --transport http bento https://bento.example.com/mcp`, then run `/mcp` in Claude Code and choose *Authenticate*; the browser opens Bento's sign-in.

On the sign-in page Bento asks which access the app gets:

| Access | Allows |
|--------|--------|
| Read only | Tables, rows, dashboards, forms and saved queries; analyses with the query API |
| Read and write | Also adding and changing rows |

The app acts as you and never does more than your role allows (a viewer's connection only reads). It never changes the schema, users or keys, and MCP has no delete. Each connected app appears under **Connected apps** with its access and when it was last used; **Disconnect** ends its access at once. Admins see every user's apps.

### Connect with an API key

For clients without sign-in, pass an [API key](#api-keys) as a header:

```bash
claude mcp add --transport http bento https://bento.example.com/mcp \
  --header "Authorization: Bearer $BENTO_API_KEY"
```

A read key only reads; a read-write key may also add and change rows.

### What Claude can do

| Tool | Does |
|------|------|
| `describe_schema` | The tables, columns, links and relations (one table in detail when named) |
| `list_rows`, `get_row` | Rows with filters, search, sort and paging; one row by id |
| `aggregate` | Sums, counts, averages over time and groups — the query API |
| `list_formula_functions` | Every formula function, for measures |
| `list_saved_queries`, `run_saved_query` | Saved queries |
| `list_dashboards`, `get_dashboard` | Dashboards and the queries behind their widgets |
| `list_forms` | Entry forms with their defaults (how you add a transaction) |
| `create_row`, `update_row` | Add or change a row (read-and-write access only) |

**Recent calls** lists every tool call: when, which tool and arguments, through which app or key, the result and how long it took. Calls are kept 90 days.

Behind a proxy that hides the scheme or host (for example Coolify, where HTTPS ends before Bento), set `PUBLIC_URL` so the address and the sign-in links point to the right place; the Coolify compose file does that already.
