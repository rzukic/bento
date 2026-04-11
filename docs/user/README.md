# User Guides

> Practical docs for people using the current MVP 1 product.
> Scope: setup, first login, schema upload, and day-to-day API usage.

---

## Start Here

If you want to get Repere running and try the current product without reading the technical internals first, use these guides:

- [Getting Started](getting-started.md) — start the backend with Docker, create the first admin user, and log in
- [Using The API](using-the-api.md) — upload a schema, create data, query data, and understand the main API patterns

---

## What Repere Is Right Now

MVP 1 is a backend-only product.

That means:
- there is no web UI yet
- you use the system through HTTP API calls
- you define your data model as JSON
- Repere creates the API routes for your tables automatically

If you are comfortable with `curl`, Postman, Insomnia, Bruno, or another API client, you can use the product today.

---

## Recommended Path

1. Follow [Getting Started](getting-started.md)
2. Follow [Using The API](using-the-api.md)
3. Keep the technical reference in [../api/README.md](../api/README.md) nearby for the full contract when you need more detail

---

## Current Limitations

- No frontend yet
- No dashboard builder yet
- No visual schema designer yet
- JWT login is the current auth path
- API key auth and logout are not finished yet

That is expected for MVP 1.
