#!/usr/bin/env bash
#
# Packaged MVP 2 smoke: brings up the full browser-first stack (proxy + frontend
# + backend, optionally Postgres) and drives it through the proxy only — the way
# a user's browser would. No container port other than the proxy's is touched.

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CODE_DIR="${REPO_ROOT}/code"
DEFAULT_ENV_FILE="${CODE_DIR}/.env.example"
FULL_STACK_FILE="${CODE_DIR}/docker-compose.yml"
POSTGRES_OVERRIDE_FILE="${CODE_DIR}/docker-compose.postgres.yml"

usage() {
  cat <<'EOF'
Usage:
  code/scripts/packaged-mvp2-smoke.sh <sqlite|postgres> [options]

Starts the full stack from code/docker-compose.yml (plus the Postgres override
for the postgres variant) and validates the MVP 2 product path through the
proxy: SPA shell, setup status, bootstrap admin, login, sample data, data
queries, a schema change, saved views, and the initialised state.

Options:
  --env-file <path>         Compose env file to use (default: code/.env.example)
  --project-name <name>     Explicit Docker Compose project name
  --timeout-seconds <n>     Seconds to wait for the stack to become healthy (default: 420)
  --host-http-port <n>      Host port for the proxy's HTTP listener (default: 18080)
  --host-https-port <n>     Host port for the proxy's HTTPS listener (default: 18443)
  --log-dir <path>          Directory for captured failure logs
  --skip-startup            Assume the stack is already running on the given ports
  --keep-running            Leave the stack running after the smoke completes
  --help                    Show this help

Examples:
  code/scripts/packaged-mvp2-smoke.sh sqlite
  code/scripts/packaged-mvp2-smoke.sh postgres --host-http-port 8081 --host-https-port 8444
  code/scripts/packaged-mvp2-smoke.sh sqlite --keep-running   # then open http://localhost:18080
EOF
}

if [[ $# -eq 1 && "$1" == "--help" ]]; then
  usage
  exit 0
fi

if [[ $# -lt 1 ]]; then
  usage
  exit 1
fi

VARIANT="$1"
shift

case "$VARIANT" in
  sqlite|postgres) ;;
  *)
    echo "Unsupported variant: $VARIANT" >&2
    usage
    exit 1
    ;;
esac

ENV_FILE="$DEFAULT_ENV_FILE"
PROJECT_NAME="repere-mvp2-${VARIANT}"
TIMEOUT_SECONDS=420
HOST_HTTP_PORT=18080
HOST_HTTPS_PORT=18443
LOG_DIR=""
SKIP_STARTUP=0
KEEP_RUNNING=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env-file) ENV_FILE="$2"; shift 2 ;;
    --project-name) PROJECT_NAME="$2"; shift 2 ;;
    --timeout-seconds) TIMEOUT_SECONDS="$2"; shift 2 ;;
    --host-http-port) HOST_HTTP_PORT="$2"; shift 2 ;;
    --host-https-port) HOST_HTTPS_PORT="$2"; shift 2 ;;
    --log-dir) LOG_DIR="$2"; shift 2 ;;
    --skip-startup) SKIP_STARTUP=1; shift ;;
    --keep-running) KEEP_RUNNING=1; shift ;;
    --help) usage; exit 0 ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Env file does not exist: $ENV_FILE" >&2
  exit 1
fi

for numeric in TIMEOUT_SECONDS HOST_HTTP_PORT HOST_HTTPS_PORT; do
  if ! [[ "${!numeric}" =~ ^[0-9]+$ ]] || [[ "${!numeric}" -le 0 ]]; then
    echo "${numeric} must be a positive integer." >&2
    exit 1
  fi
done

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command not found: $1" >&2
    exit 1
  fi
}

require_command docker
require_command curl
require_command jq
require_command mktemp
require_command grep

if [[ -z "$LOG_DIR" ]]; then
  LOG_DIR="/tmp/${PROJECT_NAME}"
fi
mkdir -p "$LOG_DIR"

WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/packaged-mvp2-smoke.XXXXXX")"
TEMP_ENV_FILE="$(mktemp "${TMPDIR:-/tmp}/packaged-mvp2-smoke-env.XXXXXX")"
cp "$ENV_FILE" "$TEMP_ENV_FILE"

BASE_URL="http://localhost:${HOST_HTTP_PORT}"
API_ROOT="${BASE_URL}/api/v1"
STACK_AVAILABLE=0
STACK_OWNED=0
CURRENT_STEP="initialization"
LAST_RESPONSE_FILE=""
LAST_HEADERS_FILE=""
RESPONSE_COUNTER=0
TOKEN=""
ADMIN_USERNAME="admin"
ADMIN_PASSWORD="smoke-admin-$(LC_ALL=C tr -dc 'a-z0-9' </dev/urandom | head -c 12 || true)"
SCHEMA_VERSION_AFTER_SAMPLE=""

# ── env file handling ────────────────────────────────────────────────────────

set_env_var() {
  local file="$1" key="$2" value="$3"
  local updated="${file}.tmp"
  grep -v "^${key}=" "$file" > "$updated" || true
  printf '%s=%s\n' "$key" "$value" >> "$updated"
  mv "$updated" "$file"
}

generate_secret() {
  local secret
  secret="$(
    set +o pipefail
    LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32
  )"
  printf '%s' "$secret"
}

is_insecure_postgres_secret() {
  local value="${1:-}"
  [[ -z "$value" || "$value" == "CHANGE_ME" || "$value" == "repere_dev" ]]
}

# The ports are pinned here so a second stack (or the user's real one on 80/443)
# never collides with the smoke. The env file's values are overridden on purpose.
set_env_var "$TEMP_ENV_FILE" "HOST_HTTP_PORT" "$HOST_HTTP_PORT"
set_env_var "$TEMP_ENV_FILE" "HOST_HTTPS_PORT" "$HOST_HTTPS_PORT"
set_env_var "$TEMP_ENV_FILE" "DOMAIN" "localhost"

if [[ "$VARIANT" == "postgres" ]]; then
  for key in POSTGRES_SUPERUSER_PASSWORD REPERE_SYSTEM_PASSWORD REPERE_DATA_PASSWORD; do
    value="${!key:-}"
    if is_insecure_postgres_secret "$value"; then
      value="$(grep "^${key}=" "$TEMP_ENV_FILE" | tail -n 1 | cut -d= -f2- || true)"
    fi
    if is_insecure_postgres_secret "$value"; then
      value="$(generate_secret)"
      printf '[packaged-mvp2-smoke] generated ephemeral %s for the disposable Postgres stack\n' "$key"
    fi
    set_env_var "$TEMP_ENV_FILE" "$key" "$value"
  done
fi

COMPOSE_FILES=("$FULL_STACK_FILE")
if [[ "$VARIANT" == "postgres" ]]; then
  COMPOSE_FILES+=("$POSTGRES_OVERRIDE_FILE")
fi

compose_cmd() {
  local args=(docker compose --project-name "$PROJECT_NAME" --env-file "$TEMP_ENV_FILE")
  local file
  for file in "${COMPOSE_FILES[@]}"; do
    args+=(-f "$file")
  done
  args+=("$@")
  # Shell-exported HOST_* values would win over the env file; clear them so the
  # pinned ports above are what compose sees.
  env -u HOST_HTTP_PORT -u HOST_HTTPS_PORT -u DOMAIN "${args[@]}"
}

# ── helpers ──────────────────────────────────────────────────────────────────

timestamp() { date -u +"%Y%m%dT%H%M%SZ"; }
log_note() { printf '[packaged-mvp2-smoke] %s\n' "$*"; }

response_file() {
  RESPONSE_COUNTER=$((RESPONSE_COUNTER + 1))
  printf '%s/response-%04d\n' "$WORK_DIR" "$RESPONSE_COUNTER"
}

# http_request <expected_status> <method> <url> <none|bearer> [curl args...]
http_request() {
  local expected_status="$1" method="$2" url="$3" auth_mode="$4"
  shift 4

  local base
  base="$(response_file)"
  LAST_RESPONSE_FILE="${base}.body"
  LAST_HEADERS_FILE="${base}.headers"

  local curl_args=(
    --silent --show-error --globoff
    --output "$LAST_RESPONSE_FILE"
    --dump-header "$LAST_HEADERS_FILE"
    --write-out '%{http_code}'
    -X "$method"
  )
  if [[ "$auth_mode" == "bearer" ]]; then
    curl_args+=(-H "Authorization: Bearer $TOKEN")
  fi
  curl_args+=("$@" "$url")

  local status_code
  status_code="$(curl "${curl_args[@]}")"
  if [[ "$status_code" != "$expected_status" ]]; then
    echo "Expected HTTP ${expected_status} for ${method} ${url} during step '${CURRENT_STEP}', got ${status_code}." >&2
    cat "$LAST_RESPONSE_FILE" >&2 || true
    echo >&2
    return 1
  fi
}

api_request() {
  local expected_status="$1" method="$2" path="$3" auth_mode="$4"
  shift 4
  http_request "$expected_status" "$method" "${API_ROOT}${path}" "$auth_mode" -H 'Accept: application/json' "$@"
}

assert_json() {
  local filter="$1" description="$2"
  if ! jq -e "$filter" "$LAST_RESPONSE_FILE" >/dev/null; then
    echo "Assertion failed during step '${CURRENT_STEP}': ${description}" >&2
    cat "$LAST_RESPONSE_FILE" >&2 || true
    echo >&2
    return 1
  fi
}

assert_body_contains() {
  local needle="$1" description="$2"
  if ! grep -q -- "$needle" "$LAST_RESPONSE_FILE"; then
    echo "Assertion failed during step '${CURRENT_STEP}': ${description}" >&2
    head -c 400 "$LAST_RESPONSE_FILE" >&2 || true
    echo >&2
    return 1
  fi
}

assert_header() {
  local pattern="$1" description="$2"
  if ! grep -i -q -- "$pattern" "$LAST_HEADERS_FILE"; then
    echo "Assertion failed during step '${CURRENT_STEP}': ${description}" >&2
    cat "$LAST_HEADERS_FILE" >&2 || true
    return 1
  fi
}

json_value() { jq -r "$1" "$LAST_RESPONSE_FILE"; }

capture_debug_artifacts() {
  local artifact_dir="${LOG_DIR}/$(timestamp)"
  mkdir -p "$artifact_dir"
  log_note "capturing diagnostics to ${artifact_dir}"

  {
    echo "project_name=${PROJECT_NAME}"
    echo "variant=${VARIANT}"
    echo "current_step=${CURRENT_STEP}"
    echo "env_file=${ENV_FILE}"
    echo "base_url=${BASE_URL}"
    echo "host_http_port=${HOST_HTTP_PORT}"
    echo "host_https_port=${HOST_HTTPS_PORT}"
  } > "${artifact_dir}/context.txt"

  if [[ -n "$LAST_RESPONSE_FILE" && -f "$LAST_RESPONSE_FILE" ]]; then
    cp "$LAST_RESPONSE_FILE" "${artifact_dir}/last-response.txt"
  fi
  if [[ -n "$LAST_HEADERS_FILE" && -f "$LAST_HEADERS_FILE" ]]; then
    cp "$LAST_HEADERS_FILE" "${artifact_dir}/last-response-headers.txt"
  fi

  curl --silent --show-error "${BASE_URL}/proxy-healthz" > "${artifact_dir}/proxy-healthz.txt" 2>&1 || true
  curl --silent --show-error "${API_ROOT}/admin/health" > "${artifact_dir}/health.json" 2>&1 || true

  if [[ "$STACK_AVAILABLE" -eq 1 ]]; then
    compose_cmd ps > "${artifact_dir}/compose-ps.txt" 2>&1 || true
    compose_cmd logs --no-color > "${artifact_dir}/compose-logs.txt" 2>&1 || true
    local service
    for service in proxy frontend backend postgres; do
      local container_id
      container_id="$(compose_cmd ps -q "$service" 2>/dev/null || true)"
      if [[ -n "$container_id" ]]; then
        docker inspect "$container_id" > "${artifact_dir}/${service}-inspect.json" 2>&1 || true
        docker logs "$container_id" > "${artifact_dir}/${service}-logs.txt" 2>&1 || true
      fi
    done
  fi
}

cleanup() {
  if [[ "$STACK_OWNED" -eq 1 && "$KEEP_RUNNING" -eq 0 ]]; then
    log_note "tearing down ${PROJECT_NAME}"
    compose_cmd down -v --remove-orphans >/dev/null 2>&1 || true
  fi
  rm -f "$TEMP_ENV_FILE"
  rm -rf "$WORK_DIR"
}

on_error() {
  local exit_code=$?
  echo "Packaged MVP 2 smoke failed during step '${CURRENT_STEP}'." >&2
  capture_debug_artifacts
  cleanup
  exit "$exit_code"
}

trap on_error ERR

run_step() {
  local description="$1"
  shift
  CURRENT_STEP="$description"
  log_note "$description"
  "$@"
}

# ── steps ────────────────────────────────────────────────────────────────────

start_stack() {
  local started_at
  started_at="$(date +%s)"
  compose_cmd up -d --build --wait --wait-timeout "$TIMEOUT_SECONDS"
  STACK_AVAILABLE=1
  STACK_OWNED=1
  log_note "stack healthy after $(( $(date +%s) - started_at ))s (including image builds)"
}

wait_for_proxy() {
  local deadline=$(( $(date +%s) + TIMEOUT_SECONDS ))
  until curl --silent --fail --output /dev/null "${BASE_URL}/proxy-healthz"; do
    if [[ "$(date +%s)" -ge "$deadline" ]]; then
      echo "Proxy did not answer on ${BASE_URL}/proxy-healthz within ${TIMEOUT_SECONDS}s." >&2
      return 1
    fi
    sleep 2
  done
  STACK_AVAILABLE=1
}

check_only_proxy_publishes_ports() {
  if [[ "$STACK_OWNED" -eq 0 ]]; then
    log_note "skipping port check (stack not started by this script)"
    return 0
  fi
  local published
  published="$(compose_cmd ps --format json \
    | jq -r 'select((.Publishers // []) | map(select(.PublishedPort > 0)) | length > 0) | .Service' \
    | sort -u | tr '\n' ' ' | sed 's/ *$//')"
  if [[ "$published" != "proxy" ]]; then
    echo "Expected only the proxy to publish host ports, got: '${published}'" >&2
    return 1
  fi
}

check_spa_shell() {
  http_request 200 GET "${BASE_URL}/" none
  assert_body_contains '<div id="root"' 'root document should be the SPA shell'
  assert_header '^Cache-Control: no-cache' 'index.html must not be cached'

  # Deep client routes are served by the SPA fallback, not a 404.
  http_request 200 GET "${BASE_URL}/data/customers" none
  assert_body_contains '<div id="root"' 'deep route should fall back to the SPA shell'
  http_request 200 GET "${BASE_URL}/setup" none
  assert_body_contains '<div id="root"' '/setup should fall back to the SPA shell'

  # Hashed assets are immutable.
  local asset_path
  asset_path="$(grep -o '/assets/index-[^"]*\.js' "$LAST_RESPONSE_FILE" | head -n 1 || true)"
  if [[ -z "$asset_path" ]]; then
    echo "Could not find the hashed entry script in index.html." >&2
    return 1
  fi
  http_request 200 GET "${BASE_URL}${asset_path}" none
  assert_header '^Cache-Control: .*immutable' 'hashed asset should carry an immutable cache header'

  # Requests by IP must not be redirected to HTTPS in local mode.
  http_request 200 GET "http://127.0.0.1:${HOST_HTTP_PORT}/" none
  assert_body_contains '<div id="root"' 'IP-based access should serve the SPA over plain HTTP'
}

check_fresh_setup_status() {
  api_request 200 GET "/setup/status" none
  assert_json '.initialized == false' 'fresh installation should not be initialised'
  assert_json '.auth_mode == "standard"' 'auth mode should be standard'
  assert_json '.registration_bootstrap_open == true' 'bootstrap registration should be open'
  assert_json ".database_provider == \"${VARIANT}\"" "database provider should be ${VARIANT}"
  assert_json '.schema_version == 0' 'schema version should be 0'
  assert_json '.sample_data_available == true' 'sample data should be loadable'
}

check_public_health() {
  api_request 200 GET "/admin/health" none
  assert_json '.status == "healthy"' 'health status should be healthy'
  assert_json '.database.system_pool == "connected"' 'system pool should be connected'
  assert_json '.database.data_pool == "connected"' 'data pool should be connected'
}

check_anonymous_is_rejected() {
  api_request 401 GET "/admin/schema" none
  api_request 401 POST "/setup/sample-data" none
}

register_bootstrap_admin() {
  api_request 201 POST "/auth/register" none \
    -H "Content-Type: application/json" \
    --data "{\"username\":\"${ADMIN_USERNAME}\",\"password\":\"${ADMIN_PASSWORD}\"}"
  assert_json '.role == "admin"' 'first user should be an admin'
  assert_json '.bootstrap_admin == true' 'first user should be the bootstrap admin'
}

login_admin() {
  api_request 200 POST "/auth/login" none \
    -H "Content-Type: application/json" \
    --data "{\"username\":\"${ADMIN_USERNAME}\",\"password\":\"${ADMIN_PASSWORD}\"}"
  assert_json '.token_type == "Bearer"' 'token type should be Bearer'
  assert_json '.user.role == "admin"' 'login should return the admin role'
  # Behind the proxy over plain HTTP the refresh cookie must be set and must not
  # be Secure, otherwise the browser would drop it and sessions could not resume.
  assert_header '^Set-Cookie: ' 'login should set the refresh cookie'
  if grep -i '^Set-Cookie: ' "$LAST_HEADERS_FILE" | grep -i -q 'secure'; then
    echo "Refresh cookie is marked Secure on a plain-HTTP deployment; browsers would drop it." >&2
    return 1
  fi
  TOKEN="$(json_value '.access_token')"
  if [[ -z "$TOKEN" || "$TOKEN" == "null" ]]; then
    echo "Login did not return an access token." >&2
    return 1
  fi
}

check_health_details() {
  api_request 200 GET "/admin/health/details" bearer
  assert_json ".runtime.database_provider == \"${VARIANT}\"" "provider should be ${VARIANT}"
  assert_json '.runtime.jwt_configured == true' 'JWT should be configured'
  assert_json '.runtime.trusted_proxies_configured == true' 'backend should trust the proxy network'
}

check_setup_status_after_admin() {
  api_request 200 GET "/setup/status" none
  assert_json '.initialized == true' 'installation should be initialised once an admin exists'
  assert_json '.registration_bootstrap_open == false' 'bootstrap registration should be closed'
  assert_json '.sample_data_available == true' 'sample data should still be loadable'
}

load_sample_data() {
  api_request 200 POST "/setup/sample-data" bearer
  assert_json '.tables_created | length == 3' 'sample data should create three tables'
  assert_json '(.rows_inserted | add) == 21' 'sample data should insert 21 rows'
  SCHEMA_VERSION_AFTER_SAMPLE="$(json_value '.schema_version')"
  assert_json '.schema_version >= 1' 'sample data should bump the schema version'
}

check_sample_data_is_one_time() {
  api_request 409 POST "/setup/sample-data" bearer
  api_request 200 GET "/setup/status" none
  assert_json '.sample_data_available == false' 'sample data should no longer be offered'
  assert_json ".schema_version == ${SCHEMA_VERSION_AFTER_SAMPLE}" 'status should report the applied schema version'
}

query_sample_data() {
  api_request 200 GET "/data/orders?filter[status]=paid&include=customer&sort=-total" bearer
  assert_json '.data | length == 4' 'four demo orders are paid'
  assert_json '.data[0].customer.name | type == "string"' 'customer include should be embedded'
  assert_json '.data[0].total >= .data[1].total' 'orders should be sorted by total descending'

  api_request 200 GET "/data/customers?search=Acme" bearer
  assert_json '.data | length >= 1' 'search should find Acme'

  api_request 200 GET "/data/orders?filter[placed_at][gte]=2026-09-20T00:00:00Z" bearer
  assert_json '.meta.total == 5' 'five demo orders were placed on or after 2026-09-20'
}

apply_schema_change() {
  api_request 200 GET "/admin/schema" bearer
  local before_version
  before_version="$(json_value '.version')"

  local changed="${WORK_DIR}/schema-with-notes.json"
  jq '{tables: (.tables | map(if .name == "products" then .columns += [{"name":"notes","type":"text"}] else . end)), relations: .relations}' \
    "$LAST_RESPONSE_FILE" > "$changed"

  api_request 200 PUT "/admin/schema" bearer \
    -H "Content-Type: application/json" \
    --data-binary "@${changed}"
  assert_json '.applied == true' 'schema change should be applied'
  assert_json ".version == ${before_version} + 1" 'schema version should increase by one'

  api_request 200 GET "/data/products?per_page=1" bearer
  assert_json '.data[0] | has("notes")' 'products rows should expose the new column'
  assert_json '.meta.total == 5' 'adding a column must keep the five demo products'
}

create_saved_views() {
  api_request 201 POST "/views" bearer \
    -H "Content-Type: application/json" \
    --data '{"name":"Paid orders","table_name":"orders","type":"table","is_shared":true,"config":{"type":"table","search":"","sort":[{"column":"total","dir":"desc"}],"filters":[{"column":"status","op":"eq","value":"paid"}],"hiddenColumnNames":[],"columnOrder":[]}}'
  assert_json '.name == "Paid orders"' 'table view should be created'

  api_request 201 POST "/views" bearer \
    -H "Content-Type: application/json" \
    --data '{"name":"Order board","table_name":"orders","type":"kanban","is_shared":true,"config":{"type":"kanban","search":"","sort":[],"filters":[],"hiddenColumnNames":[],"columnOrder":[],"kanbanColumn":"status","cardFieldNames":["total","placed_at"],"hiddenLaneValues":[]}}'
  assert_json '.type == "kanban"' 'kanban view should be created'

  api_request 200 GET "/views/orders" bearer
  assert_json '[.data[] | .name] | contains(["Paid orders","Order board"])' 'both views should be listed for orders'
}

check_migration_history() {
  api_request 200 GET "/admin/migrations" bearer
  assert_json '.data | length >= 2' 'sample data and the schema change should both be recorded'
}

check_setup_redirect_contract() {
  # The SPA decides the redirect, but the data it relies on must be final here.
  api_request 200 GET "/setup/status" none
  assert_json '.initialized == true and .sample_data_available == false' 'status should describe a set-up installation'
}

# ── run ──────────────────────────────────────────────────────────────────────

if [[ "$SKIP_STARTUP" -eq 0 ]]; then
  run_step "building and starting the full ${VARIANT} stack" start_stack
fi
run_step "waiting for the proxy" wait_for_proxy
run_step "checking that only the proxy publishes ports" check_only_proxy_publishes_ports
run_step "checking the SPA shell through the proxy" check_spa_shell
run_step "checking the fresh setup status" check_fresh_setup_status
run_step "checking public health" check_public_health
run_step "checking that anonymous requests are rejected" check_anonymous_is_rejected
run_step "registering the bootstrap admin" register_bootstrap_admin
run_step "logging in through the proxy" login_admin
run_step "checking health details" check_health_details
run_step "checking setup status after the admin exists" check_setup_status_after_admin
run_step "loading sample data" load_sample_data
run_step "checking that sample data is one-time" check_sample_data_is_one_time
run_step "querying sample data" query_sample_data
run_step "applying a schema change" apply_schema_change
run_step "creating saved views" create_saved_views
run_step "checking migration history" check_migration_history
run_step "checking the final setup status" check_setup_redirect_contract

log_note "packaged MVP 2 smoke succeeded for ${VARIANT} at ${BASE_URL}"
if [[ "$KEEP_RUNNING" -eq 1 ]]; then
  log_note "stack left running (project ${PROJECT_NAME}); admin user '${ADMIN_USERNAME}', password printed below for the manual checklist"
  log_note "admin password: ${ADMIN_PASSWORD}"
fi

cleanup
