# Build context: code/src/frontend (package.json, pnpm-lock.yaml and pnpm-workspace.yaml live here).
#   docker build -t repere/frontend:local -f code/src/frontend/Dockerfile code/src/frontend

FROM node:20-alpine AS build
WORKDIR /app

# Keep in sync with "packageManager" in package.json. Corepack reads that field at
# run time anyway; preparing the same version here just avoids a second download.
ARG PNPM_VERSION=12.6.0
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0
RUN corepack enable && corepack prepare "pnpm@${PNPM_VERSION}" --activate

COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile

COPY . .
RUN pnpm run build

# alpine-slim is the official nginx image without the optional dynamic modules
# (njs, geoip, xslt, image filter). gzip is core nginx, so nginx.conf needs nothing
# else, and the runtime image stays well below the 50 MB target in Part 13 R13.1.
FROM nginx:alpine-slim AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/ /usr/share/nginx/html/

EXPOSE 80

# nginx:alpine ships busybox wget; /healthz is served by nginx.conf.
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1/healthz || exit 1
