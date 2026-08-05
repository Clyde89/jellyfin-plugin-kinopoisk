#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd -P)"
ROOT="$(mktemp -d)"
trap 'rm -rf -- "$ROOT"' EXIT
PKG="$ROOT/package"
BIN="$ROOT/bin"
CONFIG="$ROOT/config"
PLUGIN_ROOT="$CONFIG/plugins"
OLD_DIR="$PLUGIN_ROOT/КиноПоиск_10.11.0.0"
COMPOSE="$ROOT/compose.jellyfin.yaml"
OVERRIDE="$ROOT/compose.jellyfin.kinopoisk-web.yaml"
WEBROOT="$ROOT/web/kinopoisk"
BACKUPS="$ROOT/backups"
STATE="$ROOT/fake-state"
DOCKER_LOG="$ROOT/docker.log"
EXPECTED_TARGET="/jellyfin/jellyfin-web/index.html"

mkdir -p "$PKG" "$BIN" "$OLD_DIR" "$PLUGIN_ROOT/configurations" "$WEBROOT" "$BACKUPS" "$STATE"

cp "$REPO_ROOT/tools/install-media-core-autonomous.sh" "$PKG/"
cp "$REPO_ROOT/tools/prepare-kinopoisk-readonly-index.py" "$PKG/"
cp "$REPO_ROOT/tools/prepare-media-core-readonly.sh" "$PKG/"
cp "$REPO_ROOT/src/Jellyfin.Plugin.Kinopoisk/bin/Release/net9.0/Jellyfin.Plugin.Kinopoisk.dll" "$PKG/"
cp "$REPO_ROOT/src/Jellyfin.Plugin.Kinopoisk/bin/Release/net9.0/Jellyfin.Plugin.Kinopoisk.pdb" "$PKG/"
cp "$REPO_ROOT/src/KinopoiskUnofficialInfo.ApiClient/bin/Release/net9.0/KinopoiskUnofficialInfo.ApiClient.dll" "$PKG/"
cp "$REPO_ROOT/src/KinopoiskUnofficialInfo.ApiClient/bin/Release/net9.0/KinopoiskUnofficialInfo.ApiClient.pdb" "$PKG/"
chmod +x "$PKG/"*.sh "$PKG/"*.py

printf 'old main\n' > "$OLD_DIR/Jellyfin.Plugin.Kinopoisk.dll"
printf 'old client\n' > "$OLD_DIR/KinopoiskUnofficialInfo.ApiClient.dll"
printf 'old pdb\n' > "$OLD_DIR/Jellyfin.Plugin.Kinopoisk.pdb"
printf '{"version":"10.11.0.0","imagePath":"logo.png"}\n' > "$OLD_DIR/meta.json"
printf 'logo\n' > "$OLD_DIR/logo.png"
printf '<PluginConfiguration><ApiToken>SECRET-TEST-TOKEN</ApiToken></PluginConfiguration>\n' > "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"
printf 'services:\n  jellyfin:\n    read_only: true\n' > "$COMPOSE"
(
  cd "$PKG"
  sha256sum \
    Jellyfin.Plugin.Kinopoisk.dll \
    KinopoiskUnofficialInfo.ApiClient.dll \
    Jellyfin.Plugin.Kinopoisk.pdb \
    KinopoiskUnofficialInfo.ApiClient.pdb \
    prepare-kinopoisk-readonly-index.py \
    prepare-media-core-readonly.sh \
    install-media-core-autonomous.sh \
    > SHA256SUMS
)
printf '0\n' > "$STATE/recreated"
: > "$DOCKER_LOG"

cat > "$BIN/docker" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
printf '%q ' "$@" >> "$FAKE_DOCKER_LOG"
printf '\n' >> "$FAKE_DOCKER_LOG"
json_escape() { python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1"; }
recreated="$(cat "$FAKE_STATE/recreated")"
case "${1:-}" in
  compose)
    arguments=" $* "
    if [[ "$arguments" == *" ps -q "* ]]; then
      printf '%s\n' 'fake-jellyfin-id'
      exit 0
    fi
    if [[ "$arguments" == *" config --format json "* ]]; then
      compose_file_count=0
      for argument in "$@"; do
        [[ "$argument" == "-f" ]] && compose_file_count=$((compose_file_count + 1))
      done
      if (( compose_file_count >= 2 )); then
        source_json="$(json_escape "$JELLYFIN_WEB_OVERRIDE_ROOT/index.html")"
        target_json="$(json_escape "$FAKE_WEB_INDEX_PATH")"
        printf '{"services":{"jellyfin":{"read_only":true,"volumes":[{"type":"bind","source":%s,"target":%s,"read_only":true}]}}}\n' "$source_json" "$target_json"
      else
        printf '%s\n' '{"services":{"jellyfin":{"read_only":true,"volumes":[]}}}'
      fi
      exit 0
    fi
    [[ "$arguments" == *" config --quiet "* ]] && exit 0
    if [[ "$arguments" == *" up -d "* ]]; then
      printf '1\n' > "$FAKE_STATE/recreated"
      exit 0
    fi
    printf 'Неожиданная команда docker compose: %s\n' "$*" >&2
    exit 91
    ;;
  ps)
    printf '%s\n' 'fake-jellyfin-id'
    ;;
  exec)
    if [[ "${2:-}" == "fake-jellyfin-id" && "${3:-}" == "test" && "${4:-}" == "-f" ]]; then
      exit 0
    fi
    exit 1
    ;;
  cp)
    destination="${3:-}"
    cat > "$destination" <<'HTML'
<!doctype html><html><head></head><body><main>Jellyfin</main></body></html>
HTML
    ;;
  logs)
    if [[ "${*: -1}" == "jellyfin" ]]; then
      if [[ "$recreated" == "1" ]]; then
        cat <<'LOG'
[INF] Loaded assembly Jellyfin.Plugin.Kinopoisk, Version=10.11.0.1, Culture=neutral, PublicKeyToken=null from /config/plugins/КиноПоиск_10.11.0.1/Jellyfin.Plugin.Kinopoisk.dll
[INF] Loaded assembly KinopoiskUnofficialInfo.ApiClient, Version=1.0.0.0 from /config/plugins/КиноПоиск_10.11.0.1/KinopoiskUnofficialInfo.ApiClient.dll
[INF] Loaded plugin: КиноПоиск 10.11.0.1
[INF] Автономный веб-клиент КиноПоиска готов
LOG
      else
        printf '%s\n' '[INF] Loaded assembly Jellyfin.Plugin.Kinopoisk, Version=10.11.0.0 from /config/plugins/КиноПоиск_10.11.0.0/Jellyfin.Plugin.Kinopoisk.dll'
      fi
    fi
    ;;
  inspect)
    target="${2:-}"
    if [[ "$*" == *"--format"* ]]; then
      format="${*: -1}"
      if [[ "$format" == *'.State.Health'* ]]; then
        printf '%s\n' 'healthy'
      else
        printf '%s\n' 'running'
      fi
      exit 0
    fi
    if [[ "$target" == "jellyfin-egress-proxy" ]]; then
      printf '%s\n' '[{"Id":"proxy-id","RestartCount":0,"State":{"Status":"running","Health":{"Status":"healthy"},"StartedAt":"2026-08-01T00:00:00Z","OOMKilled":false},"HostConfig":{"ReadonlyRootfs":true},"Mounts":[]}]'
      exit 0
    fi
    source_json="$(json_escape "$FAKE_CONFIG")"
    mounts="{\"Destination\":\"/config\",\"Source\":${source_json},\"RW\":true,\"Type\":\"bind\"}"
    if [[ "$recreated" == "1" ]]; then
      index_json="$(json_escape "$JELLYFIN_WEB_OVERRIDE_ROOT/index.html")"
      mounts="$mounts,{\"Destination\":\"/jellyfin/jellyfin-web/index.html\",\"Source\":${index_json},\"RW\":false,\"Type\":\"bind\"}"
    fi
    printf '[{"Id":"fake-jellyfin-id","RestartCount":0,"State":{"Status":"running","Health":{"Status":"healthy"},"StartedAt":"2026-08-02T00:00:00Z","OOMKilled":false},"HostConfig":{"ReadonlyRootfs":true},"Mounts":[%s]}]\n' "$mounts"
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
while (($#)); do
  case "$1" in
    -o) output="$2"; shift 2 ;;
    -D) headers="$2"; shift 2 ;;
    -w) write_format="$2"; shift 2 ;;
    -H) [[ "$2" == If-None-Match:* ]] && if_none_match="${2#If-None-Match: }"; shift 2 ;;
    --max-time) shift 2 ;;
    -f|-s|-S|-fsS|-sS) shift ;;
    http*) url="$1"; shift ;;
    *) shift ;;
  esac
done
if [[ "${FAKE_FAIL_WEB:-0}" == "1" && "$url" == */Kinopoisk/WebClient.js ]]; then
  exit 22
fi
write_body() {
  if [[ -n "$output" && "$output" != "/dev/null" ]]; then
    printf '%s' "$1" > "$output"
  else
    printf '%s' "$1"
  fi
}
case "$url" in
  */System/Info/Public)
    write_body '{"ServerName":"fake","Version":"10.11.11"}'
    ;;
  */web/index.html)
    cat "$JELLYFIN_WEB_OVERRIDE_ROOT/index.html" > "$output"
    ;;
  */Kinopoisk/WebClient.js)
    if [[ -n "$if_none_match" ]]; then
      [[ -n "$write_format" ]] && printf '304'
      exit 0
    fi
    if [[ -n "$headers" ]]; then
      cat > "$headers" <<'HEADERS'
HTTP/1.1 200 OK
Content-Type: application/javascript; charset=utf-8
ETag: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
X-Content-Type-Options: nosniff

HEADERS
    fi
    write_body 'const x=".btnPlayTrailer Похожие и рекомендации kp-recommendations-scroller widgets.kinopoisk.ru";'
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
grep -Fq 'Режим plan: изменения файлов и контейнеров не выполнялись.' "$ROOT/plan.log"
[[ -d "$OLD_DIR" ]]
[[ ! -d "$PLUGIN_ROOT/КиноПоиск_10.11.0.1" ]]

bash "$PKG/install-media-core-autonomous.sh" apply --confirm > "$ROOT/apply.log"
NEW_DIR="$PLUGIN_ROOT/КиноПоиск_10.11.0.1"
[[ -d "$NEW_DIR" ]]
[[ ! -d "$OLD_DIR" ]]
python3 - "$NEW_DIR/meta.json" <<'PY'
import json
import sys
assert json.load(open(sys.argv[1], encoding="utf-8"))["version"] == "10.11.0.1"
PY
transaction="$(cat "$BACKUPS/LATEST")"
[[ -x "$transaction/rollback.sh" ]]
[[ -f "$transaction/APPLIED_OK" ]]

bash "$PKG/install-media-core-autonomous.sh" verify "$transaction" > "$ROOT/verify.log"
bash "$PKG/install-media-core-autonomous.sh" rollback "$transaction" --confirm > "$ROOT/rollback.log"
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
[[ -f "$transaction/ROLLED_BACK" ]]
grep -Fq 'SECRET-TEST-TOKEN' "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"

printf '0\n' > "$STATE/recreated"
if FAKE_FAIL_WEB=1 bash "$PKG/install-media-core-autonomous.sh" apply --confirm > "$ROOT/apply-failed.log" 2>&1; then
  printf '%s\n' 'Намеренно повреждённая установка неожиданно завершилась успешно.' >&2
  exit 1
fi
auto_transaction="$(cat "$BACKUPS/LATEST")"
[[ -f "$auto_transaction/ROLLED_BACK" ]]
[[ -d "$OLD_DIR" ]]
[[ ! -d "$NEW_DIR" ]]
grep -Fq 'Автоматический откат завершён успешно.' "$ROOT/apply-failed.log"
grep -Fq 'SECRET-TEST-TOKEN' "$PLUGIN_ROOT/configurations/Jellyfin.Plugin.Kinopoisk.xml"

if grep -Eq 'jellyfin-egress-proxy.*up|up.*jellyfin-egress-proxy' "$DOCKER_LOG"; then
  printf '%s\n' 'Установщик затронул jellyfin-egress-proxy.' >&2
  exit 1
fi

printf '%s\n' 'Полный интеграционный тест plan/apply/verify/rollback/auto-rollback успешно пройден.'
