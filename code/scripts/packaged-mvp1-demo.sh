#!/usr/bin/env bash

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CODE_DIR="${REPO_ROOT}/code"
DEFAULT_ENV_FILE="${CODE_DIR}/.env.example"
DEFAULT_SCHEMA_FILE="${CODE_DIR}/scripts/fixtures/mvp1-demo-schema.json"
SMOKE_HARNESS="${CODE_DIR}/scripts/compose-smoke.sh"

usage() {
  cat <<'EOF'
Usage:
  code/scripts/packaged-mvp1-demo.sh <sqlite|postgres> [options]

Options:
  --env-file <path>         Compose env file to use (default: code/.env.example)
  --schema-file <path>      Model JSON fixture to apply
  --project-name <name>     Explicit Docker Compose project name
  --timeout-seconds <n>     Seconds to allow startup/readiness
  --host-backend-port <n>   Override HOST_BACKEND_PORT for the packaged stack
  --host-postgres-port <n>  Override HOST_POSTGRES_PORT for the packaged stack
  --api-root <url>          Override the API root instead of deriving it from the host port
  --log-dir <path>          Directory for captured failure logs
  --skip-startup            Assume the selected stack is already running and healthy
  --keep-running            Leave the stack running after the demo completes
  --help                    Show this help

Examples:
  code/scripts/packaged-mvp1-demo.sh sqlite
  code/scripts/packaged-mvp1-demo.sh sqlite --host-backend-port 5001
  code/scripts/packaged-mvp1-demo.sh postgres --host-backend-port 5001
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
  sqlite|postgres)
    ;;
  *)
    echo "Unsupported variant: $VARIANT" >&2
    usage
    exit 1
    ;;
esac

ENV_FILE="$DEFAULT_ENV_FILE"
SCHEMA_FILE="$DEFAULT_SCHEMA_FILE"
PROJECT_NAME="repere-demo-${VARIANT}"
TIMEOUT_SECONDS=180
HOST_BACKEND_PORT_OVERRIDE=""
HOST_POSTGRES_PORT_OVERRIDE=""
API_ROOT_OVERRIDE=""
LOG_DIR=""
SKIP_STARTUP=0
KEEP_RUNNING=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env-file)
      ENV_FILE="$2"
      shift 2
      ;;
    --schema-file)
      SCHEMA_FILE="$2"
      shift 2
      ;;
    --project-name)
      PROJECT_NAME="$2"
      shift 2
      ;;
    --timeout-seconds)
      TIMEOUT_SECONDS="$2"
      shift 2
      ;;
    --host-backend-port)
      HOST_BACKEND_PORT_OVERRIDE="$2"
      shift 2
      ;;
    --host-postgres-port)
      HOST_POSTGRES_PORT_OVERRIDE="$2"
      shift 2
      ;;
    --api-root)
      API_ROOT_OVERRIDE="$2"
      shift 2
      ;;
    --log-dir)
      LOG_DIR="$2"
      shift 2
      ;;
    --skip-startup)
      SKIP_STARTUP=1
      shift
      ;;
    --keep-running)
      KEEP_RUNNING=1
      shift
      ;;
    --help)
      usage
      exit 0
      ;;
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

if [[ ! -f "$SCHEMA_FILE" ]]; then
  echo "Schema file does not exist: $SCHEMA_FILE" >&2
  exit 1
fi

if ! [[ "$TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] || [[ "$TIMEOUT_SECONDS" -le 0 ]]; then
  echo "--timeout-seconds must be a positive integer." >&2
  exit 1
fi

if [[ -n "$HOST_BACKEND_PORT_OVERRIDE" ]] && { ! [[ "$HOST_BACKEND_PORT_OVERRIDE" =~ ^[0-9]+$ ]] || [[ "$HOST_BACKEND_PORT_OVERRIDE" -le 0 ]]; }; then
  echo "--host-backend-port must be a positive integer." >&2
  exit 1
fi

if [[ -n "$HOST_POSTGRES_PORT_OVERRIDE" ]] && { ! [[ "$HOST_POSTGRES_PORT_OVERRIDE" =~ ^[0-9]+$ ]] || [[ "$HOST_POSTGRES_PORT_OVERRIDE" -le 0 ]]; }; then
  echo "--host-postgres-port must be a positive integer." >&2
  exit 1
fi

require_command() {
  local command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command not found: $command_name" >&2
    exit 1
  fi
}

require_command docker
require_command curl
require_command jq
require_command mktemp
require_command grep

WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/packaged-mvp1-demo.XXXXXX")"
TEMP_ENV_FILE=""
EFFECTIVE_ENV_FILE="$ENV_FILE"
STACK_AVAILABLE=0
STACK_OWNED=0
CURRENT_STEP="initialization"
LAST_RESPONSE_FILE=""
TOKEN=""
CUSTOMER_ID=""
PAID_ORDER_ID=""
PENDING_ORDER_ID=""
PRODUCT_ID=""
RESPONSE_COUNTER=0

set_env_var() {
  local file="$1"
  local key="$2"
  local value="$3"
  local updated_file="${file}.tmp"

  grep -v "^${key}=" "$file" > "$updated_file" || true
  printf '%s=%s\n' "$key" "$value" >> "$updated_file"
  mv "$updated_file" "$file"
}

prepare_effective_env_file() {
  EFFECTIVE_ENV_FILE="$ENV_FILE"

  if [[ -n "$HOST_BACKEND_PORT_OVERRIDE" || -n "$HOST_POSTGRES_PORT_OVERRIDE" ]]; then
    TEMP_ENV_FILE="$(mktemp "${TMPDIR:-/tmp}/packaged-mvp1-demo-env.XXXXXX")"
    cp "$ENV_FILE" "$TEMP_ENV_FILE"

    if [[ -n "$HOST_BACKEND_PORT_OVERRIDE" ]]; then
      set_env_var "$TEMP_ENV_FILE" "HOST_BACKEND_PORT" "$HOST_BACKEND_PORT_OVERRIDE"
    fi

    if [[ -n "$HOST_POSTGRES_PORT_OVERRIDE" ]]; then
      set_env_var "$TEMP_ENV_FILE" "HOST_POSTGRES_PORT" "$HOST_POSTGRES_PORT_OVERRIDE"
    fi

    EFFECTIVE_ENV_FILE="$TEMP_ENV_FILE"
  fi
}

prepare_effective_env_file

set -a
# shellcheck disable=SC1090
source "$EFFECTIVE_ENV_FILE"
set +a

HOST_BACKEND_PORT="${HOST_BACKEND_PORT:-5000}"
HOST_POSTGRES_PORT="${HOST_POSTGRES_PORT:-5432}"

if [[ -n "$API_ROOT_OVERRIDE" ]]; then
  API_ROOT="$API_ROOT_OVERRIDE"
else
  API_ROOT="http://127.0.0.1:${HOST_BACKEND_PORT}/api/v1"
fi

if [[ -z "$LOG_DIR" ]]; then
  LOG_DIR="/tmp/${PROJECT_NAME}-demo"
fi

mkdir -p "$LOG_DIR"

COMPOSE_FILES=("${CODE_DIR}/docker-compose.yml")
if [[ "$VARIANT" == "postgres" ]]; then
  COMPOSE_FILES+=("${CODE_DIR}/docker-compose.postgres.yml")
fi

compose_cmd() {
  local args=(
    docker compose
    --project-name "$PROJECT_NAME"
    --env-file "$EFFECTIVE_ENV_FILE"
  )

  local compose_file
  for compose_file in "${COMPOSE_FILES[@]}"; do
    args+=(-f "$compose_file")
  done

  args+=("$@")
  "${args[@]}"
}

timestamp() {
  date -u +"%Y%m%dT%H%M%SZ"
}

log_note() {
  printf '[packaged-mvp1-demo] %s\n' "$*"
}

response_file() {
  RESPONSE_COUNTER=$((RESPONSE_COUNTER + 1))
  printf '%s/response-%04d.json\n' "$WORK_DIR" "$RESPONSE_COUNTER"
}

api_request() {
  local expected_status="$1"
  local method="$2"
  local path="$3"
  local auth_mode="$4"
  shift 4

  LAST_RESPONSE_FILE="$(response_file)"

  local curl_args=(
    --silent
    --show-error
    --globoff
    --output "$LAST_RESPONSE_FILE"
    --write-out '%{http_code}'
    -X "$method"
    -H 'Accept: application/json'
  )

  if [[ "$auth_mode" == "bearer" ]]; then
    curl_args+=(-H "Authorization: Bearer $TOKEN")
  fi

  curl_args+=("$@" "${API_ROOT}${path}")

  local status_code
  status_code="$(curl "${curl_args[@]}")"

  if [[ "$status_code" != "$expected_status" ]]; then
    echo "Expected HTTP ${expected_status} for ${method} ${path} during step '${CURRENT_STEP}', got ${status_code}." >&2
    cat "$LAST_RESPONSE_FILE" >&2 || true
    return 1
  fi
}

assert_json() {
  local filter="$1"
  local description="$2"

  if ! jq -e "$filter" "$LAST_RESPONSE_FILE" >/dev/null; then
    echo "Assertion failed during step '${CURRENT_STEP}': ${description}" >&2
    cat "$LAST_RESPONSE_FILE" >&2 || true
    return 1
  fi
}

json_value() {
  local filter="$1"
  jq -r "$filter" "$LAST_RESPONSE_FILE"
}

capture_debug_artifacts() {
  local artifact_dir="${LOG_DIR}/$(timestamp)"
  mkdir -p "$artifact_dir"

  log_note "capturing packaged demo diagnostics to ${artifact_dir}"

  {
    echo "project_name=${PROJECT_NAME}"
    echo "variant=${VARIANT}"
    echo "current_step=${CURRENT_STEP}"
    echo "env_file=${ENV_FILE}"
    echo "effective_env_file=${EFFECTIVE_ENV_FILE}"
    echo "schema_file=${SCHEMA_FILE}"
    echo "api_root=${API_ROOT}"
    echo "host_backend_port=${HOST_BACKEND_PORT}"
    echo "host_postgres_port=${HOST_POSTGRES_PORT}"
  } > "${artifact_dir}/context.txt"

  if [[ -n "$LAST_RESPONSE_FILE" && -f "$LAST_RESPONSE_FILE" ]]; then
    cp "$LAST_RESPONSE_FILE" "${artifact_dir}/last-response.json"
  fi

  local health_file="${artifact_dir}/health.json"
  curl --silent --show-error "${API_ROOT}/admin/health" > "$health_file" 2>"${artifact_dir}/health.stderr" || true

  if [[ "$STACK_AVAILABLE" -eq 1 ]]; then
    compose_cmd ps > "${artifact_dir}/compose-ps.txt" 2>&1 || true
    compose_cmd logs --no-color > "${artifact_dir}/compose-logs.txt" 2>&1 || true

    local service
    for service in backend postgres; do
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
    "${SMOKE_HARNESS}" down "$VARIANT" \
      --env-file "$EFFECTIVE_ENV_FILE" \
      --project-name "$PROJECT_NAME" >/dev/null 2>&1 || true
  fi

  if [[ -n "$TEMP_ENV_FILE" && -f "$TEMP_ENV_FILE" ]]; then
    rm -f "$TEMP_ENV_FILE"
  fi

  rm -rf "$WORK_DIR"
}

on_error() {
  local exit_code=$?
  echo "Packaged MVP 1 demo failed during step '${CURRENT_STEP}'." >&2
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

start_stack() {
  "${SMOKE_HARNESS}" up "$VARIANT" \
    --env-file "$EFFECTIVE_ENV_FILE" \
    --project-name "$PROJECT_NAME" \
    --timeout-seconds "$TIMEOUT_SECONDS"
  STACK_AVAILABLE=1
  STACK_OWNED=1
}

check_public_health() {
  local expected_provider
  expected_provider="$VARIANT"

  api_request 200 GET "/admin/health" none
  assert_json '.status == "healthy"' 'health status should be healthy'
  assert_json '.database.system_pool == "connected"' 'system pool should be connected'
  assert_json '.database.data_pool == "connected"' 'data pool should be connected'
  assert_json ".runtime.database_provider == \"${expected_provider}\"" "database provider should be ${expected_provider}"
  assert_json '.runtime.jwt_configured == true' 'JWT should be configured by packaged runtime bootstrap'
}

register_bootstrap_admin() {
  api_request 201 POST "/auth/register" none \
    -H "Content-Type: application/json" \
    --data '{"username":"admin","password":"admin12345"}'

  assert_json '.username == "admin"' 'registered username should be admin'
  assert_json '.role == "admin"' 'bootstrap user should be admin'
  assert_json '.bootstrap_admin == true' 'first user should be marked as bootstrap admin'
}

login_admin() {
  api_request 200 POST "/auth/login" none \
    -H "Content-Type: application/json" \
    --data '{"username":"admin","password":"admin12345"}'

  assert_json '.token_type == "Bearer"' 'token type should be Bearer'
  assert_json '.user.username == "admin"' 'login should return the admin user'
  assert_json '.user.role == "admin"' 'login should return the admin role'

  TOKEN="$(json_value '.access_token')"
  if [[ -z "$TOKEN" || "$TOKEN" == "null" ]]; then
    echo "Login did not return an access token." >&2
    return 1
  fi
}

apply_demo_schema() {
  api_request 200 PUT "/admin/schema" bearer \
    -H "Content-Type: application/json" \
    --data-binary "@${SCHEMA_FILE}"

  assert_json '.applied == true' 'schema apply should report applied=true'
  assert_json '.version >= 1' 'schema version should be incremented'
  assert_json '.changes | length >= 1' 'schema apply should report at least one change'
}

verify_schema_visibility() {
  api_request 200 GET "/admin/schema" bearer
  assert_json '.tables | length == 3' 'demo schema should expose three tables'
  assert_json '.relations | length == 2' 'demo schema should expose two relations'
  assert_json '.tables | any(.name == "customers")' 'customers table should be present'
  assert_json '.tables | any(.name == "orders")' 'orders table should be present'
  assert_json '.tables | any(.name == "products")' 'products table should be present'
}

create_customer() {
  api_request 201 POST "/data/customers" bearer \
    -H "Content-Type: application/json" \
    --data '{"name":"Acme Corp","email":"acme@example.com","tier":"gold"}'

  assert_json '.data.name == "Acme Corp"' 'customer create should echo the customer name'
  CUSTOMER_ID="$(json_value '.data.id')"
  if [[ -z "$CUSTOMER_ID" || "$CUSTOMER_ID" == "null" ]]; then
    echo "Customer creation did not return an ID." >&2
    return 1
  fi
}

read_customer() {
  api_request 200 GET "/data/customers/${CUSTOMER_ID}" bearer
  assert_json '.data.email == "acme@example.com"' 'read customer should return the created email'
}

patch_customer() {
  api_request 200 PATCH "/data/customers/${CUSTOMER_ID}" bearer \
    -H "Content-Type: application/json" \
    --data '{"notes":"VIP customer"}'

  assert_json '.data.notes == "VIP customer"' 'patch should update customer notes'
}

create_orders() {
  api_request 201 POST "/data/orders" bearer \
    -H "Content-Type: application/json" \
    --data "{\"customer_id\":${CUSTOMER_ID},\"total\":250.00,\"status\":\"paid\"}"
  PAID_ORDER_ID="$(json_value '.data.id')"

  api_request 201 POST "/data/orders" bearer \
    -H "Content-Type: application/json" \
    --data "{\"customer_id\":${CUSTOMER_ID},\"total\":120.00,\"status\":\"pending\"}"
  PENDING_ORDER_ID="$(json_value '.data.id')"

  if [[ -z "$PAID_ORDER_ID" || "$PAID_ORDER_ID" == "null" || -z "$PENDING_ORDER_ID" || "$PENDING_ORDER_ID" == "null" ]]; then
    echo "Order creation did not return both order IDs." >&2
    return 1
  fi
}

create_and_delete_product() {
  api_request 201 POST "/data/products" bearer \
    -H "Content-Type: application/json" \
    --data '{"sku":"SKU-001","name":"Widget","price":99.99}'

  PRODUCT_ID="$(json_value '.data.id')"
  if [[ -z "$PRODUCT_ID" || "$PRODUCT_ID" == "null" ]]; then
    echo "Product creation did not return an ID." >&2
    return 1
  fi

  api_request 204 DELETE "/data/products/${PRODUCT_ID}" bearer
  api_request 404 GET "/data/products/${PRODUCT_ID}" bearer
  assert_json '.error.code == "NOT_FOUND"' 'deleted product should return NOT_FOUND'
}

query_orders_with_include() {
  api_request 200 GET "/data/orders?filter[status]=paid&include=customer&sort=-total" bearer
  assert_json '.data | length == 1' 'paid order filter should return exactly one row'
  assert_json '.data[0].customer.name == "Acme Corp"' 'included customer should be embedded on the paid order'
}

query_relation_subresource() {
  api_request 200 GET "/data/customers/${CUSTOMER_ID}/customer_orders?sort=-total&page=1&per_page=1" bearer
  assert_json '.meta.page == 1' 'sub-resource page should be 1'
  assert_json '.meta.per_page == 1' 'sub-resource per_page should be 1'
  assert_json '.meta.total == 2' 'sub-resource total should reflect both related orders'
  assert_json '.meta.total_pages == 2' 'sub-resource total_pages should be 2'
  assert_json '.data | length == 1' 'sub-resource page should include one row'
}

verify_migration_visibility() {
  api_request 200 GET "/admin/migrations" bearer
  assert_json '.data | length >= 1' 'migration history should have at least one entry'
  assert_json '.data[0] | has("versionFrom")' 'migration history entries should include versionFrom'
  assert_json '.data[0] | has("versionTo")' 'migration history entries should include versionTo'
}

if [[ "$SKIP_STARTUP" -eq 0 ]]; then
  run_step "starting packaged ${VARIANT} stack" start_stack
else
  STACK_AVAILABLE=1
fi

run_step "checking public health contract" check_public_health
run_step "registering bootstrap admin" register_bootstrap_admin
run_step "logging in as admin" login_admin
run_step "applying demo schema" apply_demo_schema
run_step "verifying schema visibility" verify_schema_visibility
run_step "creating customer" create_customer
run_step "reading customer" read_customer
run_step "patching customer" patch_customer
run_step "creating orders" create_orders
run_step "creating and deleting product" create_and_delete_product
run_step "querying orders with relation include" query_orders_with_include
run_step "querying relation sub-resource" query_relation_subresource
run_step "verifying migration visibility" verify_migration_visibility
run_step "rechecking public health contract" check_public_health

log_note "packaged MVP 1 demo succeeded for ${VARIANT}"

cleanup
