# Importing Data From CSV, Excel And JSON

Bento imports CSV, Excel (`.xlsx`) and JSON files into a table, for example bank and broker exports into a `transactions` table, and can create that table from the file. Importing the same or an overlapping export again adds nothing twice, and can update rows that changed at the source.

## Create A Table From A File

On the Data Explorer overview, choose **New table from CSV** (admins only) and drop the file. Bento proposes a table:

- one column per file column, named after the header (`Betrag (EUR)` becomes `betrag_eur`; the header stays the display name)
- the detected type: date, number, amount (for headers like Betrag, EUR, amount), yes/no or text
- **required** where no cell of the file is empty; empty columns are left out
- for a category column, a **new table of values**: a `kategorie` table with one row per category, linked from each transaction. Any text column with repeated values can be stored that way, or linked to a table you already have

Change names, types and what is stored how, then choose **Create table and continue**. Bento creates the tables through the usual schema checks, fills the category table, and opens the import with everything already mapped — the rows are checked right away, so the next step is **Import**.

## Import A File

1. Open the table in the Data Explorer and choose **Import** in the toolbar
2. Drop the file onto the page or choose it
3. Check **How the file is read**. Bento detects:
   - the format: CSV, Excel or JSON
   - for Excel, the sheet (the one with the most rows, so a cover sheet is passed over) and the header row; dates and numbers come as Excel stores them, formulas as their last calculated value
   - for JSON, where the list of records is (**Records at**, e.g. `data.transactions`); nested values become columns like `payee.name`
   - for CSV, the encoding (UTF-8, or Windows-1252 as most German banks use)
   - the separator (`;`, `,`, tab or `|`)
   - the header row; lines above it (account number, balance) are skipped and listed
   - how numbers and dates are written: `-1.234,56` and `31.12.2026`, or `-1,234.56` and `2026-12-31`

   Change anything that looks wrong; the file is read again right away.
4. Under **Columns**, choose the file column for each table column, or **Don't import**. Bento suggests a mapping from the column names and, for bank exports, from what the headers mean (Buchungstag, Betrag, Verwendungszweck, Auftraggeber / Begünstigter, Kategorie). Required columns are marked. **Same value for every row…** fills a column without a file column — typically the **account** a bank export belongs to (choose the record) or a note (type it).
5. Choose **Check rows**. Nothing is written yet. Every row is listed as:
   - **New**: will be imported
   - **Already there**: the table has this row already (see below)
   - **Changed**: the table has a row with this key, but other values differ (see below)
   - **Problem**: a value cannot be read or is not allowed; the message says which and why
6. Choose **Import N rows**. All new rows (and, if chosen, the changed ones) are written together; if anything fails, none are.

Rows with problems are skipped, not imported half-way. Fix the file (or add the missing category) and import it again — the rows that went in the first time are recognised as already there.

## Categories, Filled In For You

If the table has a link or choice column such as **Category**, the review fills it for the new rows. This happens only when the file does not bring the value itself.

**Fill in** picks the column; a column named like a category is chosen by default. The values come from three places, in this order:

1. **Rules**: "payee contains *REWE*" → Groceries. Rules are your own and cost nothing.
2. **Earlier rows**: a payee you categorised before gets the same category again, as long as at least two thirds of its rows agree.
3. **✨ Suggest the rest**: Bento's AI chooses for the rows that are still open, using your categories and your earlier choices as examples. Each suggestion shows how sure it is. Unsure ones (below 60 %) are outlined, so check those first. The line above the rows shows what the AI cost. This needs Bento's AI to be set up (see [AI](ai.md)).

Next to each value you see where it came from: **Rule**, **Earlier**, **AI 92 %** or **You**. Change any of them in its row.

When you import, Bento remembers two kinds of choice as rules:

- **Your corrections**: Bento remembers the payee as a rule. Untick **Rule for “…”** under the row to skip one.
- **The AI's sure choices** (80 % and up, with a clear keyword such as *Netflix*): the checkbox above the rows switches this off.

Next month those payees are filled in by their rules, without asking the AI. **Rules (N)** above the rows lists the table's rules; delete one with the bin.

Without the AI you can still pick the values by hand; your choices become rules for the next import the same way.

### Rows Already In The Table

**Categorise** in the table's toolbar lists the newest rows (up to 200) that have no value yet. It works the same way: rules, earlier rows, ✨ for the rest. **Save N rows** writes the values and keeps your corrections as rules.

## Duplicates And Key Columns

The **Key** checkboxes decide when a row counts as already in the table: all key columns must match an existing row. By default every mapped column except links to other tables is a key column (booking date, counterparty, purpose, amount).

Repeated rows count: a file with two identical coffees on the same day imports both. Importing that file again adds neither. An export that overlaps the previous one (e.g. 15.09.–15.10. after 01.09.–30.09.) adds only the rows that are missing.

### Rows That Changed At The Source

Banks correct bookings, and a pending booking gets its final amount. Leave the value that may change out of the key (e.g. key on date, counterparty and purpose, not the amount) and tick **Update rows that changed**. A row whose key is in the table but whose other values differ is then shown as **Changed**, with the old value struck through next to the new one, and updated on import instead of skipped.

- Only the columns that differ are written. Columns you filled in Bento and that are not in the file, such as a category chosen by hand, stay as they are.
- An empty cell in the file changes nothing.
- The option is saved with the profile.

## Import From A URL

If your bank, broker or app offers the export at an address, choose **From a URL** under **File**, enter the address and choose **Fetch**. Bento's server fetches the file and the import continues as with an upload.

- Only `https` addresses, and never addresses inside your own network.
- Many exports need a key or token. Store it in the vault (**Functions → Credentials**, admins only) and add a header such as `Authorization: Bearer {{bank_token}}`; `{{name}}` is replaced by the stored value on the server and never reaches the browser. The header is sent only to the address's own host.
- The import page fetches files up to 7 MB; saved imports read up to 20 MB.

## Saved Imports That Run On Their Own

After fetching from a URL and choosing the columns, **Fetch and import on its own** saves the address, how the file is read, the columns and keys, **Update rows that changed** and the column filled in by your rules (e.g. Category). Choose when it runs:

- **Every day** at a time (in Europe/Berlin), for example 06:00. A time that has already passed today starts tomorrow.
- **Every few hours** (1 to 168); the first run is right away.
- **Only when I run it**.

Each run fetches the file, fills in categories from your rules and earlier rows (no AI; what stays open you can categorise later), imports what is new, updates what changed if chosen, and keeps a log.

**Saved imports** at the top of the import page lists them with their schedule, the next and the last run. **Run now** runs one at once, **Runs** shows the last ten, **Edit** fetches the file again and loads its settings into the page (change them, then **Update saved import**), **Pause** stops the schedule. A failed run says why (the address no longer works, the key expired, the file changed its columns) and also appears under **Settings → Problems**.

Imports with a key from the vault can only be created, changed or deleted by an admin.

## Links To Other Tables

A column that links to another table (e.g. **Category**) takes the record's name as it appears in Bento, such as `Lebensmittel`. Names are matched without regard to upper and lower case. A name that does not exist yet is reported as a problem; create the record first, then import again.

## Profiles

Save the settings (including the Excel sheet or the place of the JSON records), mapping and key columns as a **profile** (e.g. "DKB Girokonto"); a fixed value such as the account and **Update rows that changed** are part of it. Next month, choose the profile before or after choosing the file and go straight to **Check rows**. Profiles belong to a table and are shared by everyone who can import into it.

## Who Can Import

Importing needs the `editor` or `admin` role (or an `api` client). Viewers do not see a working import.

## Limits

- CSV, Excel (`.xlsx`, not the older `.xls`) and JSON
- Scheduled imports fetch plain addresses with headers; exports behind a login page or two-factor approval cannot be fetched
- Creating a table from a file needs the admin role; importing into an existing table needs `editor`
- Files up to about 7 MB on the import page (the request size limit is 10 MB); saved imports up to 20 MB

For scripts and other clients, see [Import API](using-the-api.md#import-csv-excel-and-json-files).
