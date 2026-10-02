#!/usr/bin/env bash

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CODE_DIR="${REPO_ROOT}/code"
DEFAULT_ENV_FILE="${CODE_DIR}/.env.example"
POSTGRES_EXPOSE_OVERRIDE_FILE="${CODE_DIR}/docker-compose.postgres.expose.yml"

usage() {
  cat <<'EOF'
Usage:
  code/scripts/compose-smoke.sh <up|down|smoke|status> <sqlite|postgres> [options]

Commands:
  up       Build and start the selected packaged deployment, then wait for readiness
  down     Tear down the selected deployment and remove volumes
  smoke    Run `up`, report readiness, then tear down automatically unless --keep-running is set
  status   Print compose status for the selected deployment

Variants:
  sqlite   Backend-only packaged stack (docker-compose.backend-only.yml)
  postgres Backend-only stack plus the Postgres override

Options:
  --env-file <path>         Compose env file to use (default: code/.env.example)
  --project-name <name>     Explicit Docker Compose project name
  --timeout-seconds <n>     Seconds to wait for container/API readiness (default: 180)
  --host-backend-port <n>   Override HOST_BACKEND_PORT without editing an env file
  --host-postgres-port <n>  Opt in to host Postgres publish and set HOST_POSTGRES_PORT
  --log-dir <path>          Directory for captured failure logs
  --keep-running            Do not tear down automatically after `smoke`
  --help                    Show this help

Examples:
  code/scripts/compose-smoke.sh smoke sqlite
  code/scripts/compose-smoke.sh up postgres --timeout-seconds 240
  code/scripts/compose-smoke.sh smoke sqlite --host-backend-port 5001
  code/scripts/compose-smoke.sh down postgres
EOF
}

if [[ $# -eq 1 && "$1" == "--help" ]]; then
  usage
  exit 0
fi

if [[ $# -lt 2 ]]; then
  usage
  exit 1
fi

ACTION="$1"
VARIANT="$2"
shift 2

ENV_FILE="$DEFAULT_ENV_FILE"
TIMEOUT_SECONDS=180
LOG_DIR=""
KEEP_RUNNING=0
PROJECT_NAME=""
HOST_BACKEND_PORT_OVERRIDE=""
HOST_POSTGRES_PORT_OVERRIDE=""
TEMP_ENV_FILE=""
EFFECTIVE_ENV_FILE=""
POSTGRES_PORT_EXPOSED=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env-file)
      ENV_FILE="$2"
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
    --log-dir)
      LOG_DIR="$2"
      shift 2
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

case "$ACTION" in
  up|down|smoke|status)
    ;;
  *)
    echo "Unsupported action: $ACTION" >&2
    usage
    exit 1
    ;;
esac

case "$VARIANT" in
  sqlite|postgres)
    ;;
  *)
    echo "Unsupported variant: $VARIANT" >&2
    usage
    exit 1
    ;;
esac

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Env file does not exist: $ENV_FILE" >&2
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

set_env_var() {
  local file="$1"
  local key="$2"
  local value="$3"
  local updated_file="${file}.tmp"

  if [[ -f "$file" ]]; then
    grep -v "^${key}=" "$file" > "$updated_file" || true
  else
    : > "$updated_file"
  fi

  printf '%s=%s\n' "$key" "$value" >> "$updated_file"
  mv "$updated_file" "$file"
}

prepare_effective_env_file() {
  EFFECTIVE_ENV_FILE="$ENV_FILE"

  if [[ -n "$HOST_BACKEND_PORT_OVERRIDE" || -n "$HOST_POSTGRES_PORT_OVERRIDE" ]]; then
    TEMP_ENV_FILE="$(mktemp "${TMPDIR:-/tmp}/compose-smoke-env.XXXXXX")"
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

get_env_var_from_file() {
  local file="$1"
  local key="$2"

  if [[ ! -f "$file" ]]; then
    return 0
  fi

  grep "^${key}=" "$file" | tail -n 1 | cut -d= -f2- || true
}

resolve_env_var() {
  local key="$1"
  local default_value="${2:-}"
  local value="${!key:-}"

  if [[ -z "$value" ]]; then
    value="$(get_env_var_from_file "$EFFECTIVE_ENV_FILE" "$key")"
  fi

  if [[ -z "$value" ]]; then
    value="$default_value"
  fi

  printf '%s' "$value"
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

  [[ -z "$value" ]] \
    || [[ "$value" == "CHANGE_ME" ]] \
    || [[ "$value" == "repere_dev" ]] \
    || [[ "$value" == "repere_system_dev" ]] \
    || [[ "$value" == "repere_data_dev" ]]
}

ensure_postgres_passwords() {
  if [[ "$VARIANT" != "postgres" ]]; then
    return
  fi

  if [[ -z "$TEMP_ENV_FILE" ]]; then
    TEMP_ENV_FILE="$(mktemp "${TMPDIR:-/tmp}/compose-smoke-env.XXXXXX")"
    cp "$ENV_FILE" "$TEMP_ENV_FILE"
    EFFECTIVE_ENV_FILE="$TEMP_ENV_FILE"
  fi

  local key
  for key in POSTGRES_SUPERUSER_PASSWORD REPERE_SYSTEM_PASSWORD REPERE_DATA_PASSWORD; do
    local value="${!key:-}"

    if is_insecure_postgres_secret "$value"; then
      value="$(get_env_var_from_file "$TEMP_ENV_FILE" "$key")"
    fi

    if is_insecure_postgres_secret "$value"; then
      value="$(generate_secret)"
      printf '[compose-smoke] generated ephemeral %s for disposable Postgres validation\n' "$key"
    fi

    set_env_var "$TEMP_ENV_FILE" "$key" "$value"
  done
}

cleanup_temp_env_file() {
  if [[ -n "$TEMP_ENV_FILE" && -f "$TEMP_ENV_FILE" ]]; then
    rm -f "$TEMP_ENV_FILE"
  fi
}

prepare_effective_env_file
ensure_postgres_passwords

HOST_BACKEND_PORT="$(resolve_env_var "HOST_BACKEND_PORT" "5000")"
BACKEND_PORT="$(resolve_env_var "BACKEND_PORT" "5000")"
HOST_POSTGRES_PORT="$(resolve_env_var "HOST_POSTGRES_PORT" "")"

if [[ -z "$PROJECT_NAME" ]]; then
  PROJECT_NAME="repere-smoke-${VARIANT}"
fi

if [[ -z "$LOG_DIR" ]]; then
  LOG_DIR="/tmp/${PROJECT_NAME}"
fi

mkdir -p "$LOG_DIR"

COMPOSE_FILES=("${CODE_DIR}/docker-compose.backend-only.yml")
if [[ "$VARIANT" == "postgres" ]]; then
  COMPOSE_FILES+=("${CODE_DIR}/docker-compose.postgres.yml")
  if [[ -n "$HOST_POSTGRES_PORT" ]]; then
    COMPOSE_FILES+=("${POSTGRES_EXPOSE_OVERRIDE_FILE}")
    POSTGRES_PORT_EXPOSED=1
  fi
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
  printf '[compose-smoke] %s\n' "$*"
}

capture_debug_artifacts() {
  local artifact_dir="${LOG_DIR}/$(timestamp)"
  mkdir -p "$artifact_dir"

  log_note "capturing compose diagnostics to ${artifact_dir}"

  {
    echo "project_name=${PROJECT_NAME}"
    echo "variant=${VARIANT}"
    echo "env_file=${ENV_FILE}"
    echo "effective_env_file=${EFFECTIVE_ENV_FILE}"
    echo "host_backend_port=${HOST_BACKEND_PORT}"
    echo "backend_port=${BACKEND_PORT}"
    echo "host_postgres_port=${HOST_POSTGRES_PORT}"
    echo "postgres_port_exposed=${POSTGRES_PORT_EXPOSED}"
  } > "${artifact_dir}/context.txt"

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
}

on_failure() {
  local exit_code=$?
  capture_debug_artifacts
  if [[ "$ACTION" == "smoke" && "$KEEP_RUNNING" -eq 0 ]]; then
    compose_cmd down -v --remove-orphans >/dev/null 2>&1 || true
  fi
  cleanup_temp_env_file
  exit "$exit_code"
}

wait_for_container_health() {
  local service="$1"
  local started_at
  started_at="$(date +%s)"

  while true; do
    local container_id
    container_id="$(compose_cmd ps -q "$service" 2>/dev/null || true)"
    if [[ -n "$container_id" ]]; then
      local health_state
      health_state="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id" 2>/dev/null || true)"

      case "$health_state" in
        healthy|running)
          log_note "${service} is ${health_state}"
          return 0
          ;;
        unhealthy|exited|dead)
          echo "${service} entered failure state: ${health_state}" >&2
          return 1
          ;;
      esac
    fi

    if (( "$(date +%s)" - started_at >= TIMEOUT_SECONDS )); then
      echo "Timed out waiting for ${service} container readiness after ${TIMEOUT_SECONDS}s." >&2
      return 1
    fi

    sleep 2
  done
}

wait_for_http_health() {
  local url="http://127.0.0.1:${HOST_BACKEND_PORT}/api/v1/admin/health"
  local started_at
  started_at="$(date +%s)"

  while true; do
    local body_file
    body_file="$(mktemp)"
    local status_code
    status_code="$(curl --silent --show-error --output "$body_file" --write-out '%{http_code}' "$url" || true)"

    if [[ "$status_code" == "200" ]]; then
      if grep -q '"status"[[:space:]]*:[[:space:]]*"healthy"' "$body_file"; then
        rm -f "$body_file"
        log_note "HTTP health endpoint is healthy at ${url}"
        return 0
      fi
    fi

    rm -f "$body_file"

    if (( "$(date +%s)" - started_at >= TIMEOUT_SECONDS )); then
      echo "Timed out waiting for HTTP health endpoint after ${TIMEOUT_SECONDS}s: ${url}" >&2
      return 1
    fi

    sleep 2
  done
}

bring_up_and_wait() {
  trap on_failure ERR

  log_note "validating compose configuration for ${VARIANT}"
  compose_cmd config > /dev/null

  log_note "starting ${VARIANT} packaged deployment"
  compose_cmd up -d --build --remove-orphans

  if [[ "$VARIANT" == "postgres" ]]; then
    wait_for_container_health postgres
  fi

  wait_for_container_health backend
  wait_for_http_health

  trap - ERR
}

tear_down() {
  log_note "tearing down ${VARIANT} packaged deployment"
  compose_cmd down -v --remove-orphans
}

case "$ACTION" in
  up)
    bring_up_and_wait
    compose_cmd ps
    ;;
  down)
    tear_down
    ;;
  status)
    compose_cmd ps
    ;;
  smoke)
    bring_up_and_wait
    compose_cmd ps
    if [[ "$KEEP_RUNNING" -eq 0 ]]; then
      tear_down
    else
      log_note "keeping ${VARIANT} stack running as requested"
    fi
    ;;
esac

cleanup_temp_env_file
