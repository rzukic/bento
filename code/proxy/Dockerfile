# Reverse proxy: the only container that publishes ports in the packaged stack.
#   docker build -t bento/proxy:local code/proxy

FROM caddy:2-alpine

COPY routes.caddy Caddyfile.local Caddyfile.domain /etc/caddy/
COPY docker-entrypoint.sh /docker-entrypoint.sh

# Fail the build early on a broken config instead of at container start.
RUN chmod +x /docker-entrypoint.sh \
    && cd /etc/caddy \
    && caddy validate --config Caddyfile.local --adapter caddyfile \
    && DOMAIN=example.com caddy validate --config Caddyfile.domain --adapter caddyfile

EXPOSE 80 443

# Probe via "localhost" (not 127.0.0.1): in domain mode only the http://localhost
# block answers /proxy-healthz on port 80.
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://localhost/proxy-healthz || exit 1

ENTRYPOINT ["/docker-entrypoint.sh"]
