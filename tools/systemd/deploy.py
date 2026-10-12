#!/usr/bin/env python3
"""Установка кандидата КиноПоиска 12.1.0.3 в работающую службу Jellyfin 12.1."""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

VERSION = "12.1.0.3"
GUID = "33e6d249-648f-44cd-a9ce-497be06c08df"
COMMIT = "1398da5fa4abd15351649162c48271c56140e577"
DLLS = {"Jellyfin.Plugin.Kinopoisk.dll", "KinopoiskUnofficialInfo.ApiClient.dll"}
SERVICE = "jellyfin.service"
HERE = Path(__file__).resolve().parent
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def run(*args, timeout=120):
    return subprocess.run(args, check=True, text=True, capture_output=True,
                          timeout=timeout).stdout.strip()


def service(action):
    return run("systemctl", action, SERVICE)


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def write_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")
    temporary.chmod(0o600)
    temporary.replace(path)


def request(url, headers=None, data=None, method=None):
    req = urllib.request.Request(url, headers=headers or {}, data=data, method=method)
    try:
        response = HTTP.open(req, timeout=5)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        body = response.read(8 * 1024 * 1024 + 1)
        if len(body) > 8 * 1024 * 1024:
            raise RuntimeError("HTTP-ответ превысил ожидаемый размер")
        return response.status, response.headers, body


def server_info(url):
    status, _, body = request(url + "/System/Info/Public")
    if status != 200:
        raise RuntimeError(f"Jellyfin не ответил: HTTP {status}")
    info = json.loads(body)
    if not re.fullmatch(r"12\.1(?:\.\d+){0,2}(?:[+\-].*)?", info.get("Version", "")):
        raise RuntimeError("Установщик рассчитан на Jellyfin 12.1; получена другая версия")
    return info


def wait_ready(url):
    deadline = time.monotonic() + 120
    observed = {}
    while time.monotonic() < deadline:
        try:
            observed["service"] = service("is-active")
            if observed["service"] == "active":
                status, _, body = request(url + "/health")
                observed["health_http"] = status
                text = body.strip().decode("ascii", errors="replace")
                observed["health"] = text if text in {"Healthy", "Degraded", "Unhealthy"} else "OTHER"
                info = server_info(url)
                if status == 200 and body.strip() == b"Healthy":
                    return info
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            observed["last_error"] = type(error).__name__
        time.sleep(2)
    raise RuntimeError("Jellyfin не подтвердил готовность за 120 секунд; " +
                       json.dumps(observed, ensure_ascii=False, sort_keys=True))


def option(arguments, names):
    for index, value in enumerate(arguments):
        if value in names and index + 1 < len(arguments):
            return arguments[index + 1]
        for name in names:
            if value.startswith(name + "="):
                return value[len(name) + 1:]
    return None


def checked_directory(value):
    path = Path(value)
    if not path.is_absolute() or len(path.parts) < 3 or path.is_symlink():
        raise RuntimeError(f"Неоднозначный каталог службы: {path}")
    path = path.resolve(strict=True)
    if not path.is_dir() or path.is_mount():
        raise RuntimeError(f"Каталог нельзя заменить при откате: {path}")
    return path


def roots_for(data, config):
    paths = sorted({data, config}, key=lambda p: (len(p.parts), str(p)))
    return [p for p in paths if not any(other != p and other in p.parents for other in paths)]


def discover():
    if os.geteuid() != 0:
        raise RuntimeError("Команда должна быть запущена от root внутри LXC Jellyfin")
    if service("is-active") != "active":
        raise RuntimeError("Служба Jellyfin должна быть запущена перед проверкой")
    pid = int(run("systemctl", "show", SERVICE, "--property=MainPID", "--value"))
    process = Path("/proc") / str(pid)
    arguments = process.joinpath("cmdline").read_bytes().decode().strip("\0").split("\0")
    environment = dict(value.split("=", 1) for value in
                       process.joinpath("environ").read_bytes().decode().split("\0")
                       if "=" in value)
    paths = {}
    for name, flags in (("data", ("--datadir", "-d")),
                        ("config", ("--configdir", "-c")),
                        ("web", ("--webdir", "-w"))):
        value = option(arguments, flags) or environment.get(f"JELLYFIN_{name.upper()}_DIR")
        if not value:
            raise RuntimeError(f"Каталог {name} отсутствует в аргументах/окружении службы")
        if name == "web":
            paths[name] = Path(value).resolve(strict=True)
            if not paths[name].is_dir():
                raise RuntimeError("Каталог Jellyfin Web не найден")
        else:
            paths[name] = checked_directory(value)
    uid = int(next(line.split()[1] for line in process.joinpath("status").read_text().splitlines()
                   if line.startswith("Uid:")))
    gid = int(next(line.split()[1] for line in process.joinpath("status").read_text().splitlines()
                   if line.startswith("Gid:")))
    network = paths["config"] / "network.xml"
    xml = ET.parse(network).getroot() if network.exists() else ET.Element("NetworkConfiguration")
    port = int(xml.findtext("InternalHttpPort") or xml.findtext("HttpServerPortNumber") or "8096")
    base = (xml.findtext("BaseUrl") or "").strip().strip("/")
    if not 1 <= port <= 65535 or any(value in base for value in ("?", "#", ":", "\\")):
        raise RuntimeError("Сетевые параметры Jellyfin не прошли проверку")
    url = f"http://127.0.0.1:{port}" + ("/" + base if base else "")
    info = wait_ready(url)
    index = paths["web"] / "index.html"
    if not index.is_file() or index.is_symlink():
        raise RuntimeError("Обычный файл Jellyfin Web index.html не найден")
    config = paths["data"] / "plugins/configurations/Jellyfin.Plugin.Kinopoisk.xml"
    bootstrap, token_set = True, False
    if config.exists():
        plugin_xml = ET.parse(config).getroot()
        bootstrap = (plugin_xml.findtext("EnableWebBootstrap") or "true").lower() != "false"
        token_set = bool((plugin_xml.findtext("ApiToken") or "").strip())
    return {**{key: str(value) for key, value in paths.items()},
            "uid": uid, "gid": gid, "url": url, "server_version": info["Version"],
            "index_sha256": digest(index), "index_uid": index.stat().st_uid,
            "index_gid": index.stat().st_gid, "bootstrap_enabled": bootstrap,
            "api_token_configured": token_set}


def load_bundle(directory=HERE):
    manifest = json.loads((directory / "manifest.json").read_text())
    if (manifest.get("version"), manifest.get("guid"), manifest.get("commit")) != (VERSION, GUID, COMMIT):
        raise RuntimeError("Манифест не соответствует проверенному кандидату")
    archive_path = directory / "plugin.zip"
    if digest(archive_path) != manifest["archive_sha256"]:
        raise RuntimeError("Контрольная сумма архива плагина не совпала")
    payload = {}
    with zipfile.ZipFile(archive_path) as archive:
        if len(archive.infolist()) != 2 or set(archive.namelist()) != DLLS:
            raise RuntimeError("В архиве должны находиться ровно две DLL без вложенных путей")
        for item in archive.infolist():
            if stat.S_ISLNK(item.external_attr >> 16) or item.file_size > 16 * 1024 * 1024:
                raise RuntimeError("Недопустимый файл в архиве плагина")
            body = archive.read(item)
            if hashlib.sha256(body).hexdigest() != manifest["dll_sha256"][item.filename]:
                raise RuntimeError("Контрольная сумма DLL не совпала")
            payload[item.filename] = body
    return manifest, payload


def package_files(context):
    plugins = Path(context["data"]) / "plugins"
    if plugins.is_symlink():
        raise RuntimeError("Каталог плагинов является символической ссылкой")
    result = []
    for dll in sorted(list(plugins.glob("Jellyfin.Plugin.Kinopoisk.dll")) +
                      list(plugins.glob("*/Jellyfin.Plugin.Kinopoisk.dll"))):
        folder = dll.parent
        if folder.is_symlink() or dll.is_symlink():
            raise RuntimeError("Пакет КиноПоиска содержит символическую ссылку")
        foreign = {p.name for p in folder.glob("*.dll")} - DLLS
        if folder != plugins and foreign:
            raise RuntimeError("Пакет КиноПоиска содержит посторонние DLL")
        names = set(DLLS)
        for dll_name in DLLS:
            names.update({dll_name[:-4] + ".pdb", dll_name[:-4] + ".deps.json"})
        metadata = folder / "meta.json"
        if metadata.exists() and folder != plugins:
            if json.loads(metadata.read_text()).get("guid", "").lower() != GUID:
                raise RuntimeError("Манифест прежнего пакета принадлежит другому плагину")
            names.add("meta.json")
        for name in sorted(names):
            path = folder / name
            if path.exists():
                if path.is_symlink() or not path.is_file():
                    raise RuntimeError("Файл прежнего пакета не прошёл проверку")
                result.append(path)
    target = plugins / ("КиноПоиск_" + VERSION)
    if target.exists() and (target.is_symlink() or not (target / "Jellyfin.Plugin.Kinopoisk.dll").is_file()
                            or any(p not in result for p in target.iterdir())):
        raise RuntimeError("Целевой каталог уже содержит посторонние файлы")
    return result, target


def retired_package_folders(context, previous):
    plugins = Path(context["data"]) / "plugins"
    canonical = plugins / "Jellyfin.Plugin.Kinopoisk"
    known = {path.parent for path in previous} - {plugins, canonical}
    for folder in plugins.iterdir():
        if not re.fullmatch(r"КиноПоиск_\d+(?:\.\d+){1,3}", folder.name):
            continue
        if folder.is_symlink() or not folder.is_dir():
            raise RuntimeError("Каталог прежней версии не прошёл проверку")
        if folder not in known and any(folder.iterdir()):
            metadata = folder / "meta.json"
            if (not metadata.is_file() or metadata.is_symlink()
                    or json.loads(metadata.read_text()).get("guid", "").lower() != GUID):
                raise RuntimeError("Оставшийся каталог версии не был подтверждён как пакет КиноПоиска")
        if {path.name for path in folder.glob("*.dll")} - DLLS:
            raise RuntimeError("Каталог прежней версии содержит посторонние DLL")
        known.add(folder)
    return sorted(known)


def inspect_packages(data):
    plugins = Path(data) / "plugins"
    result = []
    if not plugins.is_dir():
        return result
    for folder in [plugins, *sorted(plugins.iterdir())]:
        if not folder.is_dir() or folder.is_symlink():
            continue
        if (not folder.name.startswith("КиноПоиск_")
                and folder.name != "Jellyfin.Plugin.Kinopoisk"
                and not (folder / "Jellyfin.Plugin.Kinopoisk.dll").is_file()):
            continue
        metadata = {}
        path = folder / "meta.json"
        if path.is_file() and not path.is_symlink():
            try:
                content = json.loads(path.read_text())
                metadata = {key: content.get(key) for key in ("guid", "name", "version", "status")}
            except (OSError, ValueError):
                metadata = {"error": "INVALID_MANIFEST"}
        result.append({"folder": "." if folder == plugins else folder.name,
                       "files": sorted(p.name for p in folder.iterdir()),
                       "meta": metadata, "dll_sha256": {
                           name: digest(folder / name) if (folder / name).is_file() and not (folder / name).is_symlink()
                           else None for name in sorted(DLLS)}})
    return result


def diagnose(context, manifest, backup=None):
    result = {"diagnostic": "PASS", "expected_version": VERSION,
              "expected_dll_sha256": manifest["dll_sha256"], "current_packages": inspect_packages(context["data"])}
    if backup:
        state = json.loads((Path(backup) / "state.json").read_text())
        if state.get("format") != 1 or state.get("context", {}).get("data") != context["data"]:
            raise RuntimeError("Резервная копия принадлежит другому экземпляру Jellyfin")
        data = Path(context["data"])
        result["backup_phase"] = state.get("phase")
        result["failed_installation"] = []
        for name in state.get("saved_test_state", []):
            saved = Path(name)
            if saved.parent == data.parent and saved.name.startswith(data.name + ".kinopoisk-before-rollback-"):
                result["failed_installation"].append({"data": str(saved), "packages": inspect_packages(saved)})
    return result


def verify_dlls(target, manifest):
    failures = []
    for name, expected in manifest["dll_sha256"].items():
        path = target / name
        actual = digest(path) if path.is_file() and not path.is_symlink() else None
        if actual != expected:
            failures.append({"file": name, "exists": path.is_file(), "expected_sha256": expected,
                             "actual_sha256": actual})
    if failures:
        raise RuntimeError("Установленные DLL не совпали с кандидатом; " +
                           json.dumps({"target": str(target), "files": failures}, ensure_ascii=False))


def check(context, manifest):
    info = wait_ready(context["url"])
    _, target = package_files(context)
    verify_dlls(target, manifest)
    metadata = json.loads((target / "meta.json").read_text())
    if (metadata.get("status") != "Active" or metadata.get("version") != VERSION
            or metadata.get("guid", "").lower() != GUID):
        raise RuntimeError("Jellyfin не отметил кандидат как активный плагин")
    if digest(Path(context["web"]) / "index.html") != context["index_sha256"]:
        raise RuntimeError("Файл index.html на диске изменился после установки")
    status, headers, body = request(context["url"] + "/Kinopoisk/WebClient.js")
    if status != 200 or hashlib.sha256(body).hexdigest() != manifest["webclient_sha256"]:
        raise RuntimeError("Веб-клиент кандидата не был получен из Jellyfin")
    if "javascript" not in headers.get("Content-Type", ""):
        raise RuntimeError("Веб-клиент вернул неожиданный Content-Type")
    status, headers, html = request(context["url"] + "/web/index.html")
    if status != 200:
        raise RuntimeError("Jellyfin Web не ответил после установки")
    if context["bootstrap_enabled"]:
        if headers.get("X-Kinopoisk-Web-Bootstrap") != "runtime" or \
                html.count(b'data-kinopoisk-managed="runtime"') != 1 or \
                html.count(b"../Kinopoisk/WebClient.js?v=") != 1:
            raise RuntimeError("Подключение веб-клиента в HTTP-ответ не было подтверждено")
    status, _, _ = request(context["url"] + "/KinopoiskPlayback/capabilities")
    if status != 401:
        raise RuntimeError(f"Защищённый маршрут capabilities вернул HTTP {status} вместо 401")
    return {"startup": "PASS", "jellyfin": info["Version"], "plugin": VERSION,
            "webclient": "PASS", "disk_index": "UNCHANGED",
            "bootstrap": "PASS" if context["bootstrap_enabled"] else "DISABLED_IN_CONFIGURATION",
            "anonymous_capabilities": "401", "api_token": "CONFIGURED" if context["api_token_configured"] else "MISSING",
            "functional_acceptance": "PENDING"}


def backup_size(roots):
    return sum(int(run("du", "-sb", "--", str(root)).split()[0]) for root in roots)


def create_backup(context, root):
    roots = roots_for(Path(context["data"]), Path(context["config"]))
    for line in Path("/proc/self/mountinfo").read_text().splitlines():
        name = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m[1], 8)), line.split()[4])
        mount = Path(name)
        if any(mount == p or p in mount.parents for p in roots):
            raise RuntimeError("В каталогах Jellyfin найдена точка монтирования; служба не была остановлена")
    root = Path(root).resolve()
    if any(root == p or p in root.parents for p in roots):
        raise RuntimeError("Резервная копия должна находиться вне каталогов Jellyfin")
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    needed = backup_size(roots) * 2 + 64 * 1024 * 1024
    for path in [root, *roots]:
        if shutil.disk_usage(path).free < needed:
            raise RuntimeError("Недостаточно места для резервной копии и восстановления")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    backup = root / stamp
    backup.mkdir(mode=0o700)
    shutil.copy2(__file__, backup / "deploy.py")
    shutil.copy2(Path(context["web"]) / "index.html", backup / "index.html")
    state = {"format": 1, "context": context, "roots": [str(p) for p in roots], "phase": "prepared"}
    write_json(backup / "state.json", state)
    return backup, state


def snapshot(backup, state):
    run("tar", "--acls", "--xattrs", "--numeric-owner", "--one-file-system",
        "-cpf", str(backup / "server-state.tar"), "-C", "/", "--",
        *[p.lstrip("/") for p in state["roots"]], timeout=1800)
    state["archive_sha256"] = digest(backup / "server-state.tar")
    state["phase"] = "backed_up"
    write_json(backup / "state.json", state)


def rollback(backup):
    backup = Path(backup).resolve(strict=True)
    state = json.loads((backup / "state.json").read_text())
    if state.get("format") != 1 or state.get("phase") not in {"backed_up", "installed", "verified", "rollback_failed"}:
        raise RuntimeError("Резервная копия не готова к восстановлению или уже была использована")
    if digest(backup / "server-state.tar") != state["archive_sha256"]:
        raise RuntimeError("Контрольная сумма резервной копии не совпала")
    context = state["context"]
    roots = roots_for(checked_directory(context["data"]), checked_directory(context["config"]))
    if [str(p) for p in roots] != state["roots"]:
        raise RuntimeError("Каталоги восстановления не совпали с резервной копией")
    for path in roots:
        if shutil.disk_usage(path).free < (backup / "server-state.tar").stat().st_size + 32 * 1024 * 1024:
            raise RuntimeError("Недостаточно места для восстановления; служба не была остановлена")
    moved = []
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    service("stop")
    try:
        for path in roots:
            destination = path.with_name(path.name + ".kinopoisk-before-rollback-" + stamp)
            path.rename(destination)
            moved.append((path, destination))
        run("tar", "--acls", "--xattrs", "--same-owner", "--same-permissions",
            "-xpf", str(backup / "server-state.tar"), "-C", "/", timeout=1800)
        index = Path(context["web"]) / "index.html"
        if digest(index) != context["index_sha256"]:
            shutil.copy2(backup / "index.html", index)
            os.chown(index, context["index_uid"], context["index_gid"])
        service("start")
        wait_ready(context["url"])
        state["phase"] = "rolled_back"
        state["saved_test_state"] = [str(p) for _, p in moved]
        write_json(backup / "state.json", state)
        return {"rollback": "PASS", "backup": str(backup), "saved_test_state": state["saved_test_state"]}
    except BaseException:
        # При неудаче восстановления сохранённое состояние возвращено на прежние места.
        # Обрабатывались только каталоги, которые уже были перемещены этой операцией.
        try:
            service("stop")
            for original, saved in reversed(moved):
                if original.exists():
                    shutil.rmtree(original)
                saved.rename(original)
            service("start")
            moved = []
        except BaseException:
            pass
        state["phase"] = "rollback_failed"
        state["saved_test_state"] = [str(p) for _, p in moved]
        write_json(backup / "state.json", state)
        raise


def install(context, manifest, payload, backup_root):
    previous, target = package_files(context)
    retired = retired_package_folders(context, previous)
    backup, state = create_backup(context, backup_root)
    print("BACKUP=" + str(backup), flush=True)
    print(f"ROLLBACK_COMMAND=python3 {backup}/deploy.py rollback --backup {backup}", flush=True)
    stopped, changed = False, False
    try:
        stopped = True
        service("stop")
        snapshot(backup, state)
        changed = True
        retired_root = backup / "retired-plugin-folders"
        for folder in retired:
            if folder.exists():
                retired_root.mkdir(exist_ok=True, mode=0o700)
                shutil.move(str(folder), str(retired_root / folder.name))
        state["retired_plugin_folders"] = [str(retired_root / folder.name) for folder in retired]
        # Полные прежние пакеты были сохранены вне дерева обнаружения Jellyfin.
        # Из корневого и постоянного каталогов удалены только бинарные файлы и meta.json.
        for path in previous:
            if path.exists():
                path.unlink()
        if target.exists():
            target.rmdir()
        target.mkdir(parents=True, mode=0o755)
        os.chown(target, context["uid"], context["gid"])
        for name, body in payload.items():
            path = target / name
            path.write_bytes(body)
            path.chmod(0o644)
            os.chown(path, context["uid"], context["gid"])
        meta = {"category": "Metadata", "guid": GUID, "name": "КиноПоиск",
                "owner": "Jellyfin.Plugin.Kinopoisk", "targetAbi": "12.1.0.0",
                "version": VERSION, "status": "Active", "autoUpdate": False,
                "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "description": "Кандидат переноса на Jellyfin 12.1; функциональная приёмка продолжена."}
        write_json(target / "meta.json", meta)
        (target / "meta.json").chmod(0o644)
        os.chown(target / "meta.json", context["uid"], context["gid"])
        verify_dlls(target, manifest)
        state["phase"] = "installed"
        write_json(backup / "state.json", state)
        service("start")
        result = check(context, manifest)
        state["phase"] = "verified"
        state["startup_result"] = result
        write_json(backup / "state.json", state)
        return {**result, "backup": str(backup)}
    except BaseException as failure:
        if changed:
            try:
                state["failure_type"] = type(failure).__name__
                state["packages_before_rollback"] = inspect_packages(context["data"])
                write_json(backup / "state.json", state)
            except Exception as diagnostic_error:
                print("Диагностика сбоя не была сохранена: " + type(diagnostic_error).__name__,
                      file=sys.stderr, flush=True)
            print("Установка не прошла проверку; начат автоматический откат.", file=sys.stderr, flush=True)
            try:
                print(json.dumps(rollback(backup), ensure_ascii=False), flush=True)
            except BaseException as error:
                print(f"АВТОМАТИЧЕСКИЙ ОТКАТ НЕ ЗАВЕРШЁН: {type(error).__name__}; BACKUP={backup}", file=sys.stderr)
        elif stopped:
            service("start")
            wait_ready(context["url"])
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("plan", "install", "check", "diagnose", "rollback"))
    parser.add_argument("--backup-root", default="/var/backups/jellyfin-kinopoisk")
    parser.add_argument("--backup", help="Каталог резервной копии для отката")
    args = parser.parse_args()
    os.umask(0o077)
    try:
        if args.mode == "rollback":
            if os.geteuid() != 0 or not args.backup:
                raise RuntimeError("Для отката требуются root и --backup")
            result = rollback(args.backup)
        else:
            manifest, payload = load_bundle()
            context = discover()
            if args.mode in ("check", "diagnose") and args.backup:
                original = json.loads((Path(args.backup) / "state.json").read_text())["context"]
                if any(context[name] != original[name] for name in ("data", "config", "web", "url")):
                    raise RuntimeError("Резервная копия принадлежит другому экземпляру Jellyfin")
                context["index_sha256"] = original["index_sha256"]
            if args.mode == "diagnose":
                result = diagnose(context, manifest, args.backup)
            else:
                previous, target = package_files(context)
                print(json.dumps({"server": context["server_version"], "url": context["url"],
                                  "target": str(target), "previous_plugin_packages": len({p.parent for p in previous}),
                                  "source_commit": COMMIT}, ensure_ascii=False), flush=True)
            if args.mode == "plan":
                result = {"plan": "PASS", "changes": "NONE",
                          "retired_plugin_folders": [str(p) for p in retired_package_folders(context, previous)]}
            elif args.mode == "check":
                result = check(context, manifest)
            elif args.mode == "install":
                result = install(context, manifest, payload, args.backup_root)
        print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)
        return 0
    except KeyboardInterrupt:
        print("Операция прервана; результат восстановления указан выше.", file=sys.stderr)
        return 130
    except Exception as error:
        # Текст subprocess stderr и содержимое XML/HTTP-ответов не попали в отчёт.
        detail = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        print("ОШИБКА: " + detail, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
