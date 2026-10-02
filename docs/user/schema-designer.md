# Schema Designer

> Create and change your data model from the browser.

---

## Where To Find It

Open the app and choose **Model** from the main navigation. The schema workbench is available to admin users.

The workbench has two modes:
- **Visual**: an ERD canvas for tables, columns, relations, and layout
- **JSON**: the raw Model JSON editor for direct edits, import, export, and power-user workflows

Both modes edit the same draft. Switching modes does not create a separate copy.

---

## Create Tables Visually

In Visual mode, use **Add table** to create a starter table. Select the table card to edit it in the inspector.

From the inspector you can:
- rename the table
- set display name and description
- add, rename, reorder, and delete columns
- set column type, required/nullable behavior, uniqueness, defaults, enum values, and reference targets
- duplicate or delete the selected table

When you rename a table or column intentionally, keep the rename hint so the backend can review it as a rename instead of a drop-and-add where possible.

---

## Create Relations

Select a table, then use the relation form in the Visual inspector.

Supported relation flows:
- **1:N**: choose the parent table and child table. The designer creates or reuses a reference column on the child table and adds the supporting relation metadata.
- **Self-referential 1:N**: choose the same table as parent and child, then use explicit names/accessors so the relation stays readable.
- **N:M**: choose two endpoint tables. The designer creates a visible junction table with reference columns and wires the relation through it. You can rename that junction table; the backend treats it as an N:M junction table because the relation references it, not because it uses a special prefix.

Relation lines are selectable on the canvas. Selecting a relation opens its summary in the inspector, where you can rename the relation and review resolved accessor details.

Advanced relation settings such as cascade behavior are not yet editable from the visual inspector.

---

## Arrange The Canvas

Drag table cards to reposition them. Use the canvas controls to pan, zoom, fit, or reset the view.

The table inspector also lets you set a layout color. Positions and colors are stored in the Model JSON as table `layout` metadata:

```json
{
  "name": "customers",
  "layout": {
    "x": 120,
    "y": 80,
    "color": "#2563eb"
  }
}
```

Layout metadata is portable through JSON import/export. It is ignored by database migrations, so moving a card or changing its color does not change your data tables.

---

## Use JSON Mode

Use JSON mode when you need direct control over the Model JSON or want to round-trip a file.

JSON mode supports:
- formatting
- import and export
- syntax diagnostics
- backend validation diagnostics
- the same preview and apply flow as Visual mode

If the JSON draft is temporarily invalid, Visual mode shows a blocker instead of discarding your text. Fix the JSON syntax to return to Visual editing.

---

## Preview And Apply

Use **Preview** before applying changes. Preview asks the backend migration engine what would change without mutating the database.

Preview can show:
- tables, columns, indexes, and relations that will be added, removed, renamed, or changed
- destructive warnings
- lossy conversion examples where available
- validation issues
- no-op states when only layout changed or nothing changed

Use **Apply** after reviewing the preview. Destructive changes require explicit confirmation before the backend runs the migration. If validation or apply fails, the workbench keeps your draft so you can fix it.

After a successful apply, the app refreshes the live schema snapshot so the Data Explorer and API routes use the updated model.
