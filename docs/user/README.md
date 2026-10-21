# User Guides

> Practical docs for people using the current product.
> Scope: setup, first login, browser schema design, data browsing, and day-to-day API usage.

---

## Start Here

If you want to get Bento running and try the current product without reading the technical internals first, use these guides:

- [Getting Started](getting-started.md) — start the packaged stack with Docker, finish the setup wizard in the browser (admin account, optional sample data), or run the API-only shape
- [Schema Designer](schema-designer.md) — create tables and relations visually, switch to JSON, preview, and apply
- [Connections](connections.md) — the API of your installation with try-it, OpenAPI download, and API keys for scripts
- [Dashboards](dashboards.md) — key figures and charts for any date range; share one with a link (read only, fixed filters, expiry, embeddable); a ready "Monthly cash flow" from a bank table; targets per group; formula values, values at each period end, variables such as runway
- [Formulas](formulas.md) — computed columns with spreadsheet-style formulas, links to other tables and sums over related rows, live in the explorer; your own JavaScript functions with `CUSTOM(…)`
- [Forms](forms.md) — quick entry into a table, e.g. "Add transaction" on the phone: −/+ for amounts, accounts and categories as buttons, today's date, the account used last time; from the sidebar and from dashboards
- [Live Prices](live-prices.md) — prices from an API of your choice (e.g. Yahoo Finance, no key) kept as history in your table, current when a dashboard opens; API keys in the credential vault
- [Importing Data](importing-data.md) — create a table from a CSV file or import into one, such as bank exports; skip rows that are already there, save the mapping as a profile; categories filled in by your rules, earlier rows and the AI
- [Notifications](notifications.md) — email, Slack or a signed webhook (n8n, Home Assistant); a test send and a log per channel
- [Apps](apps.md) — tables as calendars on your devices over CalDAV (iPhone, Mac, DAVx⁵, Thunderbird): events made there become rows; one password per device
- [Workspaces](workspaces.md) — separate areas in one installation (finances, calendar, health …), each with its own tables, dashboards and settings; switch in the sidebar, `/w/<name>` addresses, an MCP address per workspace
- [AI](ai.md) — explain any number on a dashboard (✨ on a widget), categorise bank bookings on import; your own Anthropic key with a monthly limit
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
- JWT login with a refresh cookie is the current auth path; signing out revokes the session
- API key auth, user management in the UI and vault mode are not finished yet
- The database provider and auth mode are chosen in the deployment configuration, not in the wizard

That is expected for the current Phase 2 product.
