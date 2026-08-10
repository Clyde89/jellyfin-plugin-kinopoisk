#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd -P)"
ROOT="$(mktemp -d)"
TEST_BINARY_ROOT="${KINOPOISK_TEST_BINARY_ROOT:-}"

if [[ -n "$TEST_BINARY_ROOT" ]]; then
  MAIN_BINARY_ROOT="$TEST_BINARY_ROOT"
  API_CLIENT_BINARY_ROOT="$TEST_BINARY_ROOT"
else
  MAIN_BINARY_ROOT="$REPO_ROOT/src/Jellyfin.Plugin.Kinopoisk/bin/Release/net9.0"
  API_CLIENT_BINARY_ROOT="$REPO_ROOT/src/KinopoiskUnofficialInfo.ApiClient/bin/Release/net9.0"
fi

PKG="$ROOT/package"
BIN="$ROOT/bin"
CONFIG="$ROOT/config"
PLUGIN_ROOT="$CONFIG/plugins"
OLD_DIR="$PLUGIN_ROOT/КиноПоиск_10.11.0.0"
NEW_DIR="$PLUGIN_ROOT/КиноПоиск_10.11.0.7"
COMPOSE="$ROOT/compose.jellyfin.yaml"
OVERRIDE="$ROOT/compose.jellyfin.kinopoisk-web.yaml"
WEBROOT="$ROOT/web/kinopoisk"
MANAGED_INDEX="$WEBROOT/index.html"
BACKUPS="$ROOT/backups"
STATE="$ROOT/fake-state"
DOCKER_LOG="$ROOT/docker.log"
PUBLIC_API_COUNTER="$ROOT/public-api.counter"
EXPECTED_TARGET="/jellyfin/jellyfin-web/index.html"
LEGACY_OVERRIDE_CONTENT='services: legacy-runtime-test'
LEGACY_INDEX_CONTENT='<!doctype html><html><body data-kinopoisk-managed="external">legacy</body></html>'

failure_report() {
  local code="$1"
  local line="$2"
  local command="$3"
  trap - ERR
  set +e

  printf '\n=== Диагностика отказа теста установщика ===\n' >&2
  printf 'Код: %s\n' "$code" >&2
  printf 'Строка: %s\n' "$line" >&2
  printf 'Команда: %s\n' "$command" >&2
  if [[ -f "$STATE/mode" ]]; then
    printf 'Fake mode: %s\n' "$(cat "$STATE/mode")" >&2
  fi
  if [[ -f "$DOCKER_LOG" ]]; then
    printf '%s\n' '--- docker.log ---' >&2
    tail -n 120 "$DOCKER_LOG" >&2
  fi

  shopt -s nullglob
  local file
  for file in "$ROOT"/*.log; do
    printf '%s\n' "--- $(basename "$file") ---" >&2
    cat "$file" >&2
  done

  printf '%s\n' '--- дерево состояния ---' >&2
  find "$ROOT" -maxdepth 4 -type f -o -type d | sort >&2
  exit "$code"
}

trap 'failure_report "$?" "$LINENO" "$BASH_COMMAND"' ERR
trap 'rm -rf -- "$ROOT"' EXIT

mkdir -p \
  "$PKG" \
  "$BIN" \
  "$OLD_DIR" \
  "$PLUGIN_ROOT/configurations" \
  "$WEBROOT" \
  "$BACKUPS" \
  "$STATE"

cp "$REPO_ROOT/tools/install-media-core-autonomous.sh" "$PKG/"
cp "$MAIN_BINARY_ROOT/Jellyfin.Plugin.Kinopoisk.dll" "$PKG/"
cp "$MAIN_BINARY_ROOT/Jellyfin.Plugin.Kinopoisk.pdb" "$PKG/"
cp "$API_CLIENT_BINARY_ROOT/KinopoiskUnofficialInfo.ApiClient.dll" "$PKG/"
cp "$API_CLIENT_BINARY_ROOT/KinopoiskUnofficialInfo.ApiClient.pdb" "$PKG/"
chmod 0755 "$PKG/install-media-core-autonomous.sh"

printf 'old main\n' > "$OLD_DIR/Jellyfin.Plugin.Kinopoisk.dll"
printf 'old client\n' > "$OLD_DIR/KinopoiskUnofficialInfo.ApiClient.dll"
printf 'old pdb\n' > "$OLD_DIR/Jellyfin.Plugin.Kinopoisk.pdb"
printf '{"version":"10.11.0.0","imagePath":"logo.png"}\n' > "$OLD_DIR/meta.json"
printf 'logo\n' > "$OLD_DIR/logo.png"
printf '<PluginConfiguration><ApiToken>SECRET-TEST-TOKEN</ApiToken></PluginConfiguration>\n' \
  > "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"
printf 'services:\n  jellyfin:\n    read_only: true\n' > "$COMPOSE"
printf '%s\n' "$LEGACY_OVERRIDE_CONTENT" > "$OVERRIDE"
printf '%s\n' "$LEGACY_INDEX_CONTENT" > "$MANAGED_INDEX"

(
  cd "$PKG"
  sha256sum \
    Jellyfin.Plugin.Kinopoisk.dll \
    Jellyfin.Plugin.Kinopoisk.pdb \
    KinopoiskUnofficialInfo.ApiClient.dll \
    KinopoiskUnofficialInfo.ApiClient.pdb \
    install-media-core-autonomous.sh \
    > SHA256SUMS
)

printf 'legacy\n' > "$STATE/mode"
: > "$DOCKER_LOG"

cat > "$BIN/docker" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail

printf '%q ' "$@" >> "$FAKE_DOCKER_LOG"
printf '\n' >> "$FAKE_DOCKER_LOG"

json_escape() {
  python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1"
}

mode="$(cat "$FAKE_STATE/mode")"

case "${1:-}" in
  compose)
    arguments=" $* "
    if [[ "$arguments" == *" ps -q "* ]]; then
      printf '%s\n' 'fake-jellyfin-id'
      exit 0
    fi

    if [[ "$arguments" == *" config --format json "* ]]; then
      printf '%s\n' '{"services":{"jellyfin":{"read_only":true,"volumes":[]}}}'
      exit 0
    fi

    if [[ "$arguments" == *" config --quiet "* ]]; then
      exit 0
    fi

    if [[ "$arguments" == *" up -d "* ]]; then
      if [[ "$arguments" == *" $JELLYFIN_OVERRIDE_FILE "* ]]; then
        printf 'legacy\n' > "$FAKE_STATE/mode"
      else
        printf 'runtime\n' > "$FAKE_STATE/mode"
      fi
      exit 0
    fi

    printf 'Неожиданная команда docker compose: %s\n' "$*" >&2
    exit 91
    ;;

  ps)
    printf '%s\n' 'fake-jellyfin-id'
    ;;

  exec)
    if [[ "${2:-}" == "fake-jellyfin-id" \
      && "${3:-}" == "test" \
      && "${4:-}" == "-f" \
      && "${5:-}" == "$FAKE_WEB_INDEX_PATH" ]]; then
      exit 0
    fi
    exit 1
    ;;

  logs)
    if [[ "${*: -1}" != "jellyfin" ]]; then
      exit 0
    fi

    mode="$(cat "$FAKE_STATE/mode")"
    if [[ "$mode" == "runtime" ]]; then
      cat <<'LOG'
[INF] Loaded assembly Jellyfin.Plugin.Kinopoisk, Version=10.11.0.7, Culture=neutral, PublicKeyToken=null from /config/plugins/КиноПоиск_10.11.0.7/Jellyfin.Plugin.Kinopoisk.dll
[INF] Loaded assembly KinopoiskUnofficialInfo.ApiClient, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null from /config/plugins/КиноПоиск_10.11.0.7/KinopoiskUnofficialInfo.ApiClient.dll
[INF] Loaded plugin: КиноПоиск 10.11.0.7
[INF] Runtime Web Bootstrap КиноПоиска зарегистрирован в HTTP pipeline Jellyfin.
LOG
    else
      printf '%s\n' \
        '[INF] Loaded assembly Jellyfin.Plugin.Kinopoisk, Version=10.11.0.0, Culture=neutral, PublicKeyToken=null from /config/plugins/КиноПоиск_10.11.0.0/Jellyfin.Plugin.Kinopoisk.dll'
    fi
    ;;

  inspect)
    target="${2:-}"
    if [[ "$*" == *"--format"* ]]; then
      format="${*: -1}"
      if [[ "$format" == *'|'* ]]; then
        printf '%s\n' 'running|healthy|0|false|2026-08-08T00:00:00Z|fake-id'
      elif [[ "$format" == *'.State.Health'* ]]; then
        printf '%s\n' 'healthy'
      else
        printf '%s\n' 'running'
      fi
      exit 0
    fi

    if [[ "$target" == "jellyfin-egress-proxy" ]]; then
      printf '%s\n' \
        '[{"Id":"proxy-id","RestartCount":0,"State":{"Status":"running","Health":{"Status":"healthy"},"StartedAt":"2026-08-01T00:00:00Z","OOMKilled":false},"HostConfig":{"ReadonlyRootfs":true},"Mounts":[]}]'
      exit 0
    fi

    source_json="$(json_escape "$FAKE_CONFIG")"
    mounts="{\"Destination\":\"/config\",\"Source\":${source_json},\"RW\":true,\"Type\":\"bind\"}"
    mode="$(cat "$FAKE_STATE/mode")"
    if [[ "$mode" == "legacy" ]]; then
      index_json="$(json_escape "$JELLYFIN_WEB_OVERRIDE_ROOT/index.html")"
      target_json="$(json_escape "$FAKE_WEB_INDEX_PATH")"
      mounts="$mounts,{\"Destination\":${target_json},\"Source\":${index_json},\"RW\":false,\"Type\":\"bind\"}"
    fi

    printf '[{"Id":"fake-jellyfin-id","RestartCount":0,"State":{"Status":"running","Health":{"Status":"healthy"},"StartedAt":"2026-08-08T00:00:00Z","OOMKilled":false},"HostConfig":{"ReadonlyRootfs":true},"Mounts":[%s]}]\n' "$mounts"
    ;;

  *)
    printf 'Неожиданная команда docker: %s\n' "$*" >&2
    exit 93
    ;;
esac
STUB
chmod 0755 "$BIN/docker"

cat > "$BIN/curl" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail

output=""
headers=""
write_format=""
if_none_match=""
url=""
index_etag='"media-core-runtime-1111111111111111111111111111111111111111111111111111111111111111"'
webclient_etag='"2222222222222222222222222222222222222222222222222222222222222222"'
if [[ "${FAKE_WEAK_INDEX_ETAG:-0}" == "1" ]]; then
  index_etag='W/"media-core-runtime-1111111111111111111111111111111111111111111111111111111111111111"'
fi

while (($#)); do
  case "$1" in
    -o)
      output="$2"
      shift 2
      ;;
    -D)
      headers="$2"
      shift 2
      ;;
    -w)
      write_format="$2"
      shift 2
      ;;
    -H)
      if [[ "$2" == If-None-Match:* ]]; then
        if_none_match="${2#If-None-Match: }"
      fi
      shift 2
      ;;
    --max-time)
      shift 2
      ;;
    --connect-timeout)
      shift 2
      ;;
    -f|-s|-S|-fsS|-sS)
      shift
      ;;
    http*)
      url="$1"
      shift
      ;;
    *)
      shift
      ;;
  esac
done

write_body() {
  local body="$1"
  if [[ -n "$output" && "$output" != "/dev/null" ]]; then
    printf '%s' "$body" > "$output"
  elif [[ "$output" != "/dev/null" ]]; then
    printf '%s' "$body"
  fi
}

write_headers() {
  local kind="$1"
  [[ -n "$headers" ]] || return 0
  if [[ "$kind" == "index" ]]; then
    {
      printf '%s\n' 'HTTP/1.1 200 OK'
      printf '%s\n' 'Content-Type: text/html; charset=utf-8'
      if [[ "${FAKE_OMIT_INDEX_ETAG:-0}" != "1" ]]; then
        printf 'ETag: %s\n' "$index_etag"
      fi
      printf '%s\n' 'X-Kinopoisk-Web-Bootstrap: runtime'
      printf '%s\n\n' 'Cache-Control: no-cache'
    } > "$headers"
  else
    cat > "$headers" <<HEADERS
HTTP/1.1 200 OK
Content-Type: application/javascript; charset=utf-8
ETag: ${webclient_etag}
X-Content-Type-Options: nosniff

HEADERS
  fi
}

case "$url" in
  */System/Info/Public)
    if [[ -n "${FAKE_PUBLIC_API_FAILURES:-}" ]]; then
      api_attempt=0
      if [[ -f "${FAKE_PUBLIC_API_COUNTER:?}" ]]; then
        read -r api_attempt < "$FAKE_PUBLIC_API_COUNTER"
      fi
      api_attempt=$((api_attempt + 1))
      printf '%s\n' "$api_attempt" > "$FAKE_PUBLIC_API_COUNTER"
      if [[ "$api_attempt" -le "$FAKE_PUBLIC_API_FAILURES" ]]; then
        printf '%s\n' 'curl: (7) Failed to connect to fake port 8096' >&2
        [[ -n "$write_format" ]] && printf '000'
        exit 7
      fi
    fi
    if [[ "${FAKE_PUBLIC_API_HTTP_STATUS:-200}" != "200" ]]; then
      write_body '{"error":"fake"}'
      [[ -n "$write_format" ]] && printf '%s' "$FAKE_PUBLIC_API_HTTP_STATUS"
      exit 0
    fi
    write_body '{"ServerName":"fake","Version":"10.11.11"}'
    [[ -n "$write_format" ]] && printf '200'
    ;;

  */web/index.html)
    if [[ "$if_none_match" == "$index_etag" ]]; then
      [[ -n "$write_format" ]] && printf '304'
      exit 0
    fi
    write_headers index
    enhanced_script=''
    if [[ "${FAKE_JELLYFIN_ENHANCED:-0}" == "1" ]]; then
      enhanced_script='<script plugin="Jellyfin Enhanced" version="test" dev="false" src="../JellyfinEnhanced/script?v=test" defer></script>'
    fi
    write_body "<!doctype html><html><head><!-- KINOPOISK_WEB_CLIENT_BEGIN --><script data-kinopoisk-managed=\"runtime\" src=\"../Kinopoisk/WebClient.js?v=test\"></script><!-- KINOPOISK_WEB_CLIENT_END -->${enhanced_script}</head><body>Jellyfin</body></html>"
    ;;

  */Kinopoisk/WebClient.js)
    if [[ "${FAKE_FAIL_WEB:-0}" == "1" ]]; then
      exit 22
    fi
    if [[ "$if_none_match" == "$webclient_etag" ]]; then
      [[ -n "$write_format" ]] && printf '304'
      exit 0
    fi
    write_headers webclient
    write_body 'const x=".btnPlayTrailer Похожие и рекомендации kp-recommendations-scroller kinopoiskRecommendationLifecycleGuard";'
    ;;

  *)
    printf 'Неизвестный URL: %s\n' "$url" >&2
    exit 22
    ;;
esac
STUB
chmod 0755 "$BIN/curl"

export PATH="$BIN:$PATH"
export FAKE_DOCKER_LOG="$DOCKER_LOG"
export FAKE_STATE="$STATE"
export FAKE_CONFIG="$CONFIG"
export FAKE_WEB_INDEX_PATH="$EXPECTED_TARGET"
export JELLYFIN_COMPOSE_FILE="$COMPOSE"
export JELLYFIN_COMPOSE_PROJECT="media-core-jellyfin"
export JELLYFIN_OVERRIDE_FILE="$OVERRIDE"
export JELLYFIN_SERVICE_NAME="jellyfin"
export JELLYFIN_CONTAINER_NAME="jellyfin"
export JELLYFIN_PROXY_CONTAINER="jellyfin-egress-proxy"
export JELLYFIN_BASE_URL="http://fake:8096"
export JELLYFIN_KINOPOISK_BACKUP_ROOT="$BACKUPS"
export JELLYFIN_WEB_OVERRIDE_ROOT="$WEBROOT"

bash "$PKG/install-media-core-autonomous.sh" plan > "$ROOT/plan.log"
grep -Fq 'Legacy mount активен:   true' "$ROOT/plan.log"
grep -Fq 'Режим plan: изменения файлов и контейнеров не выполнялись.' "$ROOT/plan.log"
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
[[ -f "$OVERRIDE" ]]
[[ -f "$MANAGED_INDEX" ]]
[[ "$(cat "$STATE/mode")" == "legacy" ]]

FAKE_PUBLIC_API_FAILURES=2 \
FAKE_PUBLIC_API_COUNTER="$PUBLIC_API_COUNTER" \
  bash "$PKG/install-media-core-autonomous.sh" apply --confirm > "$ROOT/apply.log"
[[ -d "$NEW_DIR" ]]
[[ ! -d "$OLD_DIR" ]]
[[ ! -e "$OVERRIDE" ]]
[[ ! -e "$MANAGED_INDEX" ]]
[[ "$(cat "$STATE/mode")" == "runtime" ]]
python3 - "$NEW_DIR/meta.json" <<'PY'
import json
import sys
assert json.load(open(sys.argv[1], encoding="utf-8"))["version"] == "10.11.0.7"
PY

transaction="$(cat "$BACKUPS/LATEST")"
[[ -x "$transaction/rollback.sh" ]]
[[ -f "$transaction/APPLIED_OK" ]]
[[ -f "$transaction/compose.override.before.yaml" ]]
[[ -f "$transaction/index.before.html" ]]
grep -Fq "$LEGACY_OVERRIDE_CONTENT" "$transaction/compose.override.before.yaml"
grep -Fq 'data-kinopoisk-managed="external"' "$transaction/index.before.html"
grep -Fq 'Runtime Web Bootstrap: проверен' "$transaction/install-summary.txt"

grep -Fq 'Runtime Bootstrap:  проверен' "$ROOT/apply.log"
grep -Fq 'Legacy Web mount:   удалён' "$ROOT/apply.log"
grep -Fq 'сильный ETag "media-core-runtime-' "$ROOT/apply.log"
[[ "$(cat "$PUBLIC_API_COUNTER")" == "3" ]]
grep -Fq 'Jellyfin API временно недоступен: попытка 1/30; curl=7;' "$ROOT/apply.log"
grep -Fq 'Jellyfin API временно недоступен: попытка 2/30; curl=7;' "$ROOT/apply.log"
grep -Fq 'Jellyfin API доступен: попытка 3/30; HTTP=200.' "$ROOT/apply.log"

if FAKE_PUBLIC_API_HTTP_STATUS=404 \
  bash "$PKG/install-media-core-autonomous.sh" verify "$transaction" \
  > "$ROOT/verify-http-404.log" 2>&1; then
  printf '%s\n' 'Проверка с постоянным HTTP 404 неожиданно завершилась успешно.' >&2
  exit 1
fi
grep -Fq 'Jellyfin API вернул постоянный HTTP-статус 404.' "$ROOT/verify-http-404.log"
if grep -Fq 'попытка 2/30' "$ROOT/verify-http-404.log"; then
  printf '%s\n' 'Постоянный HTTP 404 был ошибочно отправлен на повторную проверку.' >&2
  exit 1
fi

bash "$PKG/install-media-core-autonomous.sh" verify "$transaction" > "$ROOT/verify.log"
grep -Fq 'ПОЛНАЯ RUNTIME-ПРОВЕРКА САМОДОСТАТОЧНОГО ПЛАГИНА УСПЕШНО ЗАВЕРШЕНА.' \
  "$ROOT/verify.log"

bash "$PKG/install-media-core-autonomous.sh" rollback "$transaction" --confirm > "$ROOT/rollback.log"
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
[[ -f "$OVERRIDE" ]]
[[ -f "$MANAGED_INDEX" ]]
[[ -f "$transaction/ROLLED_BACK" ]]
[[ "$(cat "$STATE/mode")" == "legacy" ]]
grep -Fq "$LEGACY_OVERRIDE_CONTENT" "$OVERRIDE"
grep -Fq 'data-kinopoisk-managed="external"' "$MANAGED_INDEX"
grep -Fq 'SECRET-TEST-TOKEN' "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"

sleep 1
FAKE_OMIT_INDEX_ETAG=1 FAKE_JELLYFIN_ENHANCED=1 \
  bash "$PKG/install-media-core-autonomous.sh" apply --confirm \
  > "$ROOT/apply-jellyfin-enhanced.log"

enhanced_transaction="$(cat "$BACKUPS/LATEST")"
[[ -f "$enhanced_transaction/APPLIED_OK" ]]
[[ -d "$NEW_DIR" ]]
[[ ! -d "$OLD_DIR" ]]
[[ ! -e "$OVERRIDE" ]]
[[ ! -e "$MANAGED_INDEX" ]]
[[ "$(cat "$STATE/mode")" == "runtime" ]]
grep -Fq 'Jellyfin Enhanced изменил итоговый HTML и штатно удалил устаревший ETag.' \
  "$ROOT/apply-jellyfin-enhanced.log"
grep -Fq 'проверка 304 пропущена только для композитного ответа Jellyfin Enhanced без ETag.' \
  "$ROOT/apply-jellyfin-enhanced.log"

bash "$PKG/install-media-core-autonomous.sh" \
  rollback "$enhanced_transaction" --confirm \
  > "$ROOT/rollback-jellyfin-enhanced.log"
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
[[ -f "$OVERRIDE" ]]
[[ -f "$MANAGED_INDEX" ]]
[[ "$(cat "$STATE/mode")" == "legacy" ]]

sleep 1
if FAKE_OMIT_INDEX_ETAG=1 bash "$PKG/install-media-core-autonomous.sh" apply --confirm \
  > "$ROOT/apply-missing-etag.log" 2>&1; then
  printf '%s\n' 'Установка без Runtime ETag и без Jellyfin Enhanced неожиданно завершилась успешно.' >&2
  exit 1
fi

missing_etag_transaction="$(cat "$BACKUPS/LATEST")"
[[ -f "$missing_etag_transaction/ROLLED_BACK" ]]
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
[[ -f "$OVERRIDE" ]]
[[ -f "$MANAGED_INDEX" ]]
[[ "$(cat "$STATE/mode")" == "legacy" ]]
grep -Fq 'совместимый внешний преобразователь Jellyfin Enhanced не подтверждён' \
  "$ROOT/apply-missing-etag.log"
grep -Fq 'Автоматический откат завершён успешно.' "$ROOT/apply-missing-etag.log"
grep -Fq 'SECRET-TEST-TOKEN' "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"

sleep 1
if FAKE_WEAK_INDEX_ETAG=1 bash "$PKG/install-media-core-autonomous.sh" apply --confirm \
  > "$ROOT/apply-weak-etag.log" 2>&1; then
  printf '%s\n' 'Установка со слабым Runtime ETag неожиданно завершилась успешно.' >&2
  exit 1
fi

weak_etag_transaction="$(cat "$BACKUPS/LATEST")"
[[ -f "$weak_etag_transaction/ROLLED_BACK" ]]
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
[[ -f "$OVERRIDE" ]]
[[ -f "$MANAGED_INDEX" ]]
[[ "$(cat "$STATE/mode")" == "legacy" ]]
grep -Fq 'содержит слабый ETag' "$ROOT/apply-weak-etag.log"
grep -Fq 'Автоматический откат завершён успешно.' "$ROOT/apply-weak-etag.log"
grep -Fq 'SECRET-TEST-TOKEN' "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"

sleep 1
if FAKE_FAIL_WEB=1 bash "$PKG/install-media-core-autonomous.sh" apply --confirm \
  > "$ROOT/apply-failed.log" 2>&1; then
  printf '%s\n' 'Намеренно повреждённая установка неожиданно завершилась успешно.' >&2
  exit 1
fi

auto_transaction="$(cat "$BACKUPS/LATEST")"
[[ -f "$auto_transaction/ROLLED_BACK" ]]
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
[[ -f "$OVERRIDE" ]]
[[ -f "$MANAGED_INDEX" ]]
[[ "$(cat "$STATE/mode")" == "legacy" ]]
grep -Fq 'Автоматический откат завершён успешно.' "$ROOT/apply-failed.log"
grep -Fq 'SECRET-TEST-TOKEN' "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"

if grep -Eq 'jellyfin-egress-proxy.*up|up.*jellyfin-egress-proxy' "$DOCKER_LOG"; then
  printf '%s\n' 'Установщик затронул jellyfin-egress-proxy.' >&2
  exit 1
fi

printf '%s\n' \
  'Интеграционный тест migration plan/apply/verify/rollback/auto-rollback Runtime Web Bootstrap успешно пройден.'
