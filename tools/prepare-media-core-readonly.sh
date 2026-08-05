#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
INDEX_TOOL="${SCRIPT_DIR}/prepare-kinopoisk-readonly-index.py"

COMPOSE_FILE="${JELLYFIN_COMPOSE_FILE:-/srv/media-core/compose/compose.jellyfin.yaml}"
OVERRIDE_FILE="${JELLYFIN_OVERRIDE_FILE:-/srv/media-core/compose/compose.jellyfin.kinopoisk-web.yaml}"
SERVICE_NAME="${JELLYFIN_SERVICE_NAME:-jellyfin}"
CONTAINER_NAME="${JELLYFIN_CONTAINER_NAME:-jellyfin}"
OUTPUT_ROOT="${JELLYFIN_WEB_OVERRIDE_ROOT:-/srv/media-core/appdata/jellyfin-web/kinopoisk}"
MANAGED_INDEX="${OUTPUT_ROOT}/index.html"
BACKUP_DIR="${OUTPUT_ROOT}/backups"
STATE_FILE="${OUTPUT_ROOT}/deployment-state.env"

usage() {
  cat <<'EOF'
Использование:
  prepare-media-core-readonly.sh plan
  prepare-media-core-readonly.sh prepare

Команда plan выполняет только диагностику и ничего не изменяет.
Команда prepare:
  - копирует штатный index.html из работающего контейнера;
  - создаёт внешний управляемый index.html;
  - формирует отдельный Compose override с read-only bind mount;
  - проверяет объединённую Compose-конфигурацию;
  - НЕ заменяет DLL, НЕ пересоздаёт и НЕ перезапускает Jellyfin.

Переменные окружения:
  JELLYFIN_COMPOSE_FILE
  JELLYFIN_OVERRIDE_FILE
  JELLYFIN_SERVICE_NAME
  JELLYFIN_CONTAINER_NAME
  JELLYFIN_WEB_OVERRIDE_ROOT
EOF
}

fail() {
  printf 'ОШИБКА: %s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "Не найдена команда: $1"
}

resolve_container_id() {
  local container_id
  container_id="$(docker compose -f "$COMPOSE_FILE" ps -q "$SERVICE_NAME" 2>/dev/null || true)"
  if [[ -z "$container_id" ]]; then
    container_id="$(docker ps --filter "name=^/${CONTAINER_NAME}$" --format '{{.ID}}' | head -n 1)"
  fi
  [[ -n "$container_id" ]] || fail "Работающий контейнер Jellyfin не найден."
  printf '%s\n' "$container_id"
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
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  fail "В контейнере не найден штатный index.html Jellyfin Web."
}

print_plan() {
  local container_id="$1"
  local web_index_path="$2"

  cat <<EOF
=== План подготовки автономного веб-клиента КиноПоиска ===
Compose-файл:          $COMPOSE_FILE
Compose override:      $OVERRIDE_FILE
Сервис:                $SERVICE_NAME
Контейнер:             $container_id
Index в контейнере:    $web_index_path
Управляемый index:     $MANAGED_INDEX
Каталог резервных копий: $BACKUP_DIR

Защитные свойства:
- базовый Compose-файл не изменяется;
- read_only: true сохраняется;
- index.html подключается в контейнер только для чтения;
- отдельный JavaScript-файл в Jellyfin Web не создаётся;
- Jellyfin не перезапускается и не пересоздаётся этой утилитой;
- DLL плагина этой утилитой не заменяются.
EOF
}

write_override() {
  local web_index_path="$1"
  local temporary_file="$2"
  local source_json target_json

  source_json="$(python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$MANAGED_INDEX")"
  target_json="$(python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$web_index_path")"

  cat > "$temporary_file" <<EOF
services:
  ${SERVICE_NAME}:
    volumes:
      - type: bind
        source: ${source_json}
        target: ${target_json}
        read_only: true
EOF
}

validate_compose() {
  local web_index_path="$1"
  local rendered_json="$2"

  docker compose \
    -f "$COMPOSE_FILE" \
    -f "$OVERRIDE_FILE" \
    config --format json > "$rendered_json"

  python3 - "$rendered_json" "$SERVICE_NAME" "$MANAGED_INDEX" "$web_index_path" <<'PY'
import json
import sys
from pathlib import Path

config_path, service_name, expected_source, expected_target = sys.argv[1:]
config = json.loads(Path(config_path).read_text(encoding="utf-8"))
services = config.get("services", {})
service = services.get(service_name)
if not isinstance(service, dict):
    raise SystemExit(f"Сервис {service_name!r} отсутствует в объединённой конфигурации.")
if service.get("read_only") is not True:
    raise SystemExit("Защита read_only: true потеряна в объединённой конфигурации.")

matching = []
for volume in service.get("volumes", []):
    if not isinstance(volume, dict):
        continue
    if volume.get("target") == expected_target:
        matching.append(volume)

if len(matching) != 1:
    raise SystemExit(
        "Ожидалось ровно одно монтирование управляемого index.html, "
        f"обнаружено: {len(matching)}."
    )
volume = matching[0]
if volume.get("type") != "bind":
    raise SystemExit("Управляемый index.html подключён не как bind mount.")
if volume.get("source") != expected_source:
    raise SystemExit("Источник bind mount не совпадает с ожидаемым путём.")
if volume.get("read_only") is not True:
    raise SystemExit("Bind mount управляемого index.html не является read-only.")

print("Объединённая Compose-конфигурация проверена: read_only сохранён.")
PY
}

prepare() {
  local container_id="$1"
  local web_index_path="$2"
  local temporary_directory temporary_override rendered_json original_index
  local timestamp override_backup

  mkdir -p "$OUTPUT_ROOT" "$BACKUP_DIR" "$(dirname -- "$OVERRIDE_FILE")"
  temporary_directory="$(mktemp -d)"
  trap 'rm -rf -- "$temporary_directory"' RETURN

  original_index="${temporary_directory}/index.original.html"
  temporary_override="${temporary_directory}/compose.override.yaml"
  rendered_json="${temporary_directory}/compose.rendered.json"

  docker cp "${container_id}:${web_index_path}" "$original_index"
  python3 "$INDEX_TOOL" prepare \
    --input "$original_index" \
    --output "$MANAGED_INDEX" \
    --backup-dir "$BACKUP_DIR"
  python3 "$INDEX_TOOL" check --input "$MANAGED_INDEX"

  write_override "$web_index_path" "$temporary_override"

  if [[ -f "$OVERRIDE_FILE" ]] && ! cmp -s "$temporary_override" "$OVERRIDE_FILE"; then
    timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
    override_backup="${BACKUP_DIR}/$(basename -- "$OVERRIDE_FILE").${timestamp}.bak"
    cp -a -- "$OVERRIDE_FILE" "$override_backup"
    printf 'Резервная копия предыдущего Compose override: %s\n' "$override_backup"
  fi

  install -m 0644 "$temporary_override" "$OVERRIDE_FILE"
  validate_compose "$web_index_path" "$rendered_json"

  cat > "$STATE_FILE" <<EOF
JELLYFIN_COMPOSE_FILE=$(printf '%q' "$COMPOSE_FILE")
JELLYFIN_OVERRIDE_FILE=$(printf '%q' "$OVERRIDE_FILE")
JELLYFIN_SERVICE_NAME=$(printf '%q' "$SERVICE_NAME")
JELLYFIN_CONTAINER_NAME=$(printf '%q' "$CONTAINER_NAME")
JELLYFIN_WEB_INDEX_PATH=$(printf '%q' "$web_index_path")
JELLYFIN_MANAGED_INDEX=$(printf '%q' "$MANAGED_INDEX")
EOF
  chmod 0600 "$STATE_FILE"

  cat <<EOF

Подготовка завершена без изменения работающего контейнера.

Следующая команда только показывает итоговую конфигурацию:
  docker compose -f '$COMPOSE_FILE' -f '$OVERRIDE_FILE' config

Команда будущего применения после резервного копирования и замены DLL:
  docker compose -f '$COMPOSE_FILE' -f '$OVERRIDE_FILE' up -d --no-deps --force-recreate '$SERVICE_NAME'

Эта команда НЕ выполнялась.
EOF
}

main() {
  local command_name="${1:-plan}"
  local container_id web_index_path

  case "$command_name" in
    -h|--help|help)
      usage
      return 0
      ;;
    plan|prepare)
      ;;
    *)
      usage >&2
      fail "Неизвестная команда: $command_name"
      ;;
  esac

  require_command docker
  require_command python3
  require_command mktemp
  require_command install
  require_command cmp
  [[ -f "$COMPOSE_FILE" ]] || fail "Compose-файл не найден: $COMPOSE_FILE"
  [[ -f "$INDEX_TOOL" ]] || fail "Утилита index.html не найдена: $INDEX_TOOL"

  container_id="$(resolve_container_id)"
  web_index_path="$(resolve_web_index_path "$container_id")"
  print_plan "$container_id" "$web_index_path"

  if [[ "$command_name" == "prepare" ]]; then
    prepare "$container_id" "$web_index_path"
  else
    printf '\nРежим plan: файловые и контейнерные изменения не выполнялись.\n'
  fi
}

main "$@"
