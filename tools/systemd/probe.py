#!/usr/bin/env python3
"""Проверка авторизованных маршрутов через современный заголовок MediaBrowser."""

import getpass
import json
import sys
import urllib.parse
import uuid

import deploy


def session_probe(url, username, password):
    identity = ('MediaBrowser Client="Kinopoisk acceptance", Device="Linux", '
                f'DeviceId="kinopoisk-acceptance-{uuid.uuid4().hex}", Version="{deploy.VERSION}"')
    headers = {"Authorization": identity, "Content-Type": "application/json"}
    status, _, body = deploy.request(url + "/Users/AuthenticateByName", headers,
                                     json.dumps({"Username": username, "Pw": password}).encode(), "POST")
    if status != 200:
        raise RuntimeError(f"Вход Jellyfin не выполнен: HTTP {status}")
    authentication = json.loads(body)
    token = authentication["AccessToken"]
    headers["Authorization"] = identity + f', Token="{token}"'
    try:
        status, _, body = deploy.request(url + "/KinopoiskPlayback/capabilities", headers)
        if status != 200 or not isinstance(json.loads(body), dict):
            raise RuntimeError(f"Авторизованный capabilities не ответил: HTTP {status}")
        admin = bool(authentication["User"].get("Policy", {}).get("IsAdministrator"))
        status, _, body = deploy.request(url + "/Plugins", headers)
        if admin:
            if status != 200:
                raise RuntimeError(f"Список плагинов не ответил: HTTP {status}")
            found = [p for p in json.loads(body) if p.get("Id", "").replace("-", "").lower() == deploy.GUID.replace("-", "")
                     and p.get("Version") == deploy.VERSION and p.get("Status") == "Active"]
            if len(found) != 1:
                raise RuntimeError("Активный кандидат не найден в API Jellyfin")
        elif status != 403:
            raise RuntimeError(f"Административный список плагинов вернул HTTP {status} вместо 403")
        status, _, body = deploy.request(url + "/KinopoiskPlayback/cache/statistics", headers)
        expected = 200 if admin else 403
        if status != expected or (admin and not isinstance(json.loads(body), dict)):
            raise RuntimeError(f"Статистика кэша вернула HTTP {status} вместо {expected}")
        parameters = urllib.parse.urlencode({"UserId": authentication["User"]["Id"],
                                            "IncludeItemTypes": "Movie,Series", "Recursive": "true", "Limit": 1})
        status, _, body = deploy.request(url + "/Items?" + parameters, headers)
        if status != 200:
            raise RuntimeError(f"Список тестовых материалов не ответил: HTTP {status}")
        count = json.loads(body).get("TotalRecordCount", 0)
        return {"modern_authorization": "PASS", "capabilities": "PASS",
                "admin_routes": "PASS" if admin else "CORRECTLY_FORBIDDEN",
                "visible_movies_and_series": count,
                "functional_acceptance": "PENDING"}
    finally:
        status, _, _ = deploy.request(url + "/Sessions/Logout", headers, b"", "POST")
        if status not in (200, 204):
            raise RuntimeError(f"Временная сессия не закрылась: HTTP {status}")


def main():
    try:
        if not sys.stdin.isatty():
            raise RuntimeError("Проверка входа запускается из интерактивной консоли внутри LXC")
        context = deploy.discover()
        username = input("Имя пользователя Jellyfin: ").strip()
        password = getpass.getpass("Пароль Jellyfin: ")
        result = session_probe(context["url"], username, password)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except Exception as error:
        detail = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        print("ОШИБКА: " + detail, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
