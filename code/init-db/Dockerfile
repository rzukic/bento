# Postgres with Bento's role/schema bootstrap baked in, for hosts that build from
# the repo but do not ship repo files to bind mounts (Coolify):
#   docker build -t bento/postgres:local code/init-db
# The script runs once, when the data volume is empty.

FROM postgres:16-alpine

COPY *.sql /docker-entrypoint-initdb.d/
