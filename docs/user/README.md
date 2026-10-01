# User Guides

> Practical docs for people using the current product.
> Scope: setup, first login, browser schema design, data browsing, and day-to-day API usage.

---

## Start Here

If you want to get Repere running and try the current product without reading the technical internals first, use these guides:

- [Getting Started](getting-started.md) — start the backend with Docker, create the first admin user, and log in
- [Schema Designer](schema-designer.md) — create tables and relations visually, switch to JSON, preview, and apply
- [Using The API](using-the-api.md) — upload a schema, create data, query data, and understand the main API patterns

---

## What Repere Is Right Now

Repere now includes the Phase 2 browser shell for authenticated data management.

That means:
- you can use the web UI to browse data and design schemas
- you can still use the system through HTTP API calls
- you can define your data model visually or as JSON
- Repere creates the API routes for your tables automatically

If you are comfortable with `curl`, Postman, Insomnia, Bruno, or another API client, the API remains fully usable alongside the browser app.

---

## Recommended Path

1. Follow [Getting Started](getting-started.md)
2. Use [Schema Designer](schema-designer.md) to create or review your first model
3. Follow [Using The API](using-the-api.md) when you want to automate the same flows over HTTP
4. Keep the technical reference in [../api/README.md](../api/README.md) nearby for the full contract when you need more detail

---

## Current Limitations

- No dashboard builder yet
- Visual schema designer advanced relation properties are still limited
- Computed column formula editing is not yet a rich visual flow
- JWT login is the current auth path
- API key auth and logout are not finished yet

That is expected for the current Phase 2 product.
