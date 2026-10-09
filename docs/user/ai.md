# AI

Bento's AI works where you work. It does not open a chat window. It explains the numbers on your dashboards and categorises bank bookings. It runs as **you**: it only sees what you can see, and it never changes anything without your OK.

For a full conversation with your data, connect your own Claude over MCP (see [Connections → AI apps](connections.md)).

## Setting It Up (Admins)

Open **AI** in the sidebar.

1. **Anthropic API key**: create one at console.anthropic.com, paste it and choose **Save key**.
   - Bento keeps the key encrypted in the credential vault and never shows it again.
   - **Replace key** swaps it for a new one; **Remove key** turns the AI off.
2. **Model**: Claude Opus 5.5 (default, most capable), Sonnet 5.5 (faster, cheaper) or Haiku 5.5 (cheapest).
3. **Monthly limit (US$)**: Bento stops calling the AI once this month's calls cost that much. It starts again next month or when you raise the limit. The default is $10.
4. **On**: switches the AI actions on and off for everyone, without removing the key.

The bar below the limit shows what this month has cost so far.

What the AI looks at is sent to Anthropic: the widget, its numbers, and the rows it looks up. Without a key, Bento sends nothing.

## Explain This

On a dashboard, hover over a widget and choose the ✨ button. On a phone, the button is always visible.

A panel opens and the AI answers in your language:

- what the number is made of
- what changed compared with the period before
- which rows or categories drive it

To find out, it looks at your schema, runs aggregations, and reads rows, just as you could. The line under the answer shows:

- the model
- how many lookups it made
- what the answer cost
- how long it took

Ask a follow-up in the field at the bottom, for example *why did it rise?* or *which shop was the biggest?*

Text widgets have no ✨ button. In edit mode the button is hidden as well.

If the AI is switched off, has no key, or this month's limit is used up, the panel says so and makes no call.

## Categorise Bank Bookings

The AI also helps when you import a bank export. In the import review, **✨ Suggest the rest** fills the category for the rows that your rules and earlier rows could not fill. **Categorise** in a table's toolbar does the same for rows already in the table.

It sees your categories, a sample of rows you categorised before, and the open rows (payee, purpose, amount, date). It uses no other data and changes nothing until you import or save.

Your corrections, and the AI's sure choices, become rules. Next month those payees need no AI at all. See [Importing Data](importing-data.md#categories-filled-in-for-you).

## Recent AI Calls

The AI page lists every call. For each call it shows:

- when it ran and which feature made it
- the model
- tokens in and out
- the cost
- whether it worked, and how long it took

Viewers and editors see their own calls; admins see everyone's.

## What Comes Next

- **Describe instead of click**: describe a widget or a formula in a sentence; Bento shows a live preview before saving, and you can undo it.
