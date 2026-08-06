#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
PREPARE_SCRIPT="${SCRIPT_DIR}/prepare-media-core-readonly.sh"
INDEX_TOOL="${SCRIPT_DIR}/prepare-kinopoisk-readonly-index.py"
CHECKSUM_FILE="${SCRIPT_DIR}/SHA256SUMS"

TARGET_VERSION="10.11.0.3"
EXPECTED_JELLYFIN_VERSION="10.11.11"
COMPOSE_FILE="${JELLYFIN_COMPOSE_FILE:-/srv/media-core/compose/compose.jellyfin.yaml}"
COMPOSE_PROJECT="${JELLYFIN_COMPOSE_PROJECT:-media-core-jellyfin}"
OVERRIDE_FILE="${JELLYFIN_OVERRIDE_FILE:-/srv/media-core/compose/compose.jellyfin.kinopoisk-web.yaml}"
SERVICE_NAME="${JELLYFIN_SERVICE_NAME:-jellyfin}"
CONTAINER_NAME="${JELLYFIN_CONTAINER_NAME:-jellyfin}"
PROXY_CONTAINER="${JELLYFIN_PROXY_CONTAINER:-jellyfin-egress-proxy}"
BASE_URL="${JELLYFIN_BASE_URL:-http://192.168.0.53:8096}"
BACKUP_ROOT="${JELLYFIN_KINOPOISK_BACKUP_ROOT:-/srv/media-core/backups/jellyfin-kinopoisk}"
WEB_OVERRIDE_ROOT="${JELLYFIN_WEB_OVERRIDE_ROOT:-/srv/media-core/appdata/jellyfin-web/kinopoisk}"
MANAGED_INDEX="${WEB_OVERRIDE_ROOT}/index.html"

CONFIG_SOURCE=""
PLUGIN_ROOT=""
CURRENT_PLUGIN_DIR=""
TARGET_PLUGIN_DIR=""
PLUGIN_CONFIG=""
WEB_INDEX_PATH=""
TEMPORARY_DIRECTORY=""
TRANSACTION_DIR=""
TRANSACTION_READY=false
APPLY_STARTED=false
ROLLBACK_RUNNING=false
CURRENT_STAGE="Инициализация"
REPORT_FILE=""

usage() {
  cat <<'EOF'
Использование:
  install-media-core-autonomous.sh plan
  install-media-core-autonomous.sh apply --confirm
  install-media-core-autonomous.sh verify [ТРАНЗАКЦИЯ]
  install-media-core-autonomous.sh rollback [ТРАНЗАКЦИЯ] --confirm

plan      Выполняет только read-only диагностику.
apply     Создаёт резервную копию, устанавливает DLL и внешний read-only index.html,
          пересоздаёт только Jellyfin, проверяет runtime и автоматически откатывает
          изменения при любой критической ошибке.
verify    Выполняет только runtime-проверку текущей установки.
rollback  Восстанавливает точное состояние из указанной или последней транзакции.

Переменные окружения:
  JELLYFIN_COMPOSE_FILE
  JELLYFIN_COMPOSE_PROJECT
  JELLYFIN_OVERRIDE_FILE
  JELLYFIN_SERVICE_NAME
  JELLYFIN_CONTAINER_NAME
  JELLYFIN_PROXY_CONTAINER
  JELLYFIN_BASE_URL
  JELLYFIN_KINOPOISK_BACKUP_ROOT
  JELLYFIN_WEB_OVERRIDE_ROOT
EOF
}

log() {
  printf '%s\n' "$*"
}

fail() {
  printf 'ОШИБКА: %s\n' "$*" >&2
  return 1
}

cleanup() {
  if [[ -n "$TEMPORARY_DIRECTORY" && -d "$TEMPORARY_DIRECTORY" ]]; then
    rm -rf -- "$TEMPORARY_DIRECTORY"
  fi
  TEMPORARY_DIRECTORY=""
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "Не найдена команда: $1"
}

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

shell_quote() {
  printf '%q' "$1"
}

compose_base() {
  docker compose -p "$COMPOSE_PROJECT" -f "$COMPOSE_FILE" "$@"
}

compose_with_override() {
  docker compose \
    -p "$COMPOSE_PROJECT" \
    -f "$COMPOSE_FILE" \
    -f "$OVERRIDE_FILE" \
    "$@"
}

verify_package() {
  CURRENT_STAGE="Проверка установочного комплекта"
  [[ -f "$CHECKSUM_FILE" ]] || fail "Файл SHA256SUMS не найден: $CHECKSUM_FILE"
  [[ -f "$SCRIPT_DIR/Jellyfin.Plugin.Kinopoisk.dll" ]] \
    || fail "Основная DLL отсутствует в комплекте."
  [[ -f "$SCRIPT_DIR/KinopoiskUnofficialInfo.ApiClient.dll" ]] \
    || fail "DLL API-клиента отсутствует в комплекте."
  [[ -f "$PREPARE_SCRIPT" ]] || fail "Подготовщик media-core отсутствует."
  [[ -f "$INDEX_TOOL" ]] || fail "Подготовщик index.html отсутствует."

  (
    cd "$SCRIPT_DIR"
    sha256sum -c SHA256SUMS
  )

  strings "$SCRIPT_DIR/Jellyfin.Plugin.Kinopoisk.dll" \
    | grep -Fq "$TARGET_VERSION" \
    || fail "В основной DLL не подтверждена версия $TARGET_VERSION."
}

validate_base_compose() {
  CURRENT_STAGE="Проверка базового Compose"
  local rendered
  rendered="$(mktemp)"
  compose_base config --format json > "$rendered"
  python3 - "$rendered" "$SERVICE_NAME" <<'PY'
import json
import sys
from pathlib import Path

path, service_name = sys.argv[1:]
config = json.loads(Path(path).read_text(encoding="utf-8"))
service = config.get("services", {}).get(service_name)
if not isinstance(service, dict):
    raise SystemExit(f"Сервис {service_name!r} отсутствует в базовом Compose.")
if service.get("read_only") is not True:
    raise SystemExit("Базовый Jellyfin не содержит read_only: true.")
print("Базовый Compose: read_only: true подтверждён.")
PY
  rm -f -- "$rendered"
}

resolve_container_id() {
  local container_id
  container_id="$(compose_base ps -q "$SERVICE_NAME" 2>/dev/null || true)"
  if [[ -z "$container_id" ]]; then
    container_id="$(docker ps --filter "name=^/${CONTAINER_NAME}$" --format '{{.ID}}' | head -n 1)"
  fi
  [[ -n "$container_id" ]] || fail "Работающий контейнер Jellyfin не найден."
  printf '%s\n' "$container_id"
}

resolve_config_source() {
  local container_id="$1"
  local inspect_file
  inspect_file="$(mktemp)"
  docker inspect "$container_id" > "$inspect_file"
  CONFIG_SOURCE="$(python3 - "$inspect_file" <<'PY'
import json
import sys
from pathlib import Path

payload = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
if not payload:
    raise SystemExit("docker inspect не вернул объект контейнера.")
mounts = payload[0].get("Mounts", [])
matching = [mount for mount in mounts if mount.get("Destination") == "/config"]
if len(matching) != 1:
    raise SystemExit(f"Ожидалось одно монтирование /config, обнаружено {len(matching)}.")
mount = matching[0]
if mount.get("RW") is not True:
    raise SystemExit("Монтирование /config не является доступным для записи.")
source = mount.get("Source")
if not source:
    raise SystemExit("Источник /config не определён.")
print(source)
PY
)"
  rm -f -- "$inspect_file"

  [[ -d "$CONFIG_SOURCE" ]] || fail "Каталог /config на хосте не найден: $CONFIG_SOURCE"
  PLUGIN_ROOT="$CONFIG_SOURCE/plugins"
  PLUGIN_CONFIG="$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"
  TARGET_PLUGIN_DIR="$PLUGIN_ROOT/КиноПоиск_${TARGET_VERSION}"
}

resolve_web_index_path() {
  local container_id="$1"
  local candidate
  local candidates=(
    "/jellyfin/jellyfin-web/index.html"
    "/usr/share/jellyfin/web/index.html"
    "/usr/lib/jellyfin/bin/jellyfin-web/index.html"
  )
  for candidate in "${candidates[@]}"; do
    if docker exec "$container_id" test -f "$candidate" >/dev/null 2>&1; then
      WEB_INDEX_PATH="$candidate"
      return 0
    fi
  done
  fail "В контейнере не найден штатный index.html Jellyfin Web."
}

resolve_current_plugin_dir() {
  local loaded_relative=""
  local candidate
  loaded_relative="$(
    docker logs --tail 5000 "$CONTAINER_NAME" 2>&1 \
      | sed -n 's#.*from /config/plugins/\(.*\)/Jellyfin\.Plugin\.Kinopoisk\.dll.*#\1#p' \
      | tail -n 1
  )"

  if [[ -n "$loaded_relative" ]]; then
    candidate="$PLUGIN_ROOT/$loaded_relative"
    if [[ -f "$candidate/Jellyfin.Plugin.Kinopoisk.dll" ]]; then
      CURRENT_PLUGIN_DIR="$candidate"
      return 0
    fi
  fi

  mapfile -t candidates < <(
    find "$PLUGIN_ROOT" \
      -mindepth 2 -maxdepth 2 \
      -type f -name 'Jellyfin.Plugin.Kinopoisk.dll' \
      -printf '%h\n' \
      | sort -u
  )
  if [[ "${#candidates[@]}" -ne 1 ]]; then
    printf 'Найденные каталоги КиноПоиска:\n' >&2
    printf '  %s\n' "${candidates[@]:-отсутствуют}" >&2
    fail "Не удалось однозначно определить активный каталог плагина."
  fi
  CURRENT_PLUGIN_DIR="${candidates[0]}"
}

verify_current_files() {
  [[ -d "$CURRENT_PLUGIN_DIR" ]] || fail "Каталог плагина не найден: $CURRENT_PLUGIN_DIR"
  [[ -f "$CURRENT_PLUGIN_DIR/Jellyfin.Plugin.Kinopoisk.dll" ]] \
    || fail "В активном каталоге отсутствует основная DLL."
  [[ -f "$CURRENT_PLUGIN_DIR/KinopoiskUnofficialInfo.ApiClient.dll" ]] \
    || fail "В активном каталоге отсутствует DLL API-клиента."
  [[ -f "$CURRENT_PLUGIN_DIR/meta.json" ]] \
    || fail "В активном каталоге отсутствует meta.json."
  [[ -f "$PLUGIN_CONFIG" ]] || fail "Конфигурация плагина не найдена: $PLUGIN_CONFIG"
}

discover_environment() {
  CURRENT_STAGE="Обнаружение среды media-core"
  validate_base_compose
  local container_id
  container_id="$(resolve_container_id)"
  resolve_config_source "$container_id"
  resolve_web_index_path "$container_id"
  resolve_current_plugin_dir
  verify_current_files
}

container_status() {
  docker inspect "$1" \
    --format '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}|{{.RestartCount}}|{{.State.OOMKilled}}|{{.State.StartedAt}}|{{.Id}}'
}

verify_container_security() {
  local container_id="$1"
  local inspect_file="$2"
  docker inspect "$container_id" > "$inspect_file"
  python3 - "$inspect_file" "$MANAGED_INDEX" "$WEB_INDEX_PATH" <<'PY'
import json
import sys
from pathlib import Path

inspect_path, expected_source, expected_target = sys.argv[1:]
payload = json.loads(Path(inspect_path).read_text(encoding="utf-8"))
if not payload:
    raise SystemExit("docker inspect не вернул контейнер.")
container = payload[0]
if container.get("HostConfig", {}).get("ReadonlyRootfs") is not True:
    raise SystemExit("ReadonlyRootfs контейнера Jellyfin отключён.")
matching = [m for m in container.get("Mounts", []) if m.get("Destination") == expected_target]
if len(matching) != 1:
    raise SystemExit(
        f"Ожидалось одно монтирование index.html в {expected_target}, обнаружено {len(matching)}."
    )
mount = matching[0]
if mount.get("Source") != expected_source:
    raise SystemExit("Источник read-only index.html не совпадает с ожидаемым.")
if mount.get("RW") is not False:
    raise SystemExit("Управляемый index.html смонтирован с правом записи.")
print("Контейнер: ReadonlyRootfs=true; index.html смонтирован read-only.")
PY
}

wait_healthy() {
  local attempts="${1:-60}"
  local delay="${2:-3}"
  local attempt state health
  for attempt in $(seq 1 "$attempts"); do
    state="$(docker inspect "$CONTAINER_NAME" --format '{{.State.Status}}')"
    health="$(docker inspect "$CONTAINER_NAME" --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}')"
    log "Попытка ${attempt}/${attempts}: state=${state}; health=${health}"
    if [[ "$state" == "running" && "$health" == "healthy" ]]; then
      return 0
    fi
    if [[ "$state" == "exited" || "$state" == "dead" ]]; then
      fail "Контейнер Jellyfin остановился: $state"
      return 1
    fi
    sleep "$delay"
  done
  fail "Jellyfin не перешёл в healthy за отведённое время."
}

verify_http_runtime() {
  local workdir="$1"
  local info_file="$workdir/system-info.json"
  local index_file="$workdir/index.html"
  local js_file="$workdir/web-client.js"
  local headers_file="$workdir/web-client.headers"
  local etag http_code

  curl -fsS --max-time 15 "$BASE_URL/System/Info/Public" -o "$info_file"
  python3 - "$info_file" "$EXPECTED_JELLYFIN_VERSION" <<'PY'
import json
import sys
from pathlib import Path

path, expected = sys.argv[1:]
payload = json.loads(Path(path).read_text(encoding="utf-8"))
version = payload.get("Version")
if version != expected:
    raise SystemExit(f"Версия Jellyfin {version!r} не совпала с {expected!r}.")
print(f"Jellyfin API: Version={version}")
PY

  curl -fsS --max-time 15 "$BASE_URL/web/index.html" -o "$index_file"
  python3 "$INDEX_TOOL" check --input "$index_file"

  curl -fsS --max-time 15 \
    -D "$headers_file" \
    "$BASE_URL/Kinopoisk/WebClient.js" \
    -o "$js_file"

  python3 - "$headers_file" "$js_file" <<'PY'
import re
import sys
from pathlib import Path

headers_path, js_path = sys.argv[1:]
headers = Path(headers_path).read_text(encoding="utf-8", errors="replace")
script = Path(js_path).read_text(encoding="utf-8")
if not re.search(r"(?im)^content-type:\s*(?:application|text)/javascript(?:;|$)", headers):
    raise SystemExit("Endpoint вернул некорректный Content-Type.")
if not re.search(r"(?im)^etag:\s*\"[0-9a-f]{64}\"\s*$", headers):
    raise SystemExit("Endpoint не вернул корректный SHA-256 ETag.")
if not re.search(r"(?im)^x-content-type-options:\s*nosniff\s*$", headers):
    raise SystemExit("Endpoint не вернул X-Content-Type-Options: nosniff.")
required = (
    ".btnPlayTrailer",
    "Похожие и рекомендации",
    "kp-recommendations-scroller",
    "widgets.kinopoisk.ru",
)
missing = [value for value in required if value not in script]
if missing:
    raise SystemExit("В bundle отсутствуют маркеры: " + ", ".join(missing))
for forbidden in ("X-API-KEY", "kinopoiskapiunofficial.tech/api", "Authorization: Bearer"):
    if forbidden.lower() in script.lower():
        raise SystemExit(f"В bundle найден запрещённый секретный маркер: {forbidden}")
print("WebClient.js: MIME, ETag, nosniff и состав bundle подтверждены.")
PY

  etag="$(awk 'BEGIN{IGNORECASE=1} /^ETag:/ {sub(/\r$/, "", $2); print $2; exit}' "$headers_file")"
  [[ -n "$etag" ]] || fail "Не удалось извлечь ETag веб-клиента."
  http_code="$(
    curl -sS --max-time 15 \
      -o /dev/null \
      -w '%{http_code}' \
      -H "If-None-Match: $etag" \
      "$BASE_URL/Kinopoisk/WebClient.js"
  )"
  [[ "$http_code" == "304" ]] || fail "Условный запрос вернул HTTP $http_code вместо 304."
  log "WebClient.js: условный запрос 304 подтверждён."
}

verify_logs() {
  local since_value="$1"
  local log_file="$2"
  docker logs --since "$since_value" "$CONTAINER_NAME" > "$log_file" 2>&1

  grep -Fq "Loaded assembly Jellyfin.Plugin.Kinopoisk, Version=${TARGET_VERSION}" "$log_file" \
    || fail "В журнале не подтверждена загрузка основной DLL версии $TARGET_VERSION."
  grep -Fq "Loaded plugin: КиноПоиск ${TARGET_VERSION}" "$log_file" \
    || fail "В журнале не подтверждена активация плагина версии $TARGET_VERSION."
  grep -Fq "Автономный веб-клиент КиноПоиска готов" "$log_file" \
    || fail "В журнале не подтверждена готовность автономного веб-клиента."

  if grep -Eiq \
    'Failed to load.*Kinopoisk|Could not load.*Kinopoisk|BadImageFormatException|FileLoadException|TypeLoadException|MissingMethodException|Unable to resolve service|No service for type|AmbiguousMatchException' \
    "$log_file"; then
    grep -Ei \
      'Kinopoisk|КиноПоиск|BadImageFormatException|FileLoadException|TypeLoadException|MissingMethodException|Unable to resolve service|No service for type|AmbiguousMatchException' \
      "$log_file" | tail -n 160 >&2 || true
    fail "В журнале обнаружена критическая ошибка загрузки или DI."
  fi

  if grep -Eiq 'franchise\.(preview|apply)\.started|Предварительный просмотр франшиз КиноПоиска.*запущ|Применение франшиз КиноПоиска.*запущ' "$log_file"; then
    fail "Во время установки неожиданно запустилась Preview или Apply-задача."
  fi
  log "Журнал Jellyfin: загрузка плагина подтверждена; критических ошибок и автозапуска задач нет."
}

verify_installed_files() {
  local expected_config_sha="$1"
  local main_sha client_sha config_sha meta_version
  [[ -d "$TARGET_PLUGIN_DIR" ]] || fail "Целевой каталог плагина отсутствует: $TARGET_PLUGIN_DIR"
  main_sha="$(sha256_file "$TARGET_PLUGIN_DIR/Jellyfin.Plugin.Kinopoisk.dll")"
  client_sha="$(sha256_file "$TARGET_PLUGIN_DIR/KinopoiskUnofficialInfo.ApiClient.dll")"
  [[ "$main_sha" == "$(sha256_file "$SCRIPT_DIR/Jellyfin.Plugin.Kinopoisk.dll")" ]] \
    || fail "SHA-256 установленной основной DLL не совпал."
  [[ "$client_sha" == "$(sha256_file "$SCRIPT_DIR/KinopoiskUnofficialInfo.ApiClient.dll")" ]] \
    || fail "SHA-256 установленной DLL API-клиента не совпал."

  meta_version="$(python3 - "$TARGET_PLUGIN_DIR/meta.json" <<'PY'
import json
import sys
print(json.load(open(sys.argv[1], encoding="utf-8")).get("version", ""))
PY
)"
  [[ "$meta_version" == "$TARGET_VERSION" ]] \
    || fail "meta.json содержит версию $meta_version вместо $TARGET_VERSION."

  config_sha="$(sha256_file "$PLUGIN_CONFIG")"
  [[ "$config_sha" == "$expected_config_sha" ]] \
    || fail "XML-конфигурация плагина изменилась."
  log "Установленные DLL, meta.json и неизменность XML-конфигурации подтверждены."
}

verify_proxy_unchanged() {
  local expected_id="$1"
  local expected_started="$2"
  local expected_restarts="$3"
  [[ -n "$expected_id" ]] || return 0
  local inspect_file
  inspect_file="$(mktemp)"
  docker inspect "$PROXY_CONTAINER" > "$inspect_file"
  python3 - "$inspect_file" "$expected_id" "$expected_started" "$expected_restarts" <<'PY'
import json
import sys
from pathlib import Path

path, expected_id, expected_started, expected_restarts = sys.argv[1:]
container = json.loads(Path(path).read_text(encoding="utf-8"))[0]
actual_id = container.get("Id")
actual_started = container.get("State", {}).get("StartedAt")
actual_restarts = str(container.get("RestartCount"))
if actual_id != expected_id:
    raise SystemExit("Контейнер egress-proxy был заменён.")
if actual_started != expected_started:
    raise SystemExit("Контейнер egress-proxy был перезапущен.")
if actual_restarts != expected_restarts:
    raise SystemExit("Счётчик перезапусков egress-proxy изменился.")
print("jellyfin-egress-proxy не заменялся и не перезапускался.")
PY
  rm -f -- "$inspect_file"
}

load_transaction_state() {
  local transaction="$1"
  [[ -d "$transaction" ]] || fail "Транзакция не найдена: $transaction"
  [[ -f "$transaction/state.env" ]] || fail "state.env транзакции отсутствует."
  # shellcheck disable=SC1090
  source "$transaction/state.env"
}

resolve_transaction_argument() {
  local supplied="${1:-}"
  if [[ -n "$supplied" ]]; then
    printf '%s\n' "$supplied"
    return 0
  fi
  [[ -f "$BACKUP_ROOT/LATEST" ]] || fail "Указатель последней транзакции отсутствует."
  head -n 1 "$BACKUP_ROOT/LATEST"
}

run_verify() {
  local transaction="${1:-}"
  local since_value expected_config_sha proxy_id proxy_started proxy_restarts
  local workdir inspect_file container_id

  verify_package
  discover_environment

  if [[ -n "$transaction" ]]; then
    load_transaction_state "$transaction"
    since_value="${TRANSACTION_STARTED_UTC:-10m}"
    expected_config_sha="${ORIGINAL_CONFIG_SHA:-$(sha256_file "$PLUGIN_CONFIG")}"
    proxy_id="${ORIGINAL_PROXY_ID:-}"
    proxy_started="${ORIGINAL_PROXY_STARTED_AT:-}"
    proxy_restarts="${ORIGINAL_PROXY_RESTARTS:-}"
  else
    since_value="10m"
    expected_config_sha="$(sha256_file "$PLUGIN_CONFIG")"
    proxy_id=""
    proxy_started=""
    proxy_restarts=""
  fi

  workdir="$(mktemp -d)"
  inspect_file="$workdir/jellyfin-inspect.json"
  container_id="$(resolve_container_id)"

  CURRENT_STAGE="Проверка состояния Jellyfin"
  wait_healthy 60 3
  verify_installed_files "$expected_config_sha"
  verify_container_security "$container_id" "$inspect_file"
  verify_http_runtime "$workdir"
  verify_logs "$since_value" "$workdir/jellyfin.log"
  verify_proxy_unchanged "$proxy_id" "$proxy_started" "$proxy_restarts"

  rm -rf -- "$workdir"
  log "ПОЛНАЯ RUNTIME-ПРОВЕРКА АВТОНОМНОГО ПЛАГИНА УСПЕШНО ЗАВЕРШЕНА."
}

write_transaction_state() {
  local original_config_sha="$1"
  local original_main_sha="$2"
  local original_client_sha="$3"
  local proxy_id="$4"
  local proxy_started="$5"
  local proxy_restarts="$6"
  local override_existed="$7"
  local index_existed="$8"
  local transaction_started="$9"

  cat > "$TRANSACTION_DIR/state.env" <<EOF
TARGET_VERSION=$(shell_quote "$TARGET_VERSION")
COMPOSE_FILE=$(shell_quote "$COMPOSE_FILE")
COMPOSE_PROJECT=$(shell_quote "$COMPOSE_PROJECT")
OVERRIDE_FILE=$(shell_quote "$OVERRIDE_FILE")
SERVICE_NAME=$(shell_quote "$SERVICE_NAME")
CONTAINER_NAME=$(shell_quote "$CONTAINER_NAME")
PROXY_CONTAINER=$(shell_quote "$PROXY_CONTAINER")
BASE_URL=$(shell_quote "$BASE_URL")
CONFIG_SOURCE=$(shell_quote "$CONFIG_SOURCE")
PLUGIN_ROOT=$(shell_quote "$PLUGIN_ROOT")
ORIGINAL_PLUGIN_DIR=$(shell_quote "$CURRENT_PLUGIN_DIR")
TARGET_PLUGIN_DIR=$(shell_quote "$TARGET_PLUGIN_DIR")
PLUGIN_CONFIG=$(shell_quote "$PLUGIN_CONFIG")
MANAGED_INDEX=$(shell_quote "$MANAGED_INDEX")
WEB_INDEX_PATH=$(shell_quote "$WEB_INDEX_PATH")
ORIGINAL_CONFIG_SHA=$(shell_quote "$original_config_sha")
ORIGINAL_MAIN_SHA=$(shell_quote "$original_main_sha")
ORIGINAL_CLIENT_SHA=$(shell_quote "$original_client_sha")
ORIGINAL_PROXY_ID=$(shell_quote "$proxy_id")
ORIGINAL_PROXY_STARTED_AT=$(shell_quote "$proxy_started")
ORIGINAL_PROXY_RESTARTS=$(shell_quote "$proxy_restarts")
OVERRIDE_EXISTED=$(shell_quote "$override_existed")
MANAGED_INDEX_EXISTED=$(shell_quote "$index_existed")
TRANSACTION_STARTED_UTC=$(shell_quote "$transaction_started")
EOF
  chmod 0600 "$TRANSACTION_DIR/state.env"
}

create_rollback_entrypoint() {
  cp -a -- "$0" "$TRANSACTION_DIR/installer.sh"
  chmod 0700 "$TRANSACTION_DIR/installer.sh"
  cat > "$TRANSACTION_DIR/rollback.sh" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
exec $(shell_quote "$TRANSACTION_DIR/installer.sh") __rollback-internal $(shell_quote "$TRANSACTION_DIR")
EOF
  chmod 0700 "$TRANSACTION_DIR/rollback.sh"
}

prepare_transaction_backup() {
  CURRENT_STAGE="Создание транзакционной резервной копии"
  local stamp original_config_sha original_main_sha original_client_sha
  local proxy_json proxy_id proxy_started proxy_restarts
  local override_existed=false index_existed=false transaction_started

  stamp="$(date -u +%Y%m%d-%H%M%S)"
  TRANSACTION_DIR="$BACKUP_ROOT/${stamp}-autonomous-${TARGET_VERSION}"
  [[ ! -e "$TRANSACTION_DIR" ]] || fail "Каталог транзакции уже существует: $TRANSACTION_DIR"
  mkdir -p "$TRANSACTION_DIR/plugin-original"

  cp -a -- "$CURRENT_PLUGIN_DIR/." "$TRANSACTION_DIR/plugin-original/"
  cp -a -- "$PLUGIN_CONFIG" "$TRANSACTION_DIR/Jellyfin.Plugin.Kinopoisk.xml"
  cp -a -- "$COMPOSE_FILE" "$TRANSACTION_DIR/compose.jellyfin.yaml"
  docker inspect "$CONTAINER_NAME" > "$TRANSACTION_DIR/jellyfin.inspect.before.json"

  original_config_sha="$(sha256_file "$PLUGIN_CONFIG")"
  original_main_sha="$(sha256_file "$CURRENT_PLUGIN_DIR/Jellyfin.Plugin.Kinopoisk.dll")"
  original_client_sha="$(sha256_file "$CURRENT_PLUGIN_DIR/KinopoiskUnofficialInfo.ApiClient.dll")"
  transaction_started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

  proxy_id=""
  proxy_started=""
  proxy_restarts=""
  if docker inspect "$PROXY_CONTAINER" >/dev/null 2>&1; then
    proxy_json="$TRANSACTION_DIR/proxy.inspect.before.json"
    docker inspect "$PROXY_CONTAINER" > "$proxy_json"
    read -r proxy_id proxy_started proxy_restarts < <(
      python3 - "$proxy_json" <<'PY'
import json
import sys
from pathlib import Path
container = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))[0]
print(container.get("Id", ""), container.get("State", {}).get("StartedAt", ""), container.get("RestartCount", ""))
PY
    )
  fi

  if [[ -f "$OVERRIDE_FILE" ]]; then
    override_existed=true
    cp -a -- "$OVERRIDE_FILE" "$TRANSACTION_DIR/compose.override.before.yaml"
  fi
  if [[ -f "$MANAGED_INDEX" ]]; then
    index_existed=true
    cp -a -- "$MANAGED_INDEX" "$TRANSACTION_DIR/index.before.html"
  fi

  write_transaction_state \
    "$original_config_sha" \
    "$original_main_sha" \
    "$original_client_sha" \
    "$proxy_id" \
    "$proxy_started" \
    "$proxy_restarts" \
    "$override_existed" \
    "$index_existed" \
    "$transaction_started"
  create_rollback_entrypoint

  cp -a -- "$CHECKSUM_FILE" "$TRANSACTION_DIR/package-SHA256SUMS"
  printf '%s\n' "$TRANSACTION_DIR" > "$BACKUP_ROOT/LATEST"
  TRANSACTION_READY=true
  log "Транзакционная резервная копия: $TRANSACTION_DIR"
  log "Точный откат: $TRANSACTION_DIR/rollback.sh"
}

update_meta_version() {
  local meta_file="$1"
  python3 - "$meta_file" "$TARGET_VERSION" <<'PY'
import json
import os
import sys
import tempfile
from pathlib import Path

path = Path(sys.argv[1])
version = sys.argv[2]
payload = json.loads(path.read_text(encoding="utf-8-sig"))
payload["version"] = version
content = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
fd, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
temporary = Path(temporary_name)
try:
    with os.fdopen(fd, "w", encoding="utf-8", newline="") as stream:
        stream.write(content)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(temporary, path.stat().st_mode & 0o777)
    os.replace(temporary, path)
finally:
    temporary.unlink(missing_ok=True)
PY
}

stage_target_plugin() {
  CURRENT_STAGE="Подготовка целевого каталога плагина"
  local staging_dir="$PLUGIN_ROOT/.КиноПоиск_${TARGET_VERSION}.staging.$$"
  local directory_uid directory_gid directory_mode
  [[ ! -e "$TARGET_PLUGIN_DIR" || "$TARGET_PLUGIN_DIR" == "$CURRENT_PLUGIN_DIR" ]] \
    || fail "Целевой каталог уже существует и не является активным: $TARGET_PLUGIN_DIR"
  rm -rf -- "$staging_dir"
  mkdir -p "$staging_dir"
  cp -a -- "$CURRENT_PLUGIN_DIR/." "$staging_dir/"

  directory_uid="$(stat -c '%u' "$CURRENT_PLUGIN_DIR")"
  directory_gid="$(stat -c '%g' "$CURRENT_PLUGIN_DIR")"
  directory_mode="$(stat -c '%a' "$CURRENT_PLUGIN_DIR")"

  install -o "$directory_uid" -g "$directory_gid" -m 0644 \
    "$SCRIPT_DIR/Jellyfin.Plugin.Kinopoisk.dll" \
    "$staging_dir/Jellyfin.Plugin.Kinopoisk.dll"
  install -o "$directory_uid" -g "$directory_gid" -m 0644 \
    "$SCRIPT_DIR/KinopoiskUnofficialInfo.ApiClient.dll" \
    "$staging_dir/KinopoiskUnofficialInfo.ApiClient.dll"

  for pdb in Jellyfin.Plugin.Kinopoisk.pdb KinopoiskUnofficialInfo.ApiClient.pdb; do
    if [[ -f "$SCRIPT_DIR/$pdb" ]]; then
      install -o "$directory_uid" -g "$directory_gid" -m 0644 \
        "$SCRIPT_DIR/$pdb" "$staging_dir/$pdb"
    else
      rm -f -- "$staging_dir/$pdb"
    fi
  done

  update_meta_version "$staging_dir/meta.json"
  chown -R "$directory_uid:$directory_gid" "$staging_dir"
  chmod "$directory_mode" "$staging_dir"

  [[ "$(sha256_file "$staging_dir/Jellyfin.Plugin.Kinopoisk.dll")" \
      == "$(sha256_file "$SCRIPT_DIR/Jellyfin.Plugin.Kinopoisk.dll")" ]] \
    || fail "Основная DLL в staging повреждена."
  [[ "$(sha256_file "$staging_dir/KinopoiskUnofficialInfo.ApiClient.dll")" \
      == "$(sha256_file "$SCRIPT_DIR/KinopoiskUnofficialInfo.ApiClient.dll")" ]] \
    || fail "DLL API-клиента в staging повреждена."

  if [[ "$CURRENT_PLUGIN_DIR" == "$TARGET_PLUGIN_DIR" ]]; then
    rm -rf -- "$CURRENT_PLUGIN_DIR"
  else
    rm -rf -- "$TARGET_PLUGIN_DIR"
    rm -rf -- "$CURRENT_PLUGIN_DIR"
  fi
  mv -- "$staging_dir" "$TARGET_PLUGIN_DIR"
  sync
  CURRENT_PLUGIN_DIR="$TARGET_PLUGIN_DIR"
}

apply_readonly_layer() {
  CURRENT_STAGE="Подготовка внешнего read-only index.html"
  JELLYFIN_COMPOSE_FILE="$COMPOSE_FILE" \
  JELLYFIN_COMPOSE_PROJECT="$COMPOSE_PROJECT" \
  JELLYFIN_OVERRIDE_FILE="$OVERRIDE_FILE" \
  JELLYFIN_SERVICE_NAME="$SERVICE_NAME" \
  JELLYFIN_CONTAINER_NAME="$CONTAINER_NAME" \
  JELLYFIN_WEB_OVERRIDE_ROOT="$WEB_OVERRIDE_ROOT" \
    bash "$PREPARE_SCRIPT" prepare
}

recreate_jellyfin() {
  CURRENT_STAGE="Пересоздание только Jellyfin"
  compose_with_override config --quiet
  compose_with_override \
    up -d \
    --no-deps \
    --force-recreate \
    --pull never \
    "$SERVICE_NAME"
}

internal_rollback() {
  local transaction="$1"
  local rollback_workdir
  ROLLBACK_RUNNING=true
  trap - ERR INT TERM
  set +e

  load_transaction_state "$transaction" || return 1
  rollback_workdir="$(mktemp -d)"
  log "Начат откат транзакции: $transaction"

  rm -rf -- "$TARGET_PLUGIN_DIR" || return 1
  rm -rf -- "$ORIGINAL_PLUGIN_DIR" || return 1
  mkdir -p -- "$ORIGINAL_PLUGIN_DIR" || return 1
  cp -a -- "$transaction/plugin-original/." "$ORIGINAL_PLUGIN_DIR/" || return 1

  if [[ -f "$transaction/Jellyfin.Plugin.Kinopoisk.xml" ]]; then
    rm -f -- "$PLUGIN_CONFIG" || return 1
    cp -a -- "$transaction/Jellyfin.Plugin.Kinopoisk.xml" "$PLUGIN_CONFIG" || return 1
  fi

  if [[ "$OVERRIDE_EXISTED" == "true" ]]; then
    rm -f -- "$OVERRIDE_FILE" || return 1
    cp -a -- "$transaction/compose.override.before.yaml" "$OVERRIDE_FILE" || return 1
  else
    rm -f -- "$OVERRIDE_FILE" || return 1
  fi

  if [[ "$MANAGED_INDEX_EXISTED" == "true" ]]; then
    rm -f -- "$MANAGED_INDEX" || return 1
    cp -a -- "$transaction/index.before.html" "$MANAGED_INDEX" || return 1
  else
    rm -f -- "$MANAGED_INDEX" || return 1
  fi

  sync || return 1

  if [[ "$OVERRIDE_EXISTED" == "true" ]]; then
    docker compose \
      -p "$COMPOSE_PROJECT" \
      -f "$COMPOSE_FILE" \
      -f "$OVERRIDE_FILE" \
      config --quiet || return 1
    docker compose \
      -p "$COMPOSE_PROJECT" \
      -f "$COMPOSE_FILE" \
      -f "$OVERRIDE_FILE" \
      up -d --no-deps --force-recreate --pull never "$SERVICE_NAME" || return 1
  else
    docker compose \
      -p "$COMPOSE_PROJECT" \
      -f "$COMPOSE_FILE" \
      config --quiet || return 1
    docker compose \
      -p "$COMPOSE_PROJECT" \
      -f "$COMPOSE_FILE" \
      up -d --no-deps --force-recreate --pull never "$SERVICE_NAME" || return 1
  fi

  wait_healthy 60 3 || return 1

  [[ "$(sha256_file "$ORIGINAL_PLUGIN_DIR/Jellyfin.Plugin.Kinopoisk.dll")" == "$ORIGINAL_MAIN_SHA" ]] \
    || return 1
  [[ "$(sha256_file "$ORIGINAL_PLUGIN_DIR/KinopoiskUnofficialInfo.ApiClient.dll")" == "$ORIGINAL_CLIENT_SHA" ]] \
    || return 1
  [[ "$(sha256_file "$PLUGIN_CONFIG")" == "$ORIGINAL_CONFIG_SHA" ]] || return 1

  printf '%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$transaction/ROLLED_BACK" || return 1
  rm -rf -- "$rollback_workdir"
  log "Откат успешно завершён. Восстановлен: $ORIGINAL_PLUGIN_DIR"
  return 0
}

on_error() {
  local code=$?
  local failed_stage="$CURRENT_STAGE"
  local failed_command="$BASH_COMMAND"
  trap - ERR INT TERM
  set +e

  printf '\nОШИБКА НА ЭТАПЕ: %s\n' "$failed_stage" >&2
  printf 'Команда: %s\n' "$failed_command" >&2
  printf 'Код: %s\n' "$code" >&2
  if [[ -n "$TRANSACTION_DIR" && -d "$TRANSACTION_DIR" ]]; then
    cat > "$TRANSACTION_DIR/FAILED.txt" <<EOF
Время UTC: $(date -u +%Y-%m-%dT%H:%M:%SZ)
Этап: $failed_stage
Команда: $failed_command
Код: $code
EOF
  fi

  if [[ "$APPLY_STARTED" == "true" && "$TRANSACTION_READY" == "true" && "$ROLLBACK_RUNNING" != "true" ]]; then
    printf 'Запускается автоматический откат: %s\n' "$TRANSACTION_DIR" >&2
    if internal_rollback "$TRANSACTION_DIR"; then
      printf 'Автоматический откат завершён успешно.\n' >&2
    else
      printf 'КРИТИЧЕСКАЯ ОШИБКА: автоматический откат не завершён.\n' >&2
      printf 'Ручной сценарий: %s/rollback.sh\n' "$TRANSACTION_DIR" >&2
    fi
  fi

  cleanup
  exit "$code"
}

run_plan() {
  verify_package
  discover_environment
  local container_id current_version current_main_sha current_client_sha config_sha
  local proxy_state="отсутствует"
  container_id="$(resolve_container_id)"
  current_version="$(python3 - "$CURRENT_PLUGIN_DIR/meta.json" <<'PY'
import json
import sys
print(json.load(open(sys.argv[1], encoding="utf-8")).get("version", "неизвестно"))
PY
)"
  current_main_sha="$(sha256_file "$CURRENT_PLUGIN_DIR/Jellyfin.Plugin.Kinopoisk.dll")"
  current_client_sha="$(sha256_file "$CURRENT_PLUGIN_DIR/KinopoiskUnofficialInfo.ApiClient.dll")"
  config_sha="$(sha256_file "$PLUGIN_CONFIG")"
  if docker inspect "$PROXY_CONTAINER" >/dev/null 2>&1; then
    proxy_state="$(container_status "$PROXY_CONTAINER")"
  fi

  cat <<EOF
============================================================
ПЛАН ТРАНЗАКЦИОННОЙ УСТАНОВКИ КИНОПОИСКА
============================================================
Версия кандидата:       $TARGET_VERSION
Compose-проект:         $COMPOSE_PROJECT
Compose-файл:           $COMPOSE_FILE
Compose override:       $OVERRIDE_FILE
Jellyfin container ID:  $container_id
Jellyfin Web index:     $WEB_INDEX_PATH
/config на хосте:       $CONFIG_SOURCE
Текущий каталог:        $CURRENT_PLUGIN_DIR
Текущая версия meta:    $current_version
Целевой каталог:        $TARGET_PLUGIN_DIR
XML-конфигурация:       $PLUGIN_CONFIG
Backup root:            $BACKUP_ROOT
Base URL:               $BASE_URL

Текущие SHA-256:
  Jellyfin.Plugin.Kinopoisk.dll:         $current_main_sha
  KinopoiskUnofficialInfo.ApiClient.dll: $current_client_sha
  XML-конфигурация:                      $config_sha

Целевые SHA-256:
  Jellyfin.Plugin.Kinopoisk.dll:         $(sha256_file "$SCRIPT_DIR/Jellyfin.Plugin.Kinopoisk.dll")
  KinopoiskUnofficialInfo.ApiClient.dll: $(sha256_file "$SCRIPT_DIR/KinopoiskUnofficialInfo.ApiClient.dll")

Proxy state:
  $proxy_state

apply --confirm выполнит:
1. Точную резервную копию текущего каталога плагина, XML, override и index.html.
2. Создание автономного внешнего index.html и read-only bind mount.
3. Установку отдельного каталога КиноПоиск_${TARGET_VERSION}.
4. Пересоздание только сервиса Jellyfin в проекте $COMPOSE_PROJECT.
5. Проверку health, API, read-only mount, WebClient.js, ETag/304 и журналов.
6. Автоматический откат при любой критической ошибке.

Режим plan: изменения файлов и контейнеров не выполнялись.
============================================================
EOF
}

run_apply() {
  local confirmation="${1:-}"
  [[ "$confirmation" == "--confirm" ]] \
    || fail "Для применения требуется явный аргумент: apply --confirm"
  [[ "$(id -u)" -eq 0 ]] || fail "Применение требуется запускать от root."

  trap on_error ERR INT TERM
  verify_package
  discover_environment

  if [[ "$CURRENT_PLUGIN_DIR" == "$TARGET_PLUGIN_DIR" \
    && "$(sha256_file "$CURRENT_PLUGIN_DIR/Jellyfin.Plugin.Kinopoisk.dll")" \
      == "$(sha256_file "$SCRIPT_DIR/Jellyfin.Plugin.Kinopoisk.dll")" \
    && "$(sha256_file "$CURRENT_PLUGIN_DIR/KinopoiskUnofficialInfo.ApiClient.dll")" \
      == "$(sha256_file "$SCRIPT_DIR/KinopoiskUnofficialInfo.ApiClient.dll")" ]]; then
    fail "Версия $TARGET_VERSION уже установлена; используйте verify."
  fi

  mkdir -p "$BACKUP_ROOT"
  prepare_transaction_backup
  APPLY_STARTED=true
  REPORT_FILE="$TRANSACTION_DIR/install-summary.txt"

  log "Начата транзакционная установка КиноПоиска $TARGET_VERSION"
  log "Транзакция: $TRANSACTION_DIR"
  apply_readonly_layer
  stage_target_plugin
  recreate_jellyfin
  CURRENT_STAGE="Ожидание healthy"
  wait_healthy 60 3
  run_verify "$TRANSACTION_DIR"

  printf '%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$TRANSACTION_DIR/APPLIED_OK"
  cat > "$REPORT_FILE" <<EOF
Версия: $TARGET_VERSION
Каталог: $TARGET_PLUGIN_DIR
Транзакция: $TRANSACTION_DIR
Основная DLL SHA-256: $(sha256_file "$TARGET_PLUGIN_DIR/Jellyfin.Plugin.Kinopoisk.dll")
API-клиент SHA-256: $(sha256_file "$TARGET_PLUGIN_DIR/KinopoiskUnofficialInfo.ApiClient.dll")
XML-конфигурация SHA-256: $(sha256_file "$PLUGIN_CONFIG")
Jellyfin: healthy
Автономный WebClient: проверен
Preview/Apply: не запускались
Ручной откат: $TRANSACTION_DIR/rollback.sh
EOF
  APPLY_STARTED=false
  trap - ERR INT TERM

  cat <<EOF
============================================================
АВТОНОМНЫЙ ПЛАГИН КИНОПОИСКА УСТАНОВЛЕН
Версия:             $TARGET_VERSION
Каталог:            $TARGET_PLUGIN_DIR
Транзакция:         $TRANSACTION_DIR
Ручной откат:       $TRANSACTION_DIR/rollback.sh
Отчёт:              $REPORT_FILE
Preview/Apply:       не запускались
============================================================
EOF
}

run_rollback() {
  local supplied="${1:-}"
  local confirmation="${2:-}"
  local transaction
  if [[ "$supplied" == "--confirm" ]]; then
    confirmation="$supplied"
    supplied=""
  fi
  [[ "$confirmation" == "--confirm" ]] \
    || fail "Для отката требуется: rollback [ТРАНЗАКЦИЯ] --confirm"
  [[ "$(id -u)" -eq 0 ]] || fail "Откат требуется запускать от root."
  transaction="$(resolve_transaction_argument "$supplied")"
  internal_rollback "$transaction" \
    || fail "Откат не завершён; проверьте состояние вручную."
}

main() {
  local command_name="${1:-plan}"
  shift || true

  for command in docker python3 sha256sum awk grep sed find strings curl install cp mv rm sync stat tee mktemp; do
    require_command "$command"
  done
  [[ -f "$COMPOSE_FILE" ]] || fail "Compose-файл не найден: $COMPOSE_FILE"

  case "$command_name" in
    plan)
      run_plan
      ;;
    apply)
      run_apply "${1:-}"
      ;;
    verify)
      local transaction=""
      if [[ "${1:-}" != "" ]]; then
        transaction="$(resolve_transaction_argument "$1")"
      elif [[ -f "$BACKUP_ROOT/LATEST" ]]; then
        transaction="$(resolve_transaction_argument "")"
      fi
      run_verify "$transaction"
      ;;
    rollback)
      run_rollback "${1:-}" "${2:-}"
      ;;
    __rollback-internal)
      internal_rollback "${1:?Транзакция не указана}"
      ;;
    -h|--help|help)
      usage
      ;;
    *)
      usage >&2
      fail "Неизвестная команда: $command_name"
      ;;
  esac
}

trap cleanup EXIT
main "$@"
