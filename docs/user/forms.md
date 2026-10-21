# Forms

A form adds a row to one of your tables. It is built for quick entry, such as a transaction on the phone at the checkout, without opening the spreadsheet.

## Add A Row

Open a form from **Forms**, from **Quick add** in the sidebar, or from its button on a dashboard.

- **Amounts** have a **−/+** switch, so the phone's number pad (which has no minus) is enough. The **Add transaction** form starts at − for spending; tap + for income. Type `12,50` or `12.50`.
- **Links** to tables with few rows, such as accounts and categories, show as buttons. Larger tables get a search.
- **Dates** start today; **Today** and **Yesterday** are one tap.
- **Text** suggests what you typed before in this browser, such as payees.
- Fields marked * are required. Missing values are named before anything is sent. When the table refuses a value, the message appears under the field.

After **Save**, the form starts over for the next entry. The amount is empty and has the cursor, the date is today, and fields set to "last used" keep their value (e.g. the account). The line above the form says what was saved and links to the table.

From a dashboard, the form opens as a dialog. Saving closes it and the dashboard reloads, so the new row shows in its figures at once.

Adding rows needs the editor or admin role; viewers can open shared forms but not save.

## Make A Form

**Forms → New form**, choose a table, **Start**. The form starts with every column you can fill (not computed columns or keys the database assigns): dates start today, links at the value used last time.

- **Order**: drag a field by its grip (⋮⋮), or move it with the arrows. **Section** adds a heading, e.g. "More" for rarely used fields.
- **Label** and **Help**: what the field says, and a line under it.
- **Starts with**: empty, **Today**, **Now**, **Last used** (in this browser) or a **Fixed value**.
- **+/− switch** for numbers: starts at − (spending) or +.
- **Hidden**: not shown, filled by its default. This needs Today, Now or a fixed value, e.g. a source column that is always "phone".
- **Quick add** lists the form in the sidebar and on every dashboard that shows its table, as a source or through a link in a measure. A dashboard can choose differently: in edit mode, **Quick add** sets its buttons to automatic, to forms you pick (any form, also ones not in Quick add), or to none.
- **Shared** makes it available to everyone who signs in; only you change it.

The preview beside the form shows it as it will be filled. A column the table requires (not empty, no default) must be in the form, visible or hidden with a default; the builder says which one is missing.

Renaming the table or a column in the Schema Designer updates the form. A field whose column was deleted is left out, and the form says so.

## The Wealth Template

**Wealth & cash flow** (see [Dashboards](dashboards.md#wealth--cash-flow-in-one-click)) adds **Add transaction**, shared and in Quick add. It asks for amount (−/+), payee, category, account (last used), date (today), and under "More" a note. It shows on the Cash flow and Wealth dashboards.

## API

Forms are Form JSON under `/api/v1/forms`: the `table` and its `fields` in order, each `{ column, label?, help?, hidden?, default?: { kind: "today" | "now" | "last" | "value", value? }, sign?: "negative" | "positive" }` or `{ section }`, plus `pinned`. A filled form is an ordinary `POST /api/v1/data/{table}`, checked like any other row.

## A Public Form

**Share** on a form (its owner and the workspace's admins) makes a link anyone can fill in without an account — a booking request on your website, a sign-up, a feedback box:

- It adds a row to the form's table and shows nothing of it: the visitor sees the form, and the choices of its link fields by their name only (no other columns of those tables). They cannot read, change or delete rows.
- Only the form's fields are sent; a **hidden field with a value** (e.g. *channel = website*) always gets that value.
- **Entries per visitor an hour** (default 10) and at most 500 a day per link; a hidden trap field and a few seconds' minimum keep simple robots out. A determined attacker can still send up to the limits — use an expiry and revoke a link when it is abused.
- **Thank-you text** shown after sending; the form starts over for the next entry. Nothing the visitor types is kept in their browser.
- Expiry, revocation, opens and embedding (an `<iframe>` on your site) work as for dashboards.
