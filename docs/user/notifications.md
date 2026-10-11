# Notifications

Bento can reach you by email, in Slack, or through a webhook (n8n, Home Assistant, Zapier, Make, your own script). You set up the **channels** once; **rules** say what Bento tells you over them, and when.

Channels are set up by admins under **Settings → Notifications**.

## Add A Channel

Choose **Add channel**, pick the kind, give it a name, and choose **Add channel**. Then choose **Send test**: the result shows right away ("Test sent in 180 ms" or why it failed), and the test arrives where the channel points.

### Email

- **Send to**: one or more addresses, separated by commas
- **SMTP server**, **Security** and **Port**: STARTTLS on 587 (most providers), TLS on 465, or none for a mail relay in your own network (port 25)
- **User** and **Password** of the account that sends; **From** is the sender address (the user name if it is an address)

For Gmail, Outlook and others with two-factor sign-in, create an **app password** and use that. Services such as Brevo, Mailgun or Postmark give you SMTP credentials too.

### Slack

In Slack, add the **Incoming Webhooks** app to a channel and copy the webhook address (`https://hooks.slack.com/services/…`). Mattermost and other Slack-compatible webhooks work the same way; for Discord, add `/slack` to the end of the Discord webhook address.

### Webhook

Bento sends a `POST` with JSON to the address:

```json
{
  "event": "test",
  "title": "Test from Bento",
  "text": "This is a test of the channel “n8n”. …",
  "link": null,
  "facts": [{ "label": "Channel", "value": "n8n" }],
  "channel": "n8n",
  "sentAt": "2026-10-11T16:59:23.858Z"
}
```

The header `X-Bento-Event` names the event. With a **signing secret**, `X-Bento-Signature` carries `sha256=` and the HMAC-SHA256 of the body with that secret, so the receiver can check the request came from Bento:

```python
import hmac, hashlib
expected = "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
ok = hmac.compare_digest(expected, request.headers["X-Bento-Signature"])
```

## Rules: When Bento Speaks

Under **Settings → Notification rules**, choose **Add rule**. A rule speaks **once** when its condition starts to hold and then stays quiet until the condition has cleared — no message every hour while you are over budget.

### A Value Crosses A Line

A number from a table, compared with a limit:

- **Table** and **Value (formula)**, written like a widget's formula: `-SUM(amount WHERE amount < 0)` for what was spent, `SUM(amount)` for the balance of the rows, `COUNT()` for how many there are
- **Rows of**: this month, this week, today, this year (by a date column of the table, in Europe/Berlin), or all rows
- **Tell me when it is** above, at or above, below … a **number**
- **Check**: every hour and after each scheduled import that brought rows, or once a day at a time

Example: *Spending this month* — Transactions, `-SUM(amount WHERE amount < 0)`, this month by date, above 2000.

A period starts afresh: a rule about this month speaks again next month when the value crosses the line again.

### A Budget Runs Over

Pick a dashboard's **budget widget** (the template's cash-flow dashboard has one) and at what share of the limit to tell you: **100** for over budget, **80** to hear about it early. Each category is told once a month — when groceries reach 80 %, you hear about groceries; when rent follows later, about rent.

### Something Fails

**A scheduled import fails** or **a price update fails**: you hear about the first failure with its reason; you hear again only after it worked in between.

### Check Now And Send Test

**Check now** looks at once and shows the value (and speaks if the rule newly holds). **Send test** sends what the rule would say now, whether or not it holds — to see the wording and that it arrives.

Messages link into Bento when the server knows its address (`PUBLIC_URL`, set on Coolify). Amounts are written in the workspace's format (Settings → Workspace).

## Secrets

The password, the Slack webhook address and the signing secret are stored encrypted (with the same key as the credential vault) and never shown again. When you edit a channel, leave such a field empty to keep what is stored. If the server's secret changes, enter them again.

Channels may point into your own network (a mail relay, Home Assistant on the LAN): only admins set them up.

## Log And Problems

**Log** on a channel lists what was sent, when, and what failed with the reason (wrong password, webhook removed, server not reachable). Channels can be turned off without deleting them. A notification that fails also appears under **Settings → Problems**.

## For Scripts

Rules: `/api/v1/admin/notifications/rules` (admins): `GET`, `POST { name, kind: "condition" | "budget" | "event", config, channels }`, `PUT /{id}`, `DELETE /{id}`, `POST /{id}/check`, `POST /{id}/test`. `config` for a condition: `source, formula, period?, dateColumn?, op, threshold, every?, at?`; for a budget: `dashboardId, widgetId, percent`; for an event: `event` (`import.failed`, `price.failed`).

Channels:

`/api/v1/admin/notifications/channels` (admins): `GET`, `POST { name, kind: "email" | "slack" | "webhook", config }`, `PUT /{id}`, `DELETE /{id}`, `POST /{id}/test`, `GET /{id}/deliveries`. Secret fields in `config` (`smtpPassword`, `url`, `secret`) are write-only: left out keeps them, `""` removes them; answers carry `hasPassword`, `hasUrl` and `hasSecret` instead.
