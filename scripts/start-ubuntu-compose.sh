#!/usr/bin/env bash

set -Eeuo pipefail

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly PROJECT_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
readonly COMPOSE_FILE="${PROJECT_DIR}/docker-compose.local.yml"
readonly ENV_FILE="${PROJECT_DIR}/.env.ubuntu"

default_user_id() {
    if [[ -n "${SUDO_UID:-}" && "${SUDO_UID}" -ne 0 ]]; then
        printf '%s' "${SUDO_UID}"
    elif [[ "$(id -u)" -ne 0 ]]; then
        id -u
    else
        printf '10001'
    fi
}

default_group_id() {
    if [[ -n "${SUDO_GID:-}" && "${SUDO_GID}" -ne 0 ]]; then
        printf '%s' "${SUDO_GID}"
    elif [[ "$(id -g)" -ne 0 ]]; then
        id -g
    else
        printf '10001'
    fi
}

LAN_ADDRESS="${LAN_BIND_ADDRESS:-}"
JELLYFIN_PORT="${JELLYFIN_HTTP_PORT:-8096}"
DISPATCHARR_PORT="${DISPATCHARR_HTTP_PORT:-9191}"
JELLYFIN_USER_ID="${JELLYFIN_UID:-$(default_user_id)}"
JELLYFIN_GROUP_ID="${JELLYFIN_GID:-$(default_group_id)}"
DEPLOY_TIMEZONE="${TIMEZONE:-Etc/UTC}"
WEB_REPOSITORY="${JELLYFIN_WEB_REPOSITORY:-https://github.com/michaelmoskie/jellyfin-web.git}"
WEB_REF="${JELLYFIN_WEB_REF:-master}"
DISPATCHARR_IMAGE_TAG="${DISPATCHARR_TAG:-latest}"

usage() {
    cat <<'EOF'
Usage: scripts/start-ubuntu-compose.sh [--lan-ip 192.168.1.X] [--no-build]

Builds the Jellyfin server and michaelmoskie/jellyfin-web, pulls Dispatcharr,
starts both services with Docker Compose, and verifies their HTTP endpoints.

Environment overrides:
  LAN_BIND_ADDRESS          Ubuntu host address in 192.168.1.0/24
  JELLYFIN_HTTP_PORT        Jellyfin HTTP port (default: 8096)
  DISPATCHARR_HTTP_PORT     Dispatcharr HTTP port (default: 9191)
  JELLYFIN_UID/GID          Numeric owner for Jellyfin data
  JELLYFIN_WEB_REPOSITORY   Jellyfin web git repository
  JELLYFIN_WEB_REF          Jellyfin web branch or tag (default: master)
  DISPATCHARR_TAG           Dispatcharr image tag (default: latest)
  TIMEZONE                  Container timezone (default: Etc/UTC)
EOF
}

BUILD_IMAGES=true
while (($# > 0)); do
    case "$1" in
        --lan-ip)
            if (($# < 2)); then
                echo "error: --lan-ip requires an address" >&2
                exit 2
            fi

            LAN_ADDRESS="$2"
            shift 2
            ;;
        --no-build)
            BUILD_IMAGES=false
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "error: unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "error: required command not found: $1" >&2
        exit 1
    fi
}

detect_lan_address() {
    ip -4 -o address show scope global \
        | awk '$4 ~ /^192\.168\.1\./ { sub(/\/.*/, "", $4); print $4; exit }'
}

validate_lan_address() {
    local address="$1"
    local final_octet

    if [[ ! "${address}" =~ ^192\.168\.1\.([0-9]{1,3})$ ]]; then
        return 1
    fi

    final_octet="${BASH_REMATCH[1]}"
    ((10#${final_octet} >= 1 && 10#${final_octet} <= 254))
}

validate_port() {
    local port="$1"

    [[ "${port}" =~ ^[0-9]+$ ]] && ((10#${port} >= 1 && 10#${port} <= 65535))
}

validate_numeric_id() {
    local numeric_id="$1"

    [[ "${numeric_id}" =~ ^[0-9]+$ ]] && ((10#${numeric_id} >= 1))
}

wait_for_url() {
    local service_name="$1"
    local url="$2"
    local attempts="${3:-60}"

    echo "Waiting for ${service_name} at ${url}"
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        if curl --fail --silent --show-error \
            --connect-timeout 2 \
            --max-time 5 \
            "${url}" >/dev/null 2>&1; then
            echo "${service_name} is ready."
            return 0
        fi

        sleep 5
    done

    echo "error: ${service_name} did not become ready" >&2
    return 1
}

require_command docker
require_command curl
require_command ip

if ! docker compose version >/dev/null 2>&1; then
    echo "error: Docker Compose v2 is required (the 'docker compose' command)." >&2
    exit 1
fi

if ! docker info >/dev/null 2>&1; then
    echo "error: Docker is not running or this user cannot access its socket." >&2
    echo "Run this script as a Docker-enabled user; avoid sudo unless required." >&2
    exit 1
fi

if [[ -z "${LAN_ADDRESS}" ]]; then
    LAN_ADDRESS="$(detect_lan_address)"
fi

if ! validate_lan_address "${LAN_ADDRESS}"; then
    echo "error: no usable 192.168.1.x host address was found." >&2
    echo "Specify one with --lan-ip 192.168.1.X or LAN_BIND_ADDRESS." >&2
    exit 1
fi

if ! validate_port "${JELLYFIN_PORT}" || ! validate_port "${DISPATCHARR_PORT}"; then
    echo "error: service ports must be integers between 1 and 65535." >&2
    exit 1
fi

if ! validate_numeric_id "${JELLYFIN_USER_ID}" || ! validate_numeric_id "${JELLYFIN_GROUP_ID}"; then
    echo "error: JELLYFIN_UID and JELLYFIN_GID must be positive numeric IDs." >&2
    exit 1
fi

mkdir -p \
    "${PROJECT_DIR}/run/config" \
    "${PROJECT_DIR}/run/cache" \
    "${PROJECT_DIR}/run/media" \
    "${PROJECT_DIR}/run/dispatcharr"

if [[ "$(id -u)" -eq 0 ]]; then
    chown -R \
        "${JELLYFIN_USER_ID}:${JELLYFIN_GROUP_ID}" \
        "${PROJECT_DIR}/run/config" \
        "${PROJECT_DIR}/run/cache" \
        "${PROJECT_DIR}/run/media"
fi

umask 077
{
    printf 'LAN_BIND_ADDRESS=%s\n' "${LAN_ADDRESS}"
    printf 'JELLYFIN_HTTP_PORT=%s\n' "${JELLYFIN_PORT}"
    printf 'DISPATCHARR_HTTP_PORT=%s\n' "${DISPATCHARR_PORT}"
    printf 'JELLYFIN_UID=%s\n' "${JELLYFIN_USER_ID}"
    printf 'JELLYFIN_GID=%s\n' "${JELLYFIN_GROUP_ID}"
    printf 'TIMEZONE=%s\n' "${DEPLOY_TIMEZONE}"
    printf 'JELLYFIN_WEB_REPOSITORY=%s\n' "${WEB_REPOSITORY}"
    printf 'JELLYFIN_WEB_REF=%s\n' "${WEB_REF}"
    printf 'DISPATCHARR_TAG=%s\n' "${DISPATCHARR_IMAGE_TAG}"
} > "${ENV_FILE}"

compose() {
    docker compose \
        --env-file "${ENV_FILE}" \
        --file "${COMPOSE_FILE}" \
        "$@"
}

cd "${PROJECT_DIR}"
compose config --quiet
compose pull dispatcharr

if [[ "${BUILD_IMAGES}" == true ]]; then
    compose build --pull jellyfin-local
fi

compose up --detach --remove-orphans

FAILED=false
wait_for_url "Dispatcharr" "http://${LAN_ADDRESS}:${DISPATCHARR_PORT}/api/core/version/" || FAILED=true
wait_for_url "Jellyfin" "http://${LAN_ADDRESS}:${JELLYFIN_PORT}/health" || FAILED=true

if [[ "${FAILED}" == true ]]; then
    compose ps
    compose logs --tail 150
    exit 1
fi

compose ps

cat <<EOF

Deployment is ready:
  Jellyfin:   http://${LAN_ADDRESS}:${JELLYFIN_PORT}
  Dispatcharr: http://${LAN_ADDRESS}:${DISPATCHARR_PORT}

Dispatcharr is reachable from Jellyfin on the Compose network at:
  http://dispatcharr:9191/

Complete Dispatcharr's initial setup and create an API key. Then open
Jellyfin's Dashboard → Live TV, add a tuner, and select Dispatcharr. Use:
  URL: http://dispatcharr:9191/
  API key: the key created in Dispatcharr
  Channel profile ID: optional

Persistent data is stored under:
  ${PROJECT_DIR}/run/
EOF
