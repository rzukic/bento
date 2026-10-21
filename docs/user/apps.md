# Apps: Your Tables On Your Devices

**Apps** (in the sidebar, admins) open Bento to other programs. The first is the **calendar**: tables become calendars in Apple Calendar, Google Calendar, Outlook or Thunderbird. The events stay rows in Bento — what you add or change in Bento, over the API or by asking Claude shows up on your devices.

## A Calendar App

Choose **Add calendar app**, give it a name, and add one or more calendars:

- **Table**: one row, one event — appointments, deadlines, birthdays, subscriptions.
- **Title**: the column with the event's name. For a title made of several columns, add a computed column to the table.
- **Starts**: a date column gives all-day events, a date-time column timed ones.
- **Ends** (optional): without it, a timed event lasts an hour (or the minutes you set), an all-day one a day.
- **All day when** (optional): a yes/no column that makes an event all-day.
- **Location** and **Notes** (optional).
- **Only rows where** (optional): e.g. *done is false*, *private is not true* (rows where it is empty count as "not").
- **Colour**: shown by calendar programs that support it.

Times are in the workspace's time zone (Settings → Workspace), the way Bento shows them.

## Devices

Each device gets its own password: type its name (*iPhone*, *Mac*) and choose **Add device**. Bento shows the password and the addresses **once** — copy them then. The list shows when each device last fetched; the ✕ takes a device's password back, and only that device stops.

### Subscribe (read-only)

Each calendar has a subscription address `…/feeds/<password>/<calendar>.ics`:

- **Apple Calendar (Mac)**: File → New Calendar Subscription.
- **iPhone / iPad**: Settings → Calendar → Accounts → Add Account → Other → Add Subscribed Calendar.
- **Google Calendar**: Other calendars → From URL (Google refreshes every few hours).
- **Outlook**: Add calendar → Subscribe from web.
- **Thunderbird**: New Calendar → On the Network → iCalendar (ICS).

A subscription shows the events; changes made on the device do not come back. Editing on the device comes with CalDAV.

## Claude

When an app is set up, Claude (over MCP) is told which tables are calendars and how their columns are meant — "add a dentist appointment on Friday at 10" lands in the right table and appears on your phone.

## For Scripts

`/api/v1/admin/apps` (admins): `GET`, `POST { name, kind: "calendar", config: { calendars: [{ name, table, title, start, end?, allDay?, location?, description?, duration?, color?, filters?: [{ column, op, value }] }] } }`, `PUT /{id}`, `DELETE /{id}`, `POST /{id}/passwords { name }` (answers with the password once and the addresses), `DELETE /{id}/passwords/{passwordId}`.
