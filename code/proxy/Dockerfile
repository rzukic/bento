# Reverse proxy: the only container that publishes ports in the packaged stack.
#   docker build -t repere/proxy:local code/proxy

FROM caddy:2-alpine

COPY Caddyfile /etc/caddy/Caddyfile

# Fail the build early on a broken Caddyfile instead of at container start.
RUN caddy validate --config /etc/caddy/Caddyfile

EXPOSE 80 443

# Probe via "localhost" (not 127.0.0.1) so the Host header matches the always-present
# http://localhost site block even when DOMAIN is set to a real hostname.
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://localhost/proxy-healthz || exit 1
