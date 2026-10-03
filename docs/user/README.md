# User Guides

> Practical docs for people using the current product.
> Scope: setup, first login, browser schema design, data browsing, and day-to-day API usage.

---

## Start Here

If you want to get Bento running and try the current product without reading the technical internals first, use these guides:

- [Getting Started](getting-started.md) — start the packaged stack with Docker, finish the setup wizard in the browser (admin account, optional sample data), or run the API-only shape
- [Schema Designer](schema-designer.md) — create tables and relations visually, switch to JSON, preview, and apply
- [Using The API](using-the-api.md) — upload a schema, create data, query data, and understand the main API patterns

---

## What Bento Is Right Now

Bento is a packaged browser product (MVP 2 "Spreadsheet+"): one `docker compose up`, open `http://localhost`, and a setup wizard creates your admin account and can load a demo workspace.

That means:
- you can use the web UI to browse and edit data, save views and kanban boards, and design schemas
- you can still use the system through HTTP API calls
- you can define your data model visually or as JSON, preview the migration, and apply it
- Bento creates the API routes for your tables automatically

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
- JWT login with a refresh cookie is the current auth path; signing out revokes the session
- API key auth, user management in the UI and vault mode are not finished yet
- The database provider and auth mode are chosen in the deployment configuration, not in the wizard

That is expected for the current Phase 2 product.
