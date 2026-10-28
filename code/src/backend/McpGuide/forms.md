# Forms (Form JSON)

Entry forms such as "Log hours" or "Add expense": a table and its fields in order. `list_forms` reads them; `create_form`, `update_form`, `delete_form` change them (editors and admins; only the owner changes a form).

```json
{
  "table": "time_entries",
  "pinned": true,
  "fields": [
    { "column": "hours", "label": "Hours" },
    { "column": "project_id", "label": "Project", "default": { "kind": "last" } },
    { "column": "status", "default": { "kind": "value", "value": "billable" } },
    { "section": "Details" },
    { "column": "date", "default": { "kind": "today" } },
    { "column": "note", "help": "What you worked on" }
  ]
}
```

- A field: `column`, `label?`, `help?`, `hidden?` (needs a default other than `last`), `default?` (`{kind: "today"}` for dates, `{kind: "now"}` for date-times, `{kind: "last"}` = the value used last time, `{kind: "value", value: …}`), `sign?` (`negative`/`positive`, numbers only: a +/− switch, e.g. money going out entered as a positive number but stored negative).
- `{ "section": "Title" }` is a heading between fields.
- Every required column (not nullable, no default) must be in the form; computed columns and `id` cannot be.
- `pinned: true` lists it under Quick add and as a button on dashboards showing its table.
- `isShared` (on the form, not in the JSON) makes it visible to everyone.

Filling a form is just adding a row: use `create_row` with the same columns.
