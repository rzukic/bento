# Importing Data From CSV

Bento imports CSV files into an existing table, for example bank and broker exports into a `transactions` table. Importing the same or an overlapping export again adds nothing twice.

## Import A File

1. Open the table in the Data Explorer and choose **Import** in the toolbar
2. Drop the CSV file onto the page or choose it
3. Check **How the file is read**. Bento detects:
   - the encoding (UTF-8, or Windows-1252 as most German banks use)
   - the separator (`;`, `,`, tab or `|`)
   - the header row; lines above it (account number, balance) are skipped and listed
   - how numbers and dates are written: `-1.234,56` and `31.12.2026`, or `-1,234.56` and `2026-12-31`

   Change anything that looks wrong; the file is read again right away.
4. Under **Columns**, choose the file column for each table column, or **Don't import**. Bento suggests a mapping from the column names and, for bank exports, from what the headers mean (Buchungstag, Betrag, Verwendungszweck, Auftraggeber / Begünstigter, Kategorie). Required columns are marked.
5. Choose **Check rows**. Nothing is written yet. Every row is listed as:
   - **New**: will be imported
   - **Already there**: the table has this row already (see below)
   - **Problem**: a value cannot be read or is not allowed; the message says which and why
6. Choose **Import N rows**. All new rows are written together; if anything fails, none are.

Rows with problems are skipped, not imported half-way. Fix the file (or add the missing category) and import it again — the rows that went in the first time are recognised as already there.

## Duplicates And Key Columns

The **Key** checkboxes decide when a row counts as already in the table: all key columns must match an existing row. By default every mapped column except links to other tables is a key column (booking date, counterparty, purpose, amount).

Repeated rows count: a file with two identical coffees on the same day imports both. Importing that file again adds neither. An export that overlaps the previous one (e.g. 15.09.–15.10. after 01.09.–30.09.) adds only the rows that are missing.

## Links To Other Tables

A column that links to another table (e.g. **Category**) takes the record's name as it appears in Bento, such as `Lebensmittel`. Names are matched without regard to upper and lower case. A name that does not exist yet is reported as a problem; create the record first, then import again.

## Profiles

Save the settings, mapping and key columns as a **profile** (e.g. "DKB Girokonto"). Next month, choose the profile before or after choosing the file and go straight to **Check rows**. Profiles belong to a table and are shared by everyone who can import into it.

## Who Can Import

Importing needs the `editor` or `admin` role (or an `api` client). Viewers do not see a working import.

## Limits

- CSV only for now; JSON and Excel files, importing from a URL and scheduled imports come later
- The table must exist; create it in the schema designer first
- Files up to about 7 MB (the request size limit is 10 MB)

For scripts and other clients, see [Import API](using-the-api.md#import-csv-files).
