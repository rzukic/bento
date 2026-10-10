# Getting Started

> Start Bento with Docker, finish the setup wizard in the browser, and (optionally) make your first authenticated API call.

---

## What You Need

- Docker Desktop or another working Docker daemon
- a terminal
- `curl`
- optionally `jq` for prettier JSON output

You do not need to build the .NET project manually for normal packaged usage.

---

## Two Ways To Run Bento

- **Browser-first (recommended)** — `code/docker-compose.yml` starts a reverse proxy, the web app and the backend. You open `http://localhost`, and a setup wizard takes you from an empty installation to a signed-in admin, optionally with a small demo workspace. Only the proxy publishes ports.
- **API only** — `code/docker-compose.backend-only.yml` starts just the backend on port `5000`. Use it when you only want the HTTP API; the rest of this guide's `curl` sections use this shape.

Both shapes take the same Postgres override and the same `code/.env`.

---

## Browser-First Start

From the repository root:

```bash
cp code/.env.example code/.env
docker compose -f code/docker-compose.yml --env-file code/.env up -d --build
```

The first build takes a few minutes (it compiles the backend and the web app); later starts are fast. When `docker compose ps` shows the proxy, frontend and backend as `healthy`, open:

```text
http://localhost
```

You also reach it from other machines on your network via this computer's IP address — plain HTTP, no certificate warnings, as long as `DOMAIN` stays `localhost`.

### The setup wizard

A fresh installation opens the wizard on every route:

1. **Welcome** — what the next steps do
2. **Environment** — the database provider and auth mode, shown read-only; they come from `code/.env` (`DATABASE_PROVIDER`, `AUTH_MODE`)
3. **Admin account** — pick a username and password; the first account is the administrator and you are signed in automatically
4. **Sample data** — optionally load a demo workspace (customers, orders, products with relations and an order status suited for a kanban board). This is only possible while the installation is empty; skip it to start from a blank schema
5. **Done** — open the workspace

After that, `http://localhost` goes straight to the app; `/setup` redirects to the app, and signed-out visitors land on the sign-in page.

### What to do next in the browser

- **Home** shows your tables, schema version and relations
- **Data** (`/data`) is the Data Explorer: browse, filter, sort and search, edit cells inline, add and delete rows, save views (including kanban boards), export CSV/JSON
- **Schema** (`/model`) is the schema designer: draw tables and relations visually or edit the Model JSON, preview the migration and apply it

See [Schema Designer](schema-designer.md) for the designer walkthrough.

### Ports, HTTPS, Postgres

- If port 80 or 443 is taken, set `HOST_HTTP_PORT` / `HOST_HTTPS_PORT` in `code/.env` and open `http://localhost:<port>`
- For automatic HTTPS, set `DOMAIN` to a public hostname that resolves to this machine and make ports 80 and 443 reachable from the internet; the proxy obtains and renews the certificate
- For Postgres, fill in the three Postgres passwords in `code/.env` and add the override:

```bash
docker compose -f code/docker-compose.yml -f code/docker-compose.postgres.yml --env-file code/.env up -d --build
```

### On Coolify

`code/docker-compose.coolify.yml` runs the whole stack with Postgres on a [Coolify](https://coolify.io) server, which provides HTTPS and redeploys on every push:

1. **New resource** → your Git repository (private repositories through Coolify's GitHub App), branch e.g. `dev` or `main`.
2. **Build pack** *Docker Compose*, **Base directory** `/code`, **Docker Compose file** `/docker-compose.coolify.yml`.
3. Set the domain on the **proxy** service (`https://bento.example.com`) and point its DNS record (A or CNAME) at the server.
4. **Deploy**, open the domain and finish the setup wizard.

Coolify generates the Postgres passwords and the JWT secret on the first deploy and keeps them. The data lives in named volumes, so redeploys and updates keep it; with automatic deploys on, each push to the branch updates Bento. Before the backend starts, the one-shot `postgres-init` service re-applies the database roles on every deploy.

The server must accept inbound traffic on ports **80 and 443**: Let's Encrypt checks port 80 before it issues the certificate, and GitHub only delivers push webhooks (automatic deploys) to a dashboard with a valid certificate.

### Stopping

```bash
docker compose -f code/docker-compose.yml --env-file code/.env down
```

Add `-v` to also remove the data volumes (this deletes your data and the proxy's certificates).

---

## API-Only Start

If you only want the backend, start it on port `5000`:

```bash
cp code/.env.example code/.env
docker compose -f code/docker-compose.backend-only.yml --env-file code/.env up -d --build
```

For Postgres:

```bash
docker compose -f code/docker-compose.backend-only.yml -f code/docker-compose.postgres.yml --env-file code/.env up -d --build
```

If port `5000` is already in use on your machine, change `HOST_BACKEND_PORT` in `code/.env` before starting.

By default the API is available at:

```text
http://127.0.0.1:5000/api/v1
```

The following sections register the admin and log in over HTTP. With the browser-first stack the wizard has already done both, and the same API is available at `http://localhost/api/v1` (no port).

---

## Check That The Backend Is Up

Set a reusable API root:

```bash
export API_ROOT="http://127.0.0.1:5000/api/v1"
```

Then check health:

```bash
curl -fsS "$API_ROOT/admin/health"
```

You should get a response like:

```json
{
  "status": "healthy",
  "database": {
    "system_pool": "connected",
    "data_pool": "connected"
  },
  "version": "1.0.0"
}
```

If that works, the backend is ready.

---

## Create The First Admin User

The very first user can self-register.

```bash
curl -fsS -X POST "$API_ROOT/auth/register" \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin12345"}'
```

After the first user exists, normal self-registration closes and additional users require an authenticated admin.

Pick a stronger password outside of demos.

---

## Log In

```bash
curl -fsS -X POST "$API_ROOT/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin12345"}'
```

The response includes an `access_token`.

Save it in a shell variable:

```bash
export TOKEN="$(
  curl -fsS -X POST "$API_ROOT/auth/login" \
    -H "Content-Type: application/json" \
    -d '{"username":"admin","password":"admin12345"}' \
  | jq -r '.access_token'
)"
```

Now you can call protected endpoints:

```bash
curl -fsS "$API_ROOT/admin/health/details" \
  -H "Authorization: Bearer $TOKEN"
```

---

## What To Do Next

Once you can log in successfully, continue with:

- [Schema Designer](schema-designer.md) for the browser workflow to create tables, relations, preview migrations, and apply changes
- [Using The API](using-the-api.md) for the first real workflow

The API guide shows how to:
- upload a schema JSON
- create rows
- query rows
- use filters, sorting, includes, and search

---

## Stopping The API-Only Stack

SQLite:

```bash
docker compose -f code/docker-compose.backend-only.yml --env-file code/.env down
```

Postgres:

```bash
docker compose -f code/docker-compose.backend-only.yml -f code/docker-compose.postgres.yml --env-file code/.env down
```

If you also want to remove volumes, add `-v`.

---

## Upgrading From Before The Rename

Bento was called "Repère" up to v0.2.0. The technical names moved with it: images `bento/*`, the data volume `bento-data`, the `Bento.Api` project. An install started before the rename keeps its data as follows:

- **Data volume (SQLite)**: stop the stack (`down`, without `-v`), copy the old volume once, then start again with the current compose files:

  ```bash
  code/scripts/migrate-volume-from-repere.sh
  ```

  The script copies `code_repere-data` into `code_bento-data` and leaves the old volume untouched; pass the project name if the stack was started with `-p <name>`. Remove the old volume yourself once everything is there.
- **Postgres**: the Postgres volume keeps its name. Keep the old database and role names in `code/.env`: `POSTGRES_DB=repere`, `POSTGRES_SUPERUSER=repere_admin`, `BENTO_SYSTEM_ROLE=repere_system`, `BENTO_DATA_ROLE=repere_data`. Rename `REPERE_SYSTEM_PASSWORD` / `REPERE_DATA_PASSWORD` to `BENTO_SYSTEM_PASSWORD` / `BENTO_DATA_PASSWORD`.
- **Browser**: signed-in sessions carry over (the old refresh cookie is accepted once and replaced), and theme, sidebar and schema mode settings are moved to their new keys on the first load.

---

## If Something Fails

Try these checks first:

1. Confirm Docker is running
2. Confirm the containers are up and `healthy` with `docker compose ps` (full stack: proxy, frontend, backend)
3. Retry `GET /api/v1/admin/health` (through the proxy: `http://localhost/api/v1/admin/health`)
4. Make sure you are using the same port in both `API_ROOT` and Docker
5. For Postgres, confirm you replaced the placeholder passwords in `code/.env`

For a deeper technical validation path, see [../backend/packaged-mvp1-validation.md](../backend/packaged-mvp1-validation.md).

## Numbers, Dates And Currency

Bento writes numbers, dates and amounts the way your browser's language does, e.g. `-1.250,00 €` in German and `-€1,250.00` in English.
- **Settings → Appearance → Numbers and dates** picks another way for this browser.
- Admins set the default for everyone under **Settings → Workspace**: the language and region, e.g. Deutsch (Österreich), and the currency of amounts.
