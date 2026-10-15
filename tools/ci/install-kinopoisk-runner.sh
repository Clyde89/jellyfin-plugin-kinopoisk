#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

readonly repository='https://github.com/Clyde89/jellyfin-plugin-kinopoisk'
readonly runner_name='github-kinopoisk-ci-01'
readonly runner_user='kinopoisk-runner'
readonly runner_root='/opt/actions-runner-kinopoisk'
readonly service_name="actions.runner.Clyde89-jellyfin-plugin-kinopoisk.${runner_name}.service"
readonly marker="${runner_root}/.kinopoisk-install"
registration_token=''
trap 'unset registration_token' EXIT

fail() {
    printf 'ОШИБКА: %s\n' "$*" >&2
    exit 1
}

[[ "${EUID}" -eq 0 ]] || fail 'Запусти скрипт от root в консоли VM 403.'
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || fail 'Требуется Linux x64.'
[[ -d /run/systemd/system ]] || fail 'В этой системе не найден systemd.'
for utility in python3 tar runuser useradd getent install chown chmod stat id systemctl; do
    command -v "$utility" >/dev/null || fail "Не найдена команда ${utility}."
done

[[ ! -L "$runner_root" ]] || fail "${runner_root} не должен быть символьной ссылкой."
if [[ -e "$runner_root" ]]; then
    [[ -d "$runner_root" ]] || fail "${runner_root} не является каталогом."
    if [[ ! -f "${runner_root}/.runner" ]]; then
        [[ -f "$marker" && "$(cat "$marker")" == "$repository" ]] ||
            fail "Каталог ${runner_root} уже существует и не принадлежит этой установке."
    fi
fi

if [[ ! -f "${runner_root}/.runner" ]]; then
    source_root=''
    for candidate in /opt/actions-runner /opt/actions-runner-02 /opt/actions-runner-03 /opt/actions-runner-04; do
        if [[ -f "${candidate}/.runner" && -x "${candidate}/config.sh" &&
              -f "${candidate}/bin/Runner.Listener.dll" && -d "${candidate}/externals" ]]; then
            source_root="$candidate"
            break
        fi
    done
    [[ -n "$source_root" ]] || fail 'Не найдена существующая установка runner в /opt/actions-runner*.'

    printf 'Сервер: %s\nИсходная установка: %s\nНовый runner: %s\nРепозиторий: %s\n' \
        "$(hostname)" "$source_root" "$runner_name" "$repository"
    printf '\nОткрой репозиторий плагина: Settings → Actions → Runners → New self-hosted runner → Linux → x64.\n'
    printf 'Нужен только временный registration token из команды ./config.sh после --token.\n'
    printf 'Вставь token здесь; ввод скрыт, в чат его отправлять не нужно.\n'
    [[ -r /dev/tty ]] || fail 'Нужна интерактивная консоль для защищённого ввода token.'
    IFS= read -r -s -p 'Registration token: ' registration_token </dev/tty || fail 'Ввод token отменён.'
    printf '\n'
    [[ "$registration_token" =~ ^[A-Za-z0-9_-]{10,}$ ]] || fail 'Введён не registration token.'

    if ! getent passwd "$runner_user" >/dev/null; then
        useradd --system --user-group --create-home --home-dir "/var/lib/${runner_user}" \
            --shell /bin/bash "$runner_user"
    fi
    [[ "$(id -u "$runner_user")" -ne 0 ]] || fail 'Runner должен работать от отдельного непривилегированного пользователя.'

    if [[ ! -d "$runner_root" ]]; then
        install -d -o "$runner_user" -g "$runner_user" -m 0700 "$runner_root"
        # Копируется только дистрибутив: настройки, credentials, рабочие каталоги,
        # журналы и svc.sh существующего runner сюда не попадают.
        payload=()
        for item in bin externals config.sh env.sh run.sh run-helper.sh.template run-helper.cmd.template safe_sleep.sh LICENSE LICENSE.md LICENSE.txt NOTICE; do
            [[ ! -e "${source_root}/${item}" ]] || payload+=("$item")
        done
        tar --dereference -C "$source_root" -cf - "${payload[@]}" | tar -C "$runner_root" -xf -
        chown -R "${runner_user}:${runner_user}" "$runner_root"
        printf '%s\n' "$repository" > "$marker"
        chmod 0600 "$marker"
    fi

    [[ -x "${runner_root}/config.sh" ]] || fail 'В новой установке отсутствует config.sh.'
    runuser -u "$runner_user" -- /bin/bash -c '
        cd "$1"
        exec ./config.sh --unattended --url "$2" --token "$3" --name "$4" \
            --labels kinopoisk-ci --work _work
    ' bash "$runner_root" "$repository" "$registration_token" "$runner_name"
    unset registration_token
fi

# Проверяется привязка перед установкой или запуском службы. Credentials не читаются.
python3 - "${runner_root}/.runner" "$repository" "$runner_name" <<'PY'
import json
import sys
from pathlib import Path

settings = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
normalized = {key.lower(): value for key, value in settings.items()}
if str(normalized.get("githuburl", "")).rstrip("/").lower() != sys.argv[2].lower():
    raise SystemExit("ОШИБКА: существующий runner привязан к другому репозиторию.")
if normalized.get("agentname") != sys.argv[3]:
    raise SystemExit("ОШИБКА: имя существующего runner отличается от ожидаемого.")
PY

getent passwd "$runner_user" >/dev/null || fail 'Не найден пользователь runner.'
[[ "$(id -u "$runner_user")" -ne 0 ]] || fail 'Нельзя запускать runner от root.'
[[ "$(stat -c '%U' "$runner_root")" == "$runner_user" ]] || fail 'У каталога runner другой владелец.'
[[ -x "${runner_root}/svc.sh" ]] || fail 'При регистрации не был создан svc.sh.'
if [[ -f "${runner_root}/.service" ]]; then
    [[ "$(cat "${runner_root}/.service")" == "$service_name" ]] || fail 'Найдено другое имя службы.'
fi

cd "$runner_root"
if [[ "$(systemctl show "$service_name" -p LoadState --value 2>/dev/null)" == not-found ]]; then
    ./svc.sh install "$runner_user"
fi
[[ "$(systemctl show "$service_name" -p User --value)" == "$runner_user" ]] ||
    fail 'У существующей службы другой пользователь.'
systemctl enable --now "$service_name"
systemctl is-active --quiet "$service_name" || fail 'Служба runner не запустилась.'

printf '\nRUNNER_NAME=%s\nRUNNER_ROOT=%s\nRUNNER_USER=%s\nSERVICE=%s\nSERVICE_ACTIVE=yes\n' \
    "$runner_name" "$runner_root" "$runner_user" "$service_name"
printf 'REPOSITORY=%s\nLABELS=self-hosted,Linux,X64,kinopoisk-ci\n' "$repository"
printf 'Существующие четыре runner home-ai-assistant не перенастраивались.\n'
printf 'Runner подключается к GitHub; ожидающая публикация 12.1.0.4 сможет получить задание.\n'
