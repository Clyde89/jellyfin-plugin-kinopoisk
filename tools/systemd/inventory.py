#!/usr/bin/env python3
"""Прочитано состояние Jellyfin и КиноПоиска без обновления метаданных."""

import argparse
import getpass
import ipaddress
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

PLUGIN_GUID = "33e6d249648f44cda9ce497be06c08df"
PLUGIN_VERSION = "12.1.0.3"
FIELDS = "ProviderIds,RemoteTrailers,Overview,Genres,LocalTrailerCount,MediaSourceCount,DateLastRefreshed"


def field(value, name, default=None):
    if not isinstance(value, dict):
        raise RuntimeError("Сервер вернул объект неожиданного формата")
    for key, result in value.items():
        if key.lower() == name.lower():
            return result
    return default


def identifier(value):
    try:
        return uuid.UUID(str(value)).hex
    except (ValueError, TypeError, AttributeError):
        raise RuntimeError("Сервер вернул некорректный идентификатор") from None


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class LocalApi:
    def __init__(self, url):
        parsed = urllib.parse.urlsplit(url)
        try:
            local = parsed.hostname == "localhost" or ipaddress.ip_address(parsed.hostname).is_loopback
            parsed.port
        except (ValueError, TypeError):
            local = False
        if (not local or parsed.scheme not in ("http", "https") or parsed.username is not None
                or parsed.password is not None or parsed.query or parsed.fragment
                or ".." in parsed.path.split("/")):
            raise RuntimeError("Указан некорректный локальный URL Jellyfin")
        self.url = url.rstrip("/")
        self.identity = ('MediaBrowser Client="Kinopoisk offline inventory", Device="Console", '
                         f'DeviceId="kinopoisk-inventory-{uuid.uuid4().hex}", Version="{PLUGIN_VERSION}"')
        self.authorization = self.identity
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def allows_request(self, route, method):
        return method == "GET" or (method, route) in {
            ("POST", "/Users/AuthenticateByName"), ("POST", "/Sessions/Logout")}

    def request(self, route, method="GET", payload=None):
        if not route.startswith("/") or route.startswith("//"):
            raise RuntimeError("Указан некорректный маршрут проверки")
        if not self.allows_request(route, method):
            raise RuntimeError("Изменение библиотеки из проверки запрещено")
        body = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(self.url + route, data=body, method=method,
                                         headers={"Authorization": self.authorization,
                                                  "Content-Type": "application/json"})
        try:
            with self.opener.open(request, timeout=15) as response:
                if response.status == 204:
                    return None
                body = response.read(4 * 1024 * 1024 + 1)
                if len(body) > 4 * 1024 * 1024:
                    raise RuntimeError("Ответ сервера превысил предел проверки")
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"Маршрут {route.split('?')[0]} вернул HTTP {error.code}") from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise RuntimeError("Локальный Jellyfin не ответил в пределах времени ожидания") from None
        if not body:
            return None
        try:
            return json.loads(body)
        except (ValueError, UnicodeDecodeError):
            raise RuntimeError("Локальный Jellyfin вернул некорректный JSON") from None


def library_items(api, user_id):
    result = []
    seen = set()
    while True:
        parameters = urllib.parse.urlencode({
            "UserId": user_id, "IncludeItemTypes": "Movie,Series", "Recursive": "true",
            "Fields": FIELDS, "SortBy": "SortName", "StartIndex": len(result), "Limit": 100,
            "EnableTotalRecordCount": "true",
        })
        response = api.request("/Items?" + parameters)
        items = field(response, "Items")
        total = field(response, "TotalRecordCount")
        if not isinstance(items, list) or type(total) is not int or total < 0:
            raise RuntimeError("Список библиотеки вернулся в неожиданном формате")
        if total > 5000:
            raise RuntimeError("Для этой проверки размер библиотеки ограничен 5000 объектами")
        for item in items:
            item_id = identifier(field(item, "Id"))
            if item_id in seen or field(item, "Type") not in ("Movie", "Series"):
                raise RuntimeError("Список библиотеки изменился во время проверки")
            seen.add(item_id)
            result.append(item)
        if len(result) == total:
            return result
        if not items or len(result) > total:
            raise RuntimeError("Получен неполный список библиотеки")


def item_report(api, item):
    item_id = identifier(field(item, "Id"))
    providers = field(item, "ProviderIds", {}) or {}
    providers = {key.lower(): value for key, value in providers.items()
                 if key.lower() in ("kinopoisk", "imdb", "tmdb", "tvdb")}
    trailers = field(item, "RemoteTrailers", []) or []
    widgets = sum(urllib.parse.urlsplit(field(trailer, "Url", "") or "").hostname
                  == "widgets.kinopoisk.ru" for trailer in trailers)
    source_count = field(item, "MediaSourceCount")
    if field(item, "Type") == "Movie" and source_count is None:
        source_count = 1
    result = {
        "id": item_id, "type": field(item, "Type"), "name": field(item, "Name"),
        "year": field(item, "ProductionYear"), "provider_ids": providers,
        "overview_present": bool(field(item, "Overview")),
        "primary_image_present": bool(field(field(item, "ImageTags", {}) or {}, "Primary")),
        "premiere_date": field(item, "PremiereDate"), "rating": field(item, "CommunityRating"),
        "genres": field(item, "Genres", []) or [],
        "media_sources": source_count if field(item, "Type") == "Movie" else None,
        "remote_trailers": len(trailers), "kinopoisk_widgets": widgets,
        "local_trailers": field(item, "LocalTrailerCount"),
    }
    kp_id = providers.get("kinopoisk")
    if result["type"] != "Movie" or not str(kp_id).isascii() or not str(kp_id).isdigit() or int(kp_id) < 1:
        result["native_bridge"] = {"state": "SKIPPED_NO_LINKED_MOVIE"}
        return result
    status = api.request(f"/KinopoiskPlayback/native-bridge/{int(kp_id)}?movieId={item_id}")
    if field(status, "KinopoiskId") != int(kp_id) or identifier(field(status, "MovieId")) != item_id:
        raise RuntimeError("Состояние моста не совпало с выбранной карточкой")
    result["native_bridge"] = {key[0].lower() + key[1:]: field(status, key) for key in (
        "State", "Registered", "TrailerId", "LocalTrailerCount", "PlaybackMode",
        "CacheEnabled", "CacheReady", "CacheEntryBytes")}
    return result


def session_inventory(api, username, password):
    info = api.request("/System/Info/Public")
    version = field(info, "Version", "")
    if not isinstance(version, str) or not version.startswith("12.1."):
        raise RuntimeError("Проверка предназначена для Jellyfin 12.1")
    authentication = api.request("/Users/AuthenticateByName", "POST", {"Username": username, "Pw": password})
    token = field(authentication, "AccessToken")
    if not isinstance(token, str) or not token or any(c in token for c in '\r\n"'):
        raise RuntimeError("Сервер не подтвердил создание сессии")
    api.authorization = api.identity + f', Token="{token}"'
    try:
        user = field(authentication, "User")
        if not field(field(user, "Policy", {}), "IsAdministrator", False):
            raise RuntimeError("Для инвентаризации моста требуется администратор Jellyfin")
        plugins = api.request("/Plugins")
        active = [p for p in plugins if identifier(field(p, "Id")) == PLUGIN_GUID
                  and field(p, "Version") == PLUGIN_VERSION and field(p, "Status") == "Active"]
        if len(active) != 1:
            raise RuntimeError("Активный плагин 12.1.0.3 не найден")
        capabilities = api.request("/KinopoiskPlayback/capabilities")
        if not isinstance(field(capabilities, "NativeTrailerBridge"), dict):
            raise RuntimeError("Маршрут capabilities вернул неожиданный формат")
        statistics = api.request("/KinopoiskPlayback/cache/statistics")
        cache = field(statistics, "Cache", {})
        warmup = field(statistics, "Warmup", {})
        items = library_items(api, identifier(field(user, "Id")))
        rows = [item_report(api, item) for item in items]
        return {
            "inventory": "PASS", "jellyfin": version, "plugin": PLUGIN_VERSION,
            "visible_movies_and_series": len(rows),
            "movies_with_kinopoisk_id": sum(row["native_bridge"]["state"] != "SKIPPED_NO_LINKED_MOVIE" for row in rows),
            "cache": {key: field(cache, key) for key in (
                "Enabled", "CurrentBytes", "MaximumBytes", "FileCount", "StatisticsSinceUtc",
                "HitCount", "MissCount", "WriteCount", "FailedWriteCount")},
            "warmup_active": field(warmup, "ActiveCount"),
            "items": rows, "functional_acceptance": "PENDING",
        }
    finally:
        try:
            api.request("/Sessions/Logout", "POST", {})
        finally:
            api.authorization = api.identity


def main():
    parser = argparse.ArgumentParser(description="Прочитано состояние локального Jellyfin без внешнего API")
    parser.add_argument("--url", default="http://127.0.0.1:8096", help="Локальный URL с Base URL сервера")
    args = parser.parse_args()
    try:
        if not sys.stdin.isatty():
            raise RuntimeError("Проверка запускается из интерактивной консоли контейнера")
        api = LocalApi(args.url)
        username = input("Имя пользователя Jellyfin: ").strip()
        password = getpass.getpass("Пароль Jellyfin: ")
        result = session_inventory(api, username, password)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except Exception as error:
        detail = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        print("ОШИБКА: " + detail, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
