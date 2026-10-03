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

## If Something Fails

Try these checks first:

1. Confirm Docker is running
2. Confirm the containers are up and `healthy` with `docker compose ps` (full stack: proxy, frontend, backend)
3. Retry `GET /api/v1/admin/health` (through the proxy: `http://localhost/api/v1/admin/health`)
4. Make sure you are using the same port in both `API_ROOT` and Docker
5. For Postgres, confirm you replaced the placeholder passwords in `code/.env`

For a deeper technical validation path, see [../backend/packaged-mvp1-validation.md](../backend/packaged-mvp1-validation.md).
