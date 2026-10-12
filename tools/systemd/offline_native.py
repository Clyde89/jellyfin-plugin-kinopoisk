#!/usr/bin/env python3
"""Подготовлена автономная проверка LocalTrailer на отдельном тестовом фильме."""

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import getpass
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET

import deploy
import inventory

KP_ID = 2147000001
LIBRARY = "КиноПоиск — автономный тест"
TITLE = "КиноПоиск — автономная проверка трейлера"


def query(route, **values):
    return route + "?" + urllib.parse.urlencode(values)


class FixtureApi(inventory.LocalApi):
    def __init__(self, url):
        super().__init__(url)
        self.identity = self.identity.replace("Kinopoisk offline inventory", "Jellyfin Android TV offline acceptance")
        self.authorization = self.identity
        self.owned_ids = set()

    def allows_request(self, route, method):
        if super().allows_request(route, method):
            return True
        parsed = urllib.parse.urlsplit(route)
        if parsed.path == "/Library/VirtualFolders" and method in ("POST", "DELETE"):
            return urllib.parse.parse_qs(parsed.query).get("name") == [LIBRARY]
        parts = parsed.path.strip("/").split("/")
        return (method == "POST" and len(parts) == 3 and parts[0] == "Items"
                and parts[1] in self.owned_ids and parts[2] in ("Refresh", "PlaybackInfo"))

    def read_range(self, trailer_id, start, end):
        if trailer_id not in self.owned_ids:
            raise RuntimeError("Поток не относится к тестовому трейлеру")
        route = query(f"/Videos/{trailer_id}/stream.mp4", Static="true", MediaSourceId=trailer_id)
        request = urllib.request.Request(self.url + route, headers={
            "Authorization": self.authorization, "Range": f"bytes={start}-{end}"})
        try:
            with self.opener.open(request, timeout=15) as response:
                if response.status != 206:
                    raise RuntimeError(f"MP4 Range вернул HTTP {response.status} вместо 206")
                body = response.read(end - start + 2)
                content_range = response.headers.get("Content-Range", "")
                return body, content_range
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"MP4 Range вернул HTTP {error.code}") from None
        except (urllib.error.URLError, TimeoutError, OSError):
            raise RuntimeError("Локальный MP4-поток не ответил вовремя") from None


@contextmanager
def authenticated(api, username, password):
    reply = api.request("/Users/AuthenticateByName", "POST", {"Username": username, "Pw": password})
    token = inventory.field(reply, "AccessToken")
    if not isinstance(token, str) or not token or any(c in token for c in '\r\n"'):
        raise RuntimeError("Сессия Jellyfin не была создана")
    api.authorization = api.identity + f', Token="{token}"'
    try:
        user = inventory.field(reply, "User")
        if not inventory.field(inventory.field(user, "Policy", {}), "IsAdministrator", False):
            raise RuntimeError("Для тестовой библиотеки требуется администратор Jellyfin")
        yield inventory.identifier(inventory.field(user, "Id"))
    finally:
        try:
            api.request("/Sessions/Logout", "POST", {})
        finally:
            api.authorization = api.identity


def digest_items(items):
    fields = ("Name", "Type", "ProductionYear", "ProviderIds", "Overview", "PremiereDate",
              "CommunityRating", "Genres", "ImageTags", "RemoteTrailers")
    return {inventory.identifier(inventory.field(item, "Id")): hashlib.sha256(
        json.dumps({name: inventory.field(item, name) for name in fields},
                   ensure_ascii=False, sort_keys=True).encode()).hexdigest() for item in items}


def state_root(context):
    return Path(context["data"]) / "kinopoisk-offline-acceptance"


def write_state(root, state):
    path = root / "state.json"
    temporary = root / "state.json.tmp"
    with temporary.open("w", encoding="utf-8") as stream:
        os.fchmod(stream.fileno(), 0o600)
        json.dump(state, stream, ensure_ascii=False, indent=2)
    temporary.replace(path)


def read_state(context):
    root = state_root(context)
    path = root / "state.json"
    if root.is_symlink() or path.is_symlink() or not path.is_file():
        raise RuntimeError("Состояние тестовой библиотеки отсутствует")
    state = json.loads(path.read_text())
    expected_cache = str(Path(context["data"]) / "plugins/Jellyfin.Plugin.Kinopoisk/cache/trailers")
    if (state.get("schema") != 1 or state.get("library") != LIBRARY or state.get("kp_id") != KP_ID
            or state.get("cache_dir") != expected_cache):
        raise RuntimeError("Состояние теста не совпало с текущим сервером")
    for key in ("library_id", "movie_id", "trailer_id"):
        if state.get(key):
            state[key] = inventory.identifier(state[key])
    return state


def encoder_pair():
    for directory in (Path("/usr/lib/jellyfin-ffmpeg"), Path("/usr/bin")):
        if all((directory / name).is_file() for name in ("ffmpeg", "ffprobe")):
            return str(directory / "ffmpeg"), str(directory / "ffprobe")
    raise RuntimeError("Штатные FFmpeg и FFprobe не найдены")


def create_media(ffmpeg, ffprobe, target, uid, gid):
    subprocess.run([ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                    "-t", "12", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                    "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-b:a", "128k",
                    "-af", "volume=0.1", "-movflags", "+faststart", str(target)],
                   check=True, capture_output=True, timeout=60)
    details = json.loads(subprocess.run([ffprobe, "-v", "error", "-show_streams", "-show_format",
                                        "-of", "json", str(target)], check=True,
                                       capture_output=True, text=True, timeout=15).stdout)
    video = next(s for s in details["streams"] if s["codec_type"] == "video")
    audio = next(s for s in details["streams"] if s["codec_type"] == "audio")
    if video["codec_name"] != "h264" or audio["codec_name"] != "aac" or target.stat().st_size < 65536:
        raise RuntimeError("Тестовый MP4 не прошёл проверку H.264/AAC и размера")
    os.chown(target, uid, gid)
    stamp = datetime.now(timezone.utc).isoformat()
    numerator, denominator = video["avg_frame_rate"].split("/")
    return {"schemaVersion": 1, "kinopoiskId": KP_ID, "path": "",
            "contentLength": target.stat().st_size, "createdUtc": stamp, "lastAccessUtc": stamp,
            "container": "mp4", "videoCodec": "h264", "videoProfile": video.get("profile", ""),
            "videoLevel": video.get("level"), "videoBitRate": int(video.get("bit_rate", 0)),
            "width": video["width"], "height": video["height"],
            "frameRate": int(numerator) / int(denominator), "videoBitDepth": 8,
            "pixelFormat": video["pix_fmt"], "audioCodec": "aac",
            "audioBitRate": int(audio.get("bit_rate", 0)), "audioChannels": audio["channels"],
            "audioSampleRate": int(audio["sample_rate"]),
            "runTimeTicks": round(float(details["format"]["duration"]) * 10000000),
            "totalBitRate": int(details["format"]["bit_rate"])}


def find_library(api, state):
    folders = api.request("/Library/VirtualFolders")
    matches = [folder for folder in folders if inventory.field(folder, "Name") == LIBRARY]
    if len(matches) != 1 or inventory.field(matches[0], "Locations") != [state["media_dir"]]:
        raise RuntimeError("Тестовая библиотека не совпала с созданным каталогом")
    library_id = inventory.identifier(inventory.field(matches[0], "ItemId"))
    if state.get("library_id") and state["library_id"] != library_id:
        raise RuntimeError("ID тестовой библиотеки изменился")
    api.owned_ids.add(library_id)
    return library_id


def movie_list(api, state, user_id):
    reply = api.request(query("/Items", UserId=user_id, ParentId=state["library_id"], Recursive="true",
                              IncludeItemTypes="Movie", Fields=inventory.FIELDS, Limit=10))
    return inventory.field(reply, "Items", [])


def wait_movie(api, state, user_id, expected_overview=None):
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        movies = movie_list(api, state, user_id)
        if len(movies) == 1:
            movie = movies[0]
            providers = inventory.field(movie, "ProviderIds", {})
            if str(inventory.field(providers, "kinopoisk")) != str(KP_ID):
                raise RuntimeError("Тестовый Kinopoisk ID не был прочитан из NFO")
            movie_id = inventory.identifier(inventory.field(movie, "Id"))
            if state.get("movie_id") and state["movie_id"] != movie_id:
                raise RuntimeError("ID тестового фильма изменился")
            if expected_overview is None or inventory.field(movie, "Overview") == expected_overview:
                api.owned_ids.add(movie_id)
                return movie
        elif len(movies) > 1:
            raise RuntimeError("В тестовой библиотеке обнаружено несколько фильмов")
        time.sleep(1)
    raise RuntimeError("Обновление тестового фильма не завершилось за 120 секунд")


def prepare(api, context, user_id):
    root = state_root(context)
    if root.exists():
        state = read_state(context)
        if not state.get("movie_id"):
            raise RuntimeError("Подготовка теста не завершена. Перед повторным run требуется cleanup")
        state["library_id"] = find_library(api, state)
        return state
    capabilities = api.request("/KinopoiskPlayback/capabilities")
    if inventory.field(inventory.field(capabilities, "NativeTrailerBridge", {}), "LocalTrailerCache") is not True:
        raise RuntimeError("Серверный кэш трейлеров должен быть включён для этой проверки")
    if any(inventory.field(folder, "Name") == LIBRARY for folder in api.request("/Library/VirtualFolders")):
        raise RuntimeError("Библиотека с именем теста уже существует")
    before = inventory.library_items(api, user_id)
    if any(str(inventory.field(inventory.field(item, "ProviderIds", {}), "kinopoisk")) == str(KP_ID) for item in before):
        raise RuntimeError("Служебный Kinopoisk ID уже занят в библиотеке")
    cache = Path(context["data"]) / "plugins/Jellyfin.Plugin.Kinopoisk/cache/trailers"
    if not cache.is_dir() or cache.is_symlink():
        raise RuntimeError("Обычный каталог кэша трейлеров не найден")
    paths = [cache / f"kinopoisk-{KP_ID}{suffix}" for suffix in (".mp4", ".json")]
    if any(path.exists() or path.is_symlink() for path in paths):
        raise RuntimeError("Служебный Kinopoisk ID уже занят в кэше")
    ffmpeg, ffprobe = encoder_pair()
    root.mkdir(mode=0o755)
    media = root / "media"
    movie_folder = media / "fixture"
    movie_folder.mkdir(parents=True, mode=0o755)
    for directory in (media, movie_folder):
        os.chown(directory, context["uid"], context["gid"])
    state = {"schema": 1, "library": LIBRARY, "kp_id": KP_ID, "cache_dir": str(cache),
             "media_dir": str(media), "baseline": digest_items(before), "phase": "preparing"}
    write_state(root, state)
    movie_file = movie_folder / "offline-kinopoisk.mp4"
    metadata = create_media(ffmpeg, ffprobe, movie_file, context["uid"], context["gid"])
    nfo = movie_file.with_suffix(".nfo")
    nfo.write_text(f'<?xml version="1.0" encoding="utf-8"?><movie><title>{TITLE}</title>'
                   f'<uniqueid type="kinopoisk" default="true">{KP_ID}</uniqueid><year>2026</year>'
                   '<premiered>2026-01-01</premiered><genre>Тестирование</genre>'
                   '<plot>Локальный тестовый ролик. Метаданные КиноПоиска не загружались.</plot></movie>', encoding="utf-8")
    os.chown(nfo, context["uid"], context["gid"])
    with paths[0].open("xb") as destination, movie_file.open("rb") as source:
        shutil.copyfileobj(source, destination)
    metadata["path"] = str(paths[0])
    with paths[1].open("x", encoding="utf-8") as stream:
        json.dump(metadata, stream)
    for path in paths:
        os.chown(path, context["uid"], context["gid"])
        path.chmod(0o644)
    state["cache_sha256"] = deploy.digest(paths[0])
    write_state(root, state)
    api.request(query("/Library/VirtualFolders", name=LIBRARY, collectionType="movies", refreshLibrary="false"),
                "POST", {"LibraryOptions": {
                    "PathInfos": [{"Path": str(media)}], "EnableRealtimeMonitor": False,
                    "SaveLocalMetadata": False, "EnableChapterImageExtraction": False,
                    "EnableTrickplayImageExtraction": False, "EnableInternetProviders": False,
                    "AutomaticRefreshIntervalDays": 0, "PreferredMetadataLanguage": "ru", "MetadataCountryCode": "RU",
                    "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [], "ImageFetchers": [], "ImageOptions": []}]}})
    state["library_id"] = find_library(api, state)
    write_state(root, state)
    api.request(query(f"/Items/{state['library_id']}/Refresh", metadataRefreshMode="Default", imageRefreshMode="None"), "POST")
    movie = wait_movie(api, state, user_id)
    state["movie_id"] = inventory.identifier(inventory.field(movie, "Id"))
    state["phase"] = "prepared"
    write_state(root, state)
    return state


def verify(api, context, state, user_id):
    api.owned_ids.update(state.get(key) for key in ("library_id", "movie_id", "trailer_id") if state.get(key))
    movie = api.request(query(f"/Items/{state['movie_id']}", UserId=user_id))
    if state.get("refresh_verified") and inventory.field(movie, "Overview") != state.get("refresh_marker"):
        raise RuntimeError("Обновлённое описание тестового фильма не сохранилось")
    status = api.request(query(f"/KinopoiskPlayback/native-bridge/{KP_ID}", movieId=state["movie_id"]))
    if (inventory.field(status, "State") != "ready-local-cache" or inventory.field(status, "CacheReady") is not True
            or inventory.identifier(inventory.field(status, "MovieId")) != state["movie_id"]):
        raise RuntimeError("Нативный трейлер не был зарегистрирован из готового кэша")
    trailer_id = inventory.identifier(inventory.field(status, "TrailerId"))
    if state.get("trailer_id") and state["trailer_id"] != trailer_id:
        raise RuntimeError("ID нативного трейлера изменился")
    api.owned_ids.add(trailer_id)
    trailers = api.request(query(f"/Items/{state['movie_id']}/LocalTrailers", UserId=user_id))
    if len(trailers) != 1 or inventory.identifier(inventory.field(trailers[0], "Id")) != trailer_id:
        raise RuntimeError("Штатный список LocalTrailers не совпал с зарегистрированным трейлером")
    playback = api.request(f"/Items/{trailer_id}/PlaybackInfo", "POST", {
        "UserId": user_id, "MediaSourceId": trailer_id, "EnableDirectPlay": True,
        "EnableDirectStream": True, "EnableTranscoding": False, "AutoOpenLiveStream": False,
        "DeviceProfile": {"Name": "Offline H264 AAC MP4", "MaxStreamingBitrate": 20000000,
                          "MaxStaticBitrate": 20000000,
                          "DirectPlayProfiles": [{"Type": "Video", "Container": "mp4", "VideoCodec": "h264", "AudioCodec": "aac"}],
                          "TranscodingProfiles": []}})
    sources = [source for source in inventory.field(playback, "MediaSources", [])
               if inventory.identifier(inventory.field(source, "Id")) == trailer_id]
    if len(sources) != 1 or inventory.field(sources[0], "SupportsDirectPlay") is not True:
        raise RuntimeError("PlaybackInfo не подтвердил Direct Play тестового MP4")
    streams = inventory.field(sources[0], "MediaStreams", [])
    if not any(inventory.field(s, "Type") == "Video" and inventory.field(s, "Codec") == "h264" for s in streams):
        raise RuntimeError("В PlaybackInfo отсутствует H.264")
    if not any(inventory.field(s, "Type") == "Audio" and inventory.field(s, "Codec") == "aac" for s in streams):
        raise RuntimeError("В PlaybackInfo отсутствует AAC")
    cache_file = Path(state["cache_dir"]) / f"kinopoisk-{KP_ID}.mp4"
    if deploy.digest(cache_file) != state["cache_sha256"]:
        raise RuntimeError("Тестовый MP4-кэш изменился")
    size = cache_file.stat().st_size
    for start in (0, size // 2):
        end = min(start + 65535, size - 1)
        body, content_range = api.read_range(trailer_id, start, end)
        with cache_file.open("rb") as stream:
            stream.seek(start)
            expected = stream.read(end - start + 1)
        if body != expected or content_range != f"bytes {start}-{end}/{size}":
            raise RuntimeError("HTTP Range не совпал с байтами тестового MP4")
    after = digest_items(inventory.library_items(api, user_id))
    if any(after.get(item_id) != value for item_id, value in state["baseline"].items()):
        raise RuntimeError("Состояние исходных объектов библиотеки изменилось во время теста")
    state["trailer_id"] = trailer_id
    state["phase"] = "verified"
    write_state(state_root(context), state)
    return {"registration": "PASS", "local_trailers": "PASS", "playback_info": "PASS",
            "direct_play_profile": "PASS", "video_audio": "H264/AAC", "http_range": "PASS",
            "existing_items": "UNCHANGED", "movie_id": state["movie_id"], "trailer_id": trailer_id}


def refresh_fixture(api, context, state, user_id):
    movie = wait_movie(api, state, user_id)
    if inventory.identifier(inventory.field(movie, "Id")) != state["movie_id"]:
        raise RuntimeError("Обновление было ограничено сохранённым ID тестового фильма")
    root = state_root(context)
    media = root / "media"
    folder = media / "fixture"
    nfo = folder / "offline-kinopoisk.nfo"
    if (state.get("media_dir") != str(media) or any(p.is_symlink() for p in (root, media, folder, nfo))
            or not nfo.is_file()):
        raise RuntimeError("NFO не совпал с созданным тестовым фильмом")
    document = ET.parse(nfo)
    xml = document.getroot()
    ids = xml.findall("uniqueid")
    plot = xml.find("plot")
    if (xml.tag != "movie" or len(ids) != 1 or ids[0].get("type") != "kinopoisk"
            or ids[0].text != str(KP_ID) or plot is None):
        raise RuntimeError("NFO не подтвердил служебный ID и описание тестового фильма")
    # Публичный DTO Jellyfin не содержал DateLastRefreshed; подтверждён новый результат чтения NFO.
    marker = "Локальная автономная проверка обновления: " + uuid.uuid4().hex
    plot.text = marker
    temporary = nfo.with_suffix(".nfo.tmp")
    with temporary.open("x", encoding="utf-8") as stream:
        os.fchmod(stream.fileno(), 0o644)
        os.fchown(stream.fileno(), context["uid"], context["gid"])
        document.write(stream, encoding="unicode", xml_declaration=True)
    temporary.replace(nfo)
    state["refresh_marker"] = marker
    state["refresh_verified"] = False
    state["phase"] = "refresh_prepared"
    write_state(root, state)
    api.request(query(f"/Items/{state['movie_id']}/Refresh", metadataRefreshMode="FullRefresh",
                      imageRefreshMode="None", replaceAllMetadata="true"), "POST")
    state["phase"] = "refresh_queued"
    write_state(root, state)
    wait_movie(api, state, user_id, expected_overview=marker)
    state["refresh_verified"] = True
    verify(api, context, state, user_id)
    state["phase"] = "refresh_verified"
    write_state(root, state)
    return "PASS"


def cleanup(api, context, state):
    root = state_root(context)
    cache_file = Path(state["cache_dir"]) / f"kinopoisk-{KP_ID}.mp4"
    metadata = cache_file.with_suffix(".json")
    if cache_file.exists() and state.get("cache_sha256") and deploy.digest(cache_file) != state["cache_sha256"]:
        raise RuntimeError("Тестовый файл был изменён; автоматическое удаление остановлено")
    for path in (cache_file, metadata):
        if path.is_symlink():
            raise RuntimeError("Тестовый файл заменён символической ссылкой")
    folders = [f for f in api.request("/Library/VirtualFolders") if inventory.field(f, "Name") == LIBRARY]
    if folders:
        find_library(api, state)
        api.request(query("/Library/VirtualFolders", name=LIBRARY, refreshLibrary="false"), "DELETE")
    for path in (cache_file, metadata):
        if path.exists():
            path.unlink()
    shutil.rmtree(root)
    return {"cleanup": "PASS", "functional_acceptance": "PENDING"}


def main():
    parser = argparse.ArgumentParser(description="Подготовлена автономная проверка NativeTrailerBridge")
    parser.add_argument("action", choices=("run", "check", "cleanup"))
    parser.add_argument("--restart", action="store_true", help="Перезапуск Jellyfin с повторной проверкой")
    args = parser.parse_args()
    try:
        if not sys.stdin.isatty():
            raise RuntimeError("Команда запускается в интерактивной консоли контейнера")
        context = deploy.discover()
        manifest, _ = deploy.load_bundle()
        deploy.check(context, manifest)
        api = FixtureApi(context["url"])
        username = input("Имя пользователя Jellyfin: ").strip()
        password = getpass.getpass("Пароль Jellyfin: ")
        with authenticated(api, username, password) as user_id:
            if args.action == "cleanup":
                print(json.dumps(cleanup(api, context, read_state(context)), ensure_ascii=False, indent=2))
                return 0
            state = prepare(api, context, user_id) if args.action == "run" else read_state(context)
            state["library_id"] = find_library(api, state)
            result = verify(api, context, state, user_id)
            if args.action == "run":
                result["targeted_refresh"] = refresh_fixture(api, context, state, user_id)
        if args.restart:
            print("Проверены начальные условия; выполняется перезапуск Jellyfin.", flush=True)
            deploy.service("restart")
            deploy.wait_ready(context["url"])
            deploy.check(context, manifest)
            api = FixtureApi(context["url"])
            with authenticated(api, username, password) as user_id:
                state = read_state(context)
                state["library_id"] = find_library(api, state)
                verify(api, context, state, user_id)
            result["restart_persistence"] = "PASS"
        else:
            result["restart_persistence"] = "PENDING"
        result.update({"offline_native": "PASS", "fixture_library": LIBRARY, "fixture_kinopoisk_id": KP_ID,
                       "client_playback": "PENDING", "functional_acceptance": "PENDING"})
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except Exception as error:
        detail = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        print("ОШИБКА: " + detail, file=sys.stderr)
        if "context" in locals() and state_root(context).exists():
            print("Созданное состояние теста сохранено для проверки или команды cleanup.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
