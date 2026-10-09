# Forms (Form JSON)

Entry forms such as "Add transaction": a table and its fields in order. `list_forms` reads them; `create_form`, `update_form`, `delete_form` change them (editors and admins; only the owner changes a form).

```json
{
  "table": "transactions",
  "pinned": true,
  "fields": [
    { "column": "amount", "label": "Amount", "sign": "negative" },
    { "column": "payee" },
    { "column": "category_id", "label": "Category", "default": { "kind": "last" } },
    { "section": "Details" },
    { "column": "date", "default": { "kind": "today" } },
    { "column": "account_id", "default": { "kind": "last" } },
    { "column": "purpose", "help": "Optional note" }
  ]
}
```

- A field: `column`, `label?`, `help?`, `hidden?` (needs a default other than `last`), `default?` (`{kind: "today"}` for dates, `{kind: "now"}` for date-times, `{kind: "last"}` = the value used last time, `{kind: "value", value: …}`), `sign?` (`negative`/`positive`, numbers only: a +/− switch, spending is negative).
- `{ "section": "Title" }` is a heading between fields.
- Every required column (not nullable, no default) must be in the form; computed columns and `id` cannot be.
- `pinned: true` lists it under Quick add and as a button on dashboards showing its table.
- `isShared` (on the form, not in the JSON) makes it visible to everyone.

Filling a form is just adding a row: use `create_row` with the same columns.
