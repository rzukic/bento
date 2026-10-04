# Schema Designer

> Create and change your data model from the browser.

---

## Where To Find It

Open the app and choose **Schema** from the main navigation. The schema workbench is available to admin users.

The workbench has two modes (Visual is the default; the page remembers your choice):
- **Visual**: an ERD canvas for tables, columns, relations, and layout
- **JSON**: the raw Model JSON editor for direct edits, import, export, and power-user workflows

Both modes edit the same draft. Switching modes does not create a separate copy. The header always shows the draft's state ("No unapplied changes", "2 unapplied changes", "1 unapplied change · deletes data", …).

Leaving the page with an unapplied draft asks first (**Keep editing** / **Discard changes**); reloading or closing the tab gets the browser's own prompt.

---

## Create Tables Visually

In Visual mode, use **Add table** to create a starter table; it is placed in free space on the canvas. Select a table card to edit it in the inspector, which opens only while something is selected (click empty canvas or ✕ to close it).

From the inspector you can:
- rename the table
- set display name and description
- add, rename, reorder, and delete columns
- set column type, required/nullable behavior, uniqueness, defaults, enum values, and reference targets
- write the formula of a **computed** column with suggestions (columns, links, functions), live checks and a preview of its values; its values are then live in the Data Explorer (see [Formulas](formulas.md))
- duplicate or delete the selected table (deleting asks for confirmation and lists the relations and reference columns that go with it)
- choose its **Record label**: the column that names a record everywhere in the app (FK chips, pickers, linked records, the record editor title), plus an optional column **shown with it**, e.g. products named by `name` with `sku` beside it. "Automatic" uses the first text column

When you rename a table or column intentionally, keep the rename hint so the backend can review it as a rename instead of a drop-and-add where possible.

---

## Create Relations

Select a table, then use the relation form in the Visual inspector.

Supported relation flows:
- **1:N**: choose the parent table and child table. The designer creates or reuses a reference column on the child table and adds the supporting relation metadata.
- **Self-referential 1:N**: choose the same table as parent and child, then use explicit names/accessors so the relation stays readable.
- **N:M**: choose two endpoint tables. The designer creates a visible junction table with reference columns and wires the relation through it. You can rename that junction table; the backend treats it as an N:M junction table because the relation references it, not because it uses a special prefix. A junction table's inspector lists the relation it serves.

Every N:M relation has its junction table in the schema. An N:M relation written in JSON without `junctionTable` (as in older files) gets its junction table added automatically, with the same table the database already uses, so nothing changes in the data. Renaming a junction table's two key columns rebuilds the junction and loses the stored links; the preview marks that as destructive.

Relation lines are selectable on the canvas. Selecting a relation opens its summary in the inspector, where you can rename the relation and review resolved accessor details.

Advanced relation settings such as cascade behavior are not yet editable from the visual inspector.

---

## Arrange The Canvas

Drag table cards to reposition them; a card dropped onto another moves to the nearest free spot. Scroll or two-finger swipe to pan, pinch or ⌘/Ctrl + scroll to zoom around the pointer, and use **Fit** to see every table. The canvas opens fitted and keeps your view after Apply and Refresh.

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

Layout and record-label metadata are portable through JSON import/export. They are ignored by database migrations, so moving a card, changing its color or its record label does not change your data tables; Apply saves them as "designer settings".

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

Use **Preview** before applying changes. Preview opens a side panel that asks the backend migration engine what would change without mutating the database. Problems are also listed in a card on the canvas; in Visual mode they name the table or column ("widgets.name").

Preview can show:
- tables, columns, indexes, and relations that will be added, removed, renamed, or changed
- destructive warnings
- lossy conversion examples where available
- validation issues
- no-op states when only layout changed or nothing changed

Use **Apply** after reviewing the preview. When the draft deletes data, Apply turns red and the status says so. Destructive changes require explicit confirmation before the backend runs the migration. If validation or apply fails, the workbench keeps your draft so you can fix it.

After a successful apply, the app refreshes the live schema snapshot so the Data Explorer and API routes use the updated model.
