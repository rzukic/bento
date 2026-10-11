# Schema (Model JSON)

Read it with `get_model`; change it with `preview_schema_change` then `apply_schema_change` (admin). Send either **operations** (small changes, preferred) or the **whole** model (tables left out of a whole model are dropped).

## Operations

A list of changes to the current schema, applied in order, then checked like a whole model:

```json
{ "operations": [
  { "op": "addColumn", "table": "trades", "column": { "name": "note", "type": "text" }, "after": "price" },
  { "op": "changeColumn", "table": "trades", "column": "fees", "set": { "displayName": "Fees", "nullable": false } },
  { "op": "renameColumn", "table": "trades", "column": "fees", "to": "costs" },
  { "op": "addTable", "table": { "name": "goals", "columns": [ { "name": "id", "type": "integer", "primaryKey": true }, { "name": "target", "type": "currency" } ] } },
  { "op": "addRelation", "relation": { "name": "account_goals", "type": "1:N", "from": "accounts", "to": "goals", "column": "account_id" } }
] }
```

| op | Fields |
|---|---|
| `addTable`, `changeTable`, `renameTable`, `removeTable` | `table` (the table object for add, else its name); `set` for change; `to` for rename |
| `addColumn`, `changeColumn`, `renameColumn`, `removeColumn` | `table`, `column` (the column object for add, else its name); `after` (add); `set` (change; `null` removes an option); `to` (rename) |
| `addRelation`, `changeRelation`, `removeRelation` | `relation` (object for add, else its name); `set` for change |
| `addIndex`, `removeIndex` | `table`, `index` (`{name, columns}` for add, else its name) |

Renames keep the data (they set `renamedFrom`). The preview is compact: the changes, errors and warnings, without the whole model. REST: `POST /admin/schema/patch/preview`, `PATCH /admin/schema?confirm_destructive=true`.

```json
{
  "tables": [
    {
      "name": "transactions",
      "displayName": "Transactions",
      "description": "Bookings of all accounts",
      "recordLabel": { "column": "payee", "secondary": "date" },
      "columns": [
        { "name": "id", "type": "integer", "primaryKey": true },
        { "name": "date", "displayName": "Date", "type": "date", "nullable": false },
        { "name": "amount", "type": "currency", "nullable": false },
        { "name": "kind", "type": "enum", "values": ["income", "expense"] },
        { "name": "account_id", "type": "reference", "references": "accounts" },
        { "name": "spent", "type": "computed", "formula": "IF(amount < 0, -amount, 0)" }
      ]
    }
  ],
  "relations": [
    { "name": "account_transactions", "type": "1:N", "from": "accounts", "to": "transactions", "column": "account_id" }
  ]
}
```

## Names

Use lower-case names with letters, digits and `_` for tables and columns (`finance_transactions`, `booked_on`); `displayName` is what the app shows. The primary key is always the column `id` (`integer`, filled by the database, or `uuid`); a table without one gets it added automatically.

## Column types

| Type | Holds |
|---|---|
| `text` | text |
| `integer` | whole numbers |
| `float` | decimal numbers |
| `currency` | amounts (shown in euros); spending is usually negative |
| `boolean` | yes/no |
| `date` | `YYYY-MM-DD` |
| `datetime` | local wall time `YYYY-MM-DDTHH:mm` (database-filled values are UTC with `Z`) |
| `enum` | one of `values` |
| `reference` | the id of a row in `references` (a link) |
| `computed` | a formula, never stored (see topic `formulas`); `resultType` fixes its type |
| `json`, `uuid`, `file` | JSON value, UUID, file reference |

Column options: `nullable` (default true; `false` = required), `unique`, `default`, `values` (enum), `references` (reference), `formula` and `resultType` (computed), `relationName`.

## Links and relations

- A `reference` column links one row to another table. Formulas follow it with a dot (`account_id.name`) and the API can `include` the linked row.
- A `1:N` relation names the link from the other side: `from` is the parent (accounts), `to` the child (transactions), `column` the reference column on the child. Formulas on the parent then aggregate the children: `SUM(transactions.amount)`.
- A self-reference (categories with a parent category) is a reference column to the same table plus a `1:N` relation from the table to itself.
- `N:M` relations use a junction table in the schema (`junctionTable`) with two reference columns that form its key; `{ "name": "order_products", "type": "N:M", "from": "orders", "to": "products", "junctionTable": "_jt_orders_products" }`.

## Changing safely

- Rename a table or column with `"renamedFrom": "old_name"` so the data stays; without it the old one is dropped and a new one added.
- Changing a type converts values; lossy conversions and drops are **destructive**: `preview_schema_change` lists them, and `apply_schema_change` needs `confirm_destructive: true`. Tell the user before.
- `layout` (x, y, color) and `recordLabel` are designer settings: they never touch data.
- Computed columns cannot use each other in a circle; a formula that does not check blocks the apply (the error names the column).
- Saved queries, dashboards and forms follow table and column renames.
