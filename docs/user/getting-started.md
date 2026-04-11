# Getting Started

> Run the current MVP 1 product and make your first authenticated API call.

---

## What You Need

- Docker Desktop or another working Docker daemon
- a terminal
- `curl`
- optionally `jq` for prettier JSON output

You do not need to build the .NET project manually for normal MVP 1 usage.

---

## What You Are Starting

Right now Repere is a backend service.

You start a Docker container, then talk to it over HTTP:
- health check
- register first admin user
- log in
- upload a schema
- create and query data

By default, the API is available at:

```text
http://127.0.0.1:5000/api/v1
```

If port `5000` is already in use on your machine, change the host port in your compose/env setup before starting.

---

## Fastest Start: SQLite

From the repository root:

```bash
cp code/.env.example code/.env
docker compose -f code/docker-compose.yml --env-file code/.env up -d --build
```

This is the easiest way to try the system.

SQLite is a good fit if you want:
- the fastest local setup
- one local data directory
- a simple demo or evaluation environment

---

## Optional Start: Postgres

If you want to run against PostgreSQL instead, first fill in the Postgres passwords in `code/.env`, then start:

```bash
docker compose -f code/docker-compose.yml -f code/docker-compose.postgres.yml --env-file code/.env up -d --build
```

Use Postgres if you want:
- a setup closer to a production database
- stronger separation between system and data connections
- to test the packaged Postgres deployment path

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

- [Using The API](using-the-api.md) for the first real workflow

That guide shows how to:
- upload a schema JSON
- create rows
- query rows
- use filters, sorting, includes, and search

---

## Stopping The System

SQLite:

```bash
docker compose -f code/docker-compose.yml --env-file code/.env down
```

Postgres:

```bash
docker compose -f code/docker-compose.yml -f code/docker-compose.postgres.yml --env-file code/.env down
```

If you also want to remove volumes, add `-v`.

---

## If Something Fails

Try these checks first:

1. Confirm Docker is running
2. Confirm the backend container is up with `docker compose ps`
3. Retry `GET /api/v1/admin/health`
4. Make sure you are using the same port in both `API_ROOT` and Docker
5. For Postgres, confirm you replaced the placeholder passwords in `code/.env`

For a deeper technical validation path, see [../backend/packaged-mvp1-validation.md](../backend/packaged-mvp1-validation.md).
