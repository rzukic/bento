# Bento — overview

Bento is a self-hosted data workspace. The user designs **tables** (the schema), fills them in the Data Explorer, by CSV import, with **forms** or through the API, and looks at them on **dashboards**. **Formulas** compute columns and dashboard values; **functions** are the user's own JavaScript; **import sources** fetch data from the web — a file, or any API through a function — into a table on a schedule. What the tables hold is up to the user — projects and hours, a habit log, customers and orders, money. **Templates** set up a ready-made model — tables, dashboards, forms, example rows: `list_templates` shows them and which ones this workspace has, `install_template` sets one up (ask first; it needs tables and columns, dashboards, forms and adding rows).

## Topics (bento_guide topic=…)

| Topic | What it explains |
|---|---|
| `schema` | Model JSON: tables, column types, links, relations, computed columns, record labels; how to change it safely |
| `formulas` | The formula language: row formulas (computed columns) and measures (dashboards, aggregate) |
| `queries` | The aggregation query (the `aggregate` tool, widgets, saved queries) |
| `dashboards` | Dashboard JSON: widgets, layout, variables, filters, annotations |
| `forms` | Form JSON: entry forms such as "Log hours" |
| `finance` | The finance template's tables, conventions and formulas (only for workspaces that have it) |
| `functions` | JavaScript functions, `CUSTOM`/`CUSTOM_ROWS`, price sources |
| `api` | The REST endpoints behind the tools, for `call_api` |

## How to work

1. `describe_schema` first; `get_model` when you will change the schema.
2. Answer questions with `aggregate` (totals per month, per project, per category …) rather than paging through rows.
3. Before building: read the topic, look at an existing example (`get_dashboard`, `list_forms`), then create.
4. Writes run with the user's rights and this connection's permissions; a refused call says which permission is missing.
5. Schema changes: `preview_schema_change`, explain the result (especially anything destructive), apply only after the user agrees.

## Workspaces

An installation can have several workspaces, each with its own tables, dashboards, forms, functions and settings. A connection is made for some or all of them; the instructions name them when there is more than one. `list_workspaces` lists them with your role; every tool takes `workspace` (the short name) to work in another one — without it, the connection's own (its MCP address). Say which workspace you mean when it is not that one. With the permission `workspaces.manage` (installation admins), `create_workspace` makes a new, empty one, which the connection may use at once. Workspaces are never deleted over MCP.

## Roles

- **viewer**: reads; keeps own dashboards and saved queries.
- **editor**: also writes rows, imports, forms.
- **admin**: also the schema, functions, price sources, users and keys.

A connected app never does more than its user, and only what was ticked when connecting.
