# Using The API

> A practical walkthrough for using Repere through HTTP.

---

## The Basic Idea

Repere works like this:

1. You define your data model as JSON
2. You upload that schema
3. Repere creates API endpoints for each table
4. You use those endpoints to create, edit, list, and delete rows

You do not write backend code for your tables. Repere derives the data API from the current Model JSON.

---

## Reusable Variables

These examples assume:

```bash
export API_ROOT="http://127.0.0.1:5000/api/v1"
export TOKEN="your-access-token"
```

Every protected request needs:

```text
Authorization: Bearer <token>
```

---

## Step 1: Create A Schema JSON

Start with a simple example:

```json
{
  "tables": [
    {
      "name": "customers",
      "columns": [
        { "name": "id", "type": "integer", "primaryKey": true },
        { "name": "name", "type": "text", "nullable": false },
        { "name": "email", "type": "text", "nullable": false, "unique": true },
        { "name": "tier", "type": "enum", "values": ["bronze", "silver", "gold"] }
      ]
    },
    {
      "name": "orders",
      "columns": [
        { "name": "id", "type": "integer", "primaryKey": true },
        { "name": "customer_id", "type": "reference", "references": "customers", "nullable": false },
        { "name": "total", "type": "currency", "nullable": false },
        { "name": "status", "type": "enum", "values": ["pending", "paid", "shipped"] }
      ]
    }
  ],
  "relations": [
    {
      "name": "customer_orders",
      "type": "1:N",
      "from": "customers",
      "to": "orders",
      "column": "customer_id"
    }
  ]
}
```

Save it as `schema.json`.

What this gives you:
- a `customers` table
- an `orders` table
- each order belongs to one customer
- each customer has a related `customer_orders` accessor

---

## Step 2: Upload The Schema

```bash
curl -fsS -X PUT "$API_ROOT/admin/schema" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  --data-binary @schema.json
```

Once this succeeds, the data routes become available immediately.

That means you can now use:
- `POST /api/v1/data/customers`
- `GET /api/v1/data/customers`
- `POST /api/v1/data/orders`
- `GET /api/v1/data/orders`

---

## Step 3: Create Data

Create a customer:

```bash
curl -fsS -X POST "$API_ROOT/data/customers" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Acme Corp","email":"hello@acme.test","tier":"gold"}'
```

Create an order linked to that customer:

```bash
curl -fsS -X POST "$API_ROOT/data/orders" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"customer_id":1,"total":250.00,"status":"paid"}'
```

If a required field is missing or a value does not match the schema, the API returns a validation error.

---

## Step 4: List Data

List all customers:

```bash
curl -fsS "$API_ROOT/data/customers" \
  -H "Authorization: Bearer $TOKEN"
```

The list response looks like this:

```json
{
  "data": [
    {
      "id": 1,
      "name": "Acme Corp",
      "email": "hello@acme.test",
      "tier": "gold"
    }
  ],
  "meta": {
    "page": 1,
    "per_page": 25,
    "total": 1,
    "total_pages": 1
  }
}
```

---

## Step 5: Fetch One Row

```bash
curl -fsS "$API_ROOT/data/customers/1" \
  -H "Authorization: Bearer $TOKEN"
```

---

## Step 6: Update Data

Full replacement with `PUT`:

```bash
curl -fsS -X PUT "$API_ROOT/data/customers/1" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Acme Corporation","email":"hello@acme.test","tier":"gold"}'
```

Partial update with `PATCH`:

```bash
curl -fsS -X PATCH "$API_ROOT/data/customers/1" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"tier":"silver"}'
```

Simple rule of thumb:
- use `PUT` when you want to replace the whole row payload
- use `PATCH` when you only want to change a few fields

---

## Step 7: Delete Data

```bash
curl -fsS -X DELETE "$API_ROOT/data/customers/1" \
  -H "Authorization: Bearer $TOKEN"
```

---

## Filtering, Sorting, And Pagination

Filter paid orders:

```bash
curl -fsS "$API_ROOT/data/orders?filter[status]=paid" \
  -H "Authorization: Bearer $TOKEN"
```

Sort by highest total first:

```bash
curl -fsS "$API_ROOT/data/orders?sort=-total" \
  -H "Authorization: Bearer $TOKEN"
```

Paginate:

```bash
curl -fsS "$API_ROOT/data/orders?page=1&per_page=10" \
  -H "Authorization: Bearer $TOKEN"
```

You can combine them:

```bash
curl -fsS "$API_ROOT/data/orders?filter[status]=paid&sort=-total&page=1&per_page=10" \
  -H "Authorization: Bearer $TOKEN"
```

---

## Includes And Related Data

To include the related customer while listing orders:

```bash
curl -fsS "$API_ROOT/data/orders?include=customer" \
  -H "Authorization: Bearer $TOKEN"
```

To fetch all orders for one customer through the relation accessor:

```bash
curl -fsS "$API_ROOT/data/customers/1/customer_orders" \
  -H "Authorization: Bearer $TOKEN"
```

You can also filter and sort on that relation sub-resource:

```bash
curl -fsS "$API_ROOT/data/customers/1/customer_orders?filter[status]=paid&sort=-id" \
  -H "Authorization: Bearer $TOKEN"
```

---

## Search

For text-heavy tables, use `search`:

```bash
curl -fsS "$API_ROOT/data/customers?search=acme" \
  -H "Authorization: Bearer $TOKEN"
```

This performs a case-insensitive text search across text columns. Several words must all match (AND).

Reference columns are searched by the record they point to: `orders?search=globex` finds the orders whose customer's name contains "globex", even though `orders` only stores `customer_id`. "Name" is what the app shows for a record: the table's record label (and its secondary column) or, without one, its first text column.

### Dates and times

`datetime` values are local wall time as written (`2026-10-03T15:29`), without a time zone. Values filled by the database itself (for example a link's `created_at`) are UTC and carry a `Z` (`2026-10-03T13:29:43Z`) on both SQLite and Postgres.

### Many-to-many links

An N:M relation stores its links in a junction table that is part of the schema (for the demo: `_jt_orders_products`). Read, create and delete links like any other rows; a link's id is its composite key (`order_id:1|product_id:2`, URL-encoded):

```bash
curl -fsS "$API_ROOT/data/_jt_orders_products?filter[order_id]=1" \
  -H "Authorization: Bearer $TOKEN"
```

---

## Check The Current Schema

If you want to inspect the active schema:

```bash
curl -fsS "$API_ROOT/admin/schema" \
  -H "Authorization: Bearer $TOKEN"
```

If you want to inspect migration history:

```bash
curl -fsS "$API_ROOT/admin/migrations" \
  -H "Authorization: Bearer $TOKEN"
```

---

## Common Mistakes

- Forgetting the `Authorization` header on protected routes
- Using `PUT` without sending all required writable fields
- Creating a `reference` value that points to a row that does not exist
- Using a relation name you invented instead of the actual configured accessor
- Uploading a schema change that removes tables or columns without confirming destructive changes

If a schema change is destructive, retry with:

```text
?confirm_destructive=true
```

only after you are sure you want that migration.

---

## Best Way To Work With The API

The simplest API-first workflow is:

1. keep your schema JSON in version control
2. apply schema changes through `PUT /api/v1/admin/schema`
3. use Postman, Bruno, Insomnia, or `curl` for daily usage
4. use the health and migration endpoints to verify the system state

The browser Schema Designer now provides the same schema-management path visually, while this guide remains useful for automation and direct API integration.

---

## When You Need More Detail

Use the technical docs when you want the full contract:

- [API Reference](../api/README.md)
- [Backend Notes](../backend/README.md)
- [Packaged MVP 1 Validation](../backend/packaged-mvp1-validation.md)
