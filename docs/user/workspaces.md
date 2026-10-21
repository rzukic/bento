# Workspaces: Separate Areas In One Installation

A **workspace** is a separate area of your Bento — finances, calendar & operations, health. Each has its own **tables** (the same table name can exist in two workspaces), schema history, dashboards, forms, saved queries, views, functions, price sources, imports, rules and reports, apps and settings (locale, currency, time zone, week and year start). **Users, notification channels and AI** are shared by all workspaces.

Every installation starts with the workspace **Default**: everything from before workspaces is in it, and its addresses stay as they were.

## Switching

With more than one workspace, the sidebar's heading names the one you are in; click it to switch. A workspace has its own address — `/w/finances/…` — so a bookmark or a link opens the right one. Default has no prefix.

## Creating, Renaming, Deleting (Admins)

**Settings → Workspaces**:

- **New workspace**: a name and, optionally, a **short name** for the addresses (`/w/<short name>`, `/mcp/<short name>`). Without one it is made from the name ("Kalender & Betrieb" → `kalender-betrieb`). The short name stays for good: links, scripts and devices use it. A new workspace starts empty — open it and draw its first table, import a file or install a template there.
- **Rename** changes the name only.
- **Delete** removes the workspace with all its tables and rows, dashboards, forms, imports, rules, apps and settings. Type its short name to confirm; it cannot be undone. Default cannot be deleted.

Settings → Workspace (locale, currency, time zone …) applies to the workspace you are in; a new workspace starts with Default's.

## Templates

A template installs into the workspace you are in: open the workspace first, then set up the template.

## API, Scripts And Claude

- **API**: `/api/v1/w/<short name>/…` — e.g. `/api/v1/w/finances/data/accounts` — or the header `X-Bento-Workspace: finances` on the usual address. Without either, the request goes to Default, so existing scripts keep working. `GET /api/v1/workspaces` lists them. The API page (Connections) shows the addresses of the workspace you are in.
- **MCP (Claude)**: each workspace has its own MCP address, `https://<your Bento>/mcp/<short name>`; Claude connected there sees only that workspace's tables. The existing `/mcp` is Default's. Connections → MCP shows the address (and the `.mcpb` file) of the workspace you are in.
- **Calendar apps** belong to the workspace they were made in; a device's password leads it there, so the CalDAV address stays `/dav/`.

## Not Yet

- Members per workspace: for now every user sees every workspace, with their role in all of them.
- API keys and MCP connections limited to some workspaces.
- Links or formulas across workspaces.
