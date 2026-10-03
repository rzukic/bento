#!/bin/sh
# Pick the Caddy config from DOMAIN:
#   unset / localhost / an IP address → Caddyfile.local  (plain HTTP on :80 for any host)
#   anything else                    → Caddyfile.domain (automatic HTTPS for that hostname)
set -eu

domain="${DOMAIN:-localhost}"
case "$domain" in
  ""|localhost|127.0.0.1|::1|*.localhost)
    config=/etc/caddy/Caddyfile.local
    ;;
  *)
    if printf '%s' "$domain" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'; then
      # Let's Encrypt does not issue certificates for bare IPs.
      config=/etc/caddy/Caddyfile.local
    else
      config=/etc/caddy/Caddyfile.domain
    fi
    ;;
esac

echo "bento-proxy: DOMAIN=${domain} -> ${config}" >&2
exec caddy run --config "$config" --adapter caddyfile
