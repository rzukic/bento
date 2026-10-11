# Notifications

Bento can reach you by email, in Slack, or through a webhook (n8n, Home Assistant, Zapier, Make, your own script). You set up the **channels** once; what Bento tells you over them, and when, follows with rules and the monthly review.

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

## Secrets

The password, the Slack webhook address and the signing secret are stored encrypted (with the same key as the credential vault) and never shown again. When you edit a channel, leave such a field empty to keep what is stored. If the server's secret changes, enter them again.

Channels may point into your own network (a mail relay, Home Assistant on the LAN): only admins set them up.

## Log And Problems

**Log** on a channel lists what was sent, when, and what failed with the reason (wrong password, webhook removed, server not reachable). Channels can be turned off without deleting them. A notification that fails also appears under **Settings → Problems**.

## For Scripts

`/api/v1/admin/notifications/channels` (admins): `GET`, `POST { name, kind: "email" | "slack" | "webhook", config }`, `PUT /{id}`, `DELETE /{id}`, `POST /{id}/test`, `GET /{id}/deliveries`. Secret fields in `config` (`smtpPassword`, `url`, `secret`) are write-only: left out keeps them, `""` removes them; answers carry `hasPassword`, `hasUrl` and `hasSecret` instead.
