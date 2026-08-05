#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
PREPARE_SCRIPT="${SCRIPT_DIR}/prepare-media-core-readonly.sh"
INDEX_TOOL="${SCRIPT_DIR}/prepare-kinopoisk-readonly-index.py"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf -- "$TEST_ROOT"' EXIT

BIN_DIR="${TEST_ROOT}/bin"
COMPOSE_FILE="${TEST_ROOT}/compose.jellyfin.yaml"
OVERRIDE_FILE="${TEST_ROOT}/compose.jellyfin.kinopoisk-web.yaml"
OUTPUT_ROOT="${TEST_ROOT}/web/kinopoisk"
DOCKER_LOG="${TEST_ROOT}/docker.log"
EXPECTED_TARGET="/jellyfin/jellyfin-web/index.html"

mkdir -p "$BIN_DIR"
printf 'services:\n  jellyfin:\n    read_only: true\n' > "$COMPOSE_FILE"
: > "$DOCKER_LOG"

cat > "${BIN_DIR}/docker" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail

printf '%q ' "$@" >> "$FAKE_DOCKER_LOG"
printf '\n' >> "$FAKE_DOCKER_LOG"

json_string() {
  python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1"
}

case "${1:-}" in
  compose)
    arguments=" $* "
    if [[ "$arguments" == *" ps -q "* ]]; then
      printf '%s\n' 'fake-container-id'
      exit 0
    fi
    if [[ "$arguments" == *" config --format json "* ]]; then
      if [[ "$arguments" == *" ${JELLYFIN_OVERRIDE_FILE} "* ]]; then
        source_json="$(json_string "$JELLYFIN_WEB_OVERRIDE_ROOT/index.html")"
        target_json="$(json_string "$FAKE_WEB_INDEX_PATH")"
        cat <<JSON
{"services":{"jellyfin":{"read_only":true,"volumes":[{"type":"bind","source":${source_json},"target":${target_json},"read_only":true}]}}}
JSON
      else
        printf '%s\n' '{"services":{"jellyfin":{"read_only":true,"volumes":[]}}}'
      fi
      exit 0
    fi
    printf 'Неожиданная команда docker compose: %s\n' "$*" >&2
    exit 91
    ;;
  exec)
    if [[ "${2:-}" == "fake-container-id" \
      && "${3:-}" == "test" \
      && "${4:-}" == "-f" \
      && "${5:-}" == "$FAKE_WEB_INDEX_PATH" ]]; then
      exit 0
    fi
    exit 1
    ;;
  cp)
    destination="${3:-}"
    [[ -n "$destination" ]] || exit 92
    cat > "$destination" <<'HTML'
<!doctype html><html><head></head><body><main>Jellyfin</main></body></html>
HTML
    exit 0
    ;;
  ps)
    printf '%s\n' 'fake-container-id'
    exit 0
    ;;
  *)
    printf 'Неожиданная команда docker: %s\n' "$*" >&2
    exit 93
    ;;
esac
STUB
chmod 0755 "${BIN_DIR}/docker"

export PATH="${BIN_DIR}:${PATH}"
export FAKE_DOCKER_LOG="$DOCKER_LOG"
export FAKE_WEB_INDEX_PATH="$EXPECTED_TARGET"
export JELLYFIN_COMPOSE_FILE="$COMPOSE_FILE"
export JELLYFIN_OVERRIDE_FILE="$OVERRIDE_FILE"
export JELLYFIN_SERVICE_NAME="jellyfin"
export JELLYFIN_CONTAINER_NAME="jellyfin"
export JELLYFIN_WEB_OVERRIDE_ROOT="$OUTPUT_ROOT"

plan_output="$(bash "$PREPARE_SCRIPT" plan)"
grep -Fq 'Режим plan: постоянные файловые и контейнерные изменения не выполнялись.' \
  <<< "$plan_output"
[[ ! -e "$OUTPUT_ROOT" ]]

prepare_output="$(bash "$PREPARE_SCRIPT" prepare)"
grep -Fq 'Подготовка завершена без изменения работающего контейнера.' \
  <<< "$prepare_output"

managed_index="${OUTPUT_ROOT}/index.html"
backup_directory="${OUTPUT_ROOT}/backups"
state_file="${OUTPUT_ROOT}/deployment-state.env"

[[ -f "$managed_index" ]]
[[ -f "$OVERRIDE_FILE" ]]
[[ -f "$state_file" ]]
python3 "$INDEX_TOOL" check --input "$managed_index"
grep -Fq 'read_only: true' "$OVERRIDE_FILE"
grep -Fq 'data-kinopoisk-managed="external"' "$managed_index"
[[ "$(stat -c '%a' "$OVERRIDE_FILE")" == "644" ]]
[[ "$(stat -c '%a' "$state_file")" == "600" ]]

backup_count_before="$(find "$backup_directory" -maxdepth 1 -type f -name '*.bak' | wc -l)"
manifest_before="$(sha256sum "$backup_directory/LATEST.json")"
override_before="$(sha256sum "$OVERRIDE_FILE")"
managed_before="$(sha256sum "$managed_index")"

bash "$PREPARE_SCRIPT" prepare >/dev/null

backup_count_after="$(find "$backup_directory" -maxdepth 1 -type f -name '*.bak' | wc -l)"
manifest_after="$(sha256sum "$backup_directory/LATEST.json")"
override_after="$(sha256sum "$OVERRIDE_FILE")"
managed_after="$(sha256sum "$managed_index")"

[[ "$backup_count_before" == "$backup_count_after" ]]
[[ "$manifest_before" == "$manifest_after" ]]
[[ "$override_before" == "$override_after" ]]
[[ "$managed_before" == "$managed_after" ]]

if grep -Eq '(^|[[:space:]])up([[:space:]]|$)' "$DOCKER_LOG"; then
  printf '%s\n' 'Подготовщик неожиданно вызвал docker compose up.' >&2
  exit 1
fi

printf '%s\n' 'Интеграционная самопроверка media-core read-only успешно пройдена.'
