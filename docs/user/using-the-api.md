# Using The API

> A practical walkthrough for using Bento through HTTP.

---

## The Basic Idea

Bento works like this:

1. You define your data model as JSON
2. You upload that schema
3. Bento creates API endpoints for each table
4. You use those endpoints to create, edit, list, and delete rows

You do not write backend code for your tables. Bento derives the data API from the current Model JSON.

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

`TOKEN` can be a session token from `POST $API_ROOT/auth/login` (short-lived) or, for scripts, an **API key** (`bento_…`) created under **Connections → API keys**; a key also works as `X-API-Key: <key>`. Read keys read and run queries; read-write keys also change data; no key reaches schema, user or key management. The **Connections → API** page lists every endpoint of your installation with fields and examples, lets you try them, and offers the OpenAPI description (`GET $API_ROOT/openapi.json`). See [Connections](connections.md).

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

Later changes don't need the whole schema again. Send only the operations (admin; preview first without `?confirm_destructive`):

```bash
curl -fsS -X POST "$API_ROOT/admin/schema/patch/preview" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"operations":[{"op":"addColumn","table":"orders","column":{"name":"note","type":"text"}}]}'

curl -fsS -X PATCH "$API_ROOT/admin/schema" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"operations":[{"op":"addColumn","table":"orders","column":{"name":"note","type":"text"}}]}'
```

The operations are `addTable`, `changeTable`, `renameTable`, `removeTable`, `addColumn`, `changeColumn`, `renameColumn`, `removeColumn`, `addRelation`, `changeRelation`, `removeRelation`, `addIndex` and `removeIndex`. Renames keep the data.

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

Many rows at once (up to 1000 per call, all or none):

```bash
curl -fsS -X POST "$API_ROOT/data/orders/bulk" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"rows":[{"external_id":"A1","customer_id":1,"total":250},{"external_id":"A2","customer_id":1,"total":80}],"upsertOn":["external_id"]}'
```

With `upsertOn`, a row whose columns match an existing row changes that row, and only the columns you send; the others are added. The answer counts them (`inserted`, `updated`) and gives each row's id. An error names the row, and nothing is written.

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

To include the related customer while listing orders (`include=customer`, or by the link column, `include=customer_id`; the customer appears under `customer` either way):

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

## Aggregate Data

`POST $API_ROOT/query` sums, counts and averages a table, grouped by columns, linked records or months — without SQL. Every role that can read data can use it.

```bash
# Spending per category and month in 2026, and the running balance per month
curl -fsS "$API_ROOT/query" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{
  "source": "transactions",
  "filter": { "booked_on": { "gte": "2026-01-01" }, "amount": { "lt": 0 } },
  "groupBy": [ { "column": "category_id" }, { "column": "booked_on", "bucket": "month", "as": "month" } ],
  "measures": [ { "name": "spent", "fn": "sum", "column": "amount" }, { "name": "bookings", "fn": "count" } ],
  "sort": [ "month", "spent" ]
}'
```

```json
{
  "data": [
    { "category_id": 3, "category_id_label": "Lebensmittel", "month": "2026-09-01", "spent": -49.1, "bookings": 2 }
  ],
  "meta": { "columns": [ { "name": "category_id", "type": "reference", "role": "group" }, "…" ], "truncated": false }
}
```

- `filter` uses the list operators (`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `in` with an array, `null`, `notnull`); a plain value means `eq`
- `bucket` groups dates by `day`, `week` (from Monday), `month`, `quarter` or `year`; the value is the first day of the bucket
- `"top": 10` keeps the 10 values ranked first by the first measure (`"rank": "asc"` for smallest first, e.g. spending; `"rank": "abs"` for the largest amounts of either sign, e.g. income and expenses in a money flow); `"other": true` adds the rest as one group, flagged by `<name>_other: true`; empty values stay their own group
- a link column (here `category_id`) also returns `<name>_label`, the linked record's name
- `fn` is `count`, `sum`, `avg`, `min`, `max`, `count_distinct`, `median` or `percentile` (with `"percentile": 90`, 0–100); median and percentiles interpolate between neighbours like Postgres' `percentile_cont`
- `"running": true` turns a sum or count into a running total over the date group; add `"carryIn": true` to start from the rows before the date range — a balance from September on then starts at the end-of-August balance

A measure can be a formula over the rows of each group instead of `fn`/`column`, and `asOf` computes values at the end of each period:

```bash
curl -fsS "$API_ROOT/query" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{
  "source": "transactions",
  "groupBy": [ { "column": "booked_on", "bucket": "month", "as": "month" } ],
  "measures": [
    { "name": "savings_rate", "formula": "(SUM(amount WHERE amount > 0) + SUM(amount WHERE amount < 0)) / SUM(amount WHERE amount > 0)" },
    { "name": "balance", "formula": "RUNNING_SUM(SUM(amount))" }
  ]
}'
```

- `"asOf": { "bucket": "month", "from": "2025-01-01", "to": "2026-09-30", "as": "month" }` returns one row per month; measures are formulas over all rows with `BUCKET_START()`/`BUCKET_END()`, e.g. `SUM(amount WHERE booked_on <= BUCKET_END())`
- `"variables": { "months": 3 }` makes values usable by name in formulas
- `POST $API_ROOT/query/variables` with `{ "variables": [ { "name": "spent", "source": "transactions", "formula": "-SUM(amount WHERE amount < 0)", "filter": { … } }, { "name": "burn_rate", "formula": "spent / 3" } ] }` evaluates dashboard variables in dependency order and returns each one's `value` and `type`, or its `error`

Save a query to run it again (also from dashboards later): `POST $API_ROOT/queries` with `{ "name": "Monthly spending", "isShared": true, "query": { … } }`, then `POST $API_ROOT/queries/<id>/run`, optionally with `{ "filter": { … } }` to narrow it further. `GET $API_ROOT/queries` lists your queries and shared ones; only the owner changes or deletes a query.

---

## Check Formulas

`POST $API_ROOT/formulas/validate` checks a [formula](formulas.md) against a table: its result type, the columns it uses, and errors with character positions. It always answers 200. `POST $API_ROOT/formulas/preview` computes it on the first rows (`limit`, 1–100, default 20). Both only read, so read-only API keys may call them.

```bash
curl -fsS "$API_ROOT/formulas/preview" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{
  "table": "transactions",
  "formula": "= IF(amount < 0, \"expense\", \"income\")",
  "limit": 2
}'
```

```json
{
  "data": {
    "type": "text",
    "columns": [ "amount" ],
    "rows": [
      { "id": 1, "value": "income", "inputs": { "amount": 3000 } },
      { "id": 2, "value": "expense", "inputs": { "amount": -1000 } }
    ]
  }
}
```

- an invalid formula returns `"valid": false` from validate with `errors: [{ "message", "start", "end" }]`, and 400 from preview
- `resultType` (`text`, `integer`, `float`, `currency`, `boolean`, `date`, `datetime`) checks that the formula can produce it
- validate also takes `model` — a whole draft Model JSON — or `columns` (the table's columns) to check a draft that is not applied yet, and `column`, the computed column the formula belongs to, so a formula using itself is reported as a cycle

Computed columns (`"type": "computed"` with a `formula`) are part of every read: list, single row, write responses, related rows and includes. They can be used in `filter[...]`, `sort` and `fields` like stored columns, and in `/query` groups and measures; writing them is rejected.
- `GET $API_ROOT/formulas/functions` lists every function with signature, description and example

---

## Import CSV Files

The browser import ([Importing Data](importing-data.md)) uses these routes; scripts can call them directly. Files travel base64-encoded so the encoding can be detected. Every route needs `editor`, `admin` or `api`.

```bash
CONTENT=$(base64 < girokonto.csv | tr -d '\n')

# What Bento detects: encoding, separator, header row, number/date format, columns with samples
curl -fsS "$API_ROOT/import/analyze" -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d "{\"content\": \"$CONTENT\"}"

# Dry run, then the real import (same body)
cat > import.json <<JSON
{
  "content": "$CONTENT",
  "settings": { "decimalSeparator": ",", "dateFormat": "dd.MM.yyyy" },
  "mapping": [
    { "source": "Buchungstag", "target": "booked_on" },
    { "source": "Betrag (EUR)", "target": "amount" },
    { "source": "Kategorie", "target": "category_id" }
  ],
  "keyColumns": ["booked_on", "amount"]
}
JSON
curl -fsS "$API_ROOT/import/transactions/preview" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d @import.json
curl -fsS "$API_ROOT/import/transactions" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d @import.json
```

- `settings` fields left out are detected (`encoding`, `delimiter`, `headerLine` as a 0-based record index, `decimalSeparator`, `dateFormat`)
- reference columns take the referenced record's label (e.g. a category name) or its id
- a mapping entry with `"value"` instead of `"source"` fills that column with the same value for every row, e.g. `{ "target": "account_id", "value": "Checking" }` for the account a bank export belongs to
- rows whose key columns match a row in the table are skipped (counted per key, so real repeats in one file still import)
- `"distinctKeys": true` also skips repeats within the file and compares keys without case — for filling a lookup table with one row per value
- `analyze` returns per column `blank` (empty cells), `distinctCount` and up to 100 `distinct` values
- the response has `total`, `new`, `duplicates`, `invalid`, `inserted` and, per row, `line`, `status` (`new`, `duplicate`, `invalid`), `display` and `errors`
- new rows are inserted in one transaction

Saved mappings: `GET/POST $API_ROOT/import/<table>/profiles`, `PUT/DELETE $API_ROOT/import/<table>/profiles/<id>` with `{ "name": "...", "config": { "settings": {...}, "mapping": [...], "keyColumns": [...] } }`.

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
