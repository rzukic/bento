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

## Users, Members And Roles

- **Installation admins** run the whole Bento: every workspace (as admin), users, workspaces, the credential vault, notification channels, AI settings, API keys and problems. The first account is one.
- **Everyone else** opens only the workspaces they are a **member** of, with a role in each: **Admin** (schema, settings, apps, functions, rules, members of that workspace), **Editor** (add and change rows, import, forms), **Viewer** (look). Someone who is an editor in "Finances" can be a viewer in "Health" and absent from "Calendar" — they do not see it at all, not even in the switcher.
- **Settings → Users** (installation admins): add a person with a password, their role and the workspaces they join (none ticked: the one you are in); make someone installation admin or a member again; reset a password (signs them out everywhere); **delete** someone — their dashboards, forms, saved queries and views become yours, they are signed out, their API keys stop (and stay listed as revoked), their connected apps are disconnected. The last installation admin stays one, and you cannot delete yourself.
- **Settings → Members of …** (the workspace's admins): who is in the open workspace and with which role; add people the installation already has, change a role, take someone out — at once, they lose access with their next click.
- A workspace's admin cannot put credentials from the vault into a function or an import: the vault belongs to the installation.
- Opening an address of a workspace you are not in takes you to the first one you are in.

## Templates

A template installs into the workspace you are in: open the workspace first, then set up the template.

## API, Scripts And Claude

- **API keys** (Connections → API keys, installation admins) work in the workspace they were made in, in chosen ones, or in all (also new ones). A key used elsewhere is refused. Keys from before workspaces work in Default.
- **Keys for some tables only** (Tables → Only some…): the key reads and writes the rows of those tables and imports into them — say a bank sync that may touch `transactions` and nothing else. It cannot run queries, open dashboards, forms or functions, or read related rows of other tables (`include`); it can read the schema.
- **API**: `/api/v1/w/<short name>/…` — e.g. `/api/v1/w/finances/data/accounts` — or the header `X-Bento-Workspace: finances` on the usual address. Without either, the request goes to Default, so existing scripts keep working. `GET /api/v1/workspaces` lists them. The API page (Connections) shows the addresses of the workspace you are in.
- **MCP (Claude)**: each workspace has its own MCP address, `https://<your Bento>/mcp/<short name>`; Claude connected there sees only that workspace's tables. The existing `/mcp` is Default's. Connections → MCP shows the address (and the `.mcpb` file) of the workspace you are in. When you connect, Bento's sign-in page asks **in which workspaces** the app may work — the one of its address is ticked; tick more to let it use the same connection there. Change it later under Connections → AI apps. What it may do is capped by your role in each workspace.
- **Calendar apps** belong to the workspace they were made in; a device's password leads it there, so the CalDAV address stays `/dav/`.

## Not Yet

- Links or formulas across workspaces.
- API keys limited to IP addresses or with their own rate limit (sign-in has a limit already; addresses behind the proxy change too often to be a good lock).
