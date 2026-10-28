# Bento

**Your own data workspace — self-hosted.** Design tables, fill them from forms, imports and APIs, look at them on dashboards, and let Claude work with them over MCP. Everything stays in a database you run.

> Formerly "Repère". Installs from before the rename keep their data; see [Upgrading From Before The Rename](docs/user/getting-started.md#upgrading-from-before-the-rename).

---

## What it does

- **Tables you design** — a visual schema designer (or Model JSON) with links, computed columns and safe migrations; a Data Explorer with inline editing, filters, saved views and kanban boards.
- **Data in** — CSV, Excel and JSON imports that read bank exports as they come; saved imports from a URL or from your own JavaScript function (any API) on a schedule; forms for quick entry, also public ones.
- **Dashboards** — key figures, charts, tables, targets and Sankey diagrams, edited Lens-style; a formula language for measures; share a dashboard with a link or embed it.
- **Formulas and functions** — row formulas and measures, plus your own sandboxed JavaScript functions.
- **Workspaces** — separate areas with their own tables, members and roles.
- **Notifications** — rules on values, targets and rows (added, changed, deleted), reports on a schedule, over e-mail, Slack or webhooks.
- **Calendars** — tables as calendars on your phone and computer over CalDAV.
- **AI** — an MCP server with OAuth sign-in for Claude and other MCP clients (per-permission consent, every workspace or some), plus "explain this", "describe instead of click" and categorisation in the app.
- **Templates** — projects and hours, habits and health, appointments, finance; installed in one click or by Claude.

## Quick start

You need Docker. From the repository root:

```bash
cp code/.env.example code/.env
docker compose -f code/docker-compose.yml --env-file code/.env up -d --build
```

Open `http://localhost` and follow the setup wizard: it creates the admin account and can load a small demo workspace. SQLite is the default; for Postgres, HTTPS and other setups (including [Coolify](https://coolify.io)) see **[Getting Started](docs/user/getting-started.md)**.

To connect Claude, see **[Connections](docs/user/connections.md)**.

## Documentation

| | |
|---|---|
| [User guide](docs/user/README.md) | Getting started, schema designer, importing, dashboards, formulas, forms, workspaces, notifications, apps, AI, the API |
| [Technical docs](docs/README.md) | Architecture, API and backend details |
| [Changelog](CHANGELOG.md) | What changed and when; [releases](https://github.com/rzukic/bento/releases) |

## Development

- **Backend**: ASP.NET Core 8 (`code/src/backend`), Dapper and SqlKata on SQLite or Postgres; tests in `code/tests` (`dotnet test`; Postgres tests run when `BENTO_TEST_POSTGRES_ADMIN_CONNECTION` is set).
- **Frontend**: React, Vite, TypeScript (`code/src/frontend`): `pnpm install`, `pnpm dev`, `pnpm test`, `pnpm run typecheck`, `pnpm run lint`.

## Repository structure

```
.
├── code/                   ← backend, frontend, proxy, Docker setups, tests
├── docs/                   ← user guide and technical docs
└── CHANGELOG.md            ← what changed and when
```

## Security

Please report vulnerabilities privately — see [SECURITY.md](SECURITY.md).

## License

[GNU Affero General Public License v3.0](LICENSE). If you run a modified Bento as a service for others, you make your changes available to them under the same license.
