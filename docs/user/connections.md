# Connections

**Connections** in the sidebar is where Bento meets scripts, spreadsheets and other tools: the API of your installation, explained and testable, and the API keys those tools use.

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
