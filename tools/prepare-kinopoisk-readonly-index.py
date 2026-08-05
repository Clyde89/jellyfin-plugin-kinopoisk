#!/usr/bin/env python3
"""Подготавливает внешний read-only index.html для автономного веб-клиента КиноПоиска."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

BEGIN_MARKER = "<!-- KINOPOISK_WEB_CLIENT_BEGIN -->"
END_MARKER = "<!-- KINOPOISK_WEB_CLIENT_END -->"
WEB_CLIENT_PATH = "../Kinopoisk/WebClient.js"
EXTERNAL_ATTRIBUTE = 'data-kinopoisk-managed="external"'
SCRIPT_ELEMENT = (
    f'    <script src="{WEB_CLIENT_PATH}" defer '
    f'{EXTERNAL_ATTRIBUTE}></script>'
)


class IndexPreparationError(RuntimeError):
    """Ошибка безопасной подготовки index.html."""


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_text(value: str) -> str:
    return sha256_bytes(value.encode("utf-8"))


def remove_managed_block(value: str) -> str:
    begin_count = value.count(BEGIN_MARKER)
    end_count = value.count(END_MARKER)

    if begin_count == 0 and end_count == 0:
        return value
    if begin_count != 1 or end_count != 1:
        raise IndexPreparationError(
            "Управляемые маркеры КиноПоиска повреждены или продублированы."
        )

    start = value.index(BEGIN_MARKER)
    end = value.index(END_MARKER)
    if end < start:
        raise IndexPreparationError(
            "Конечный маркер КиноПоиска расположен раньше начального."
        )

    end += len(END_MARKER)
    while end < len(value) and value[end] in "\r\n":
        end += 1
    return value[:start] + value[end:]


def build_external_index(value: str) -> str:
    if not value.strip():
        raise IndexPreparationError("Исходный index.html пуст.")

    clean = remove_managed_block(value)
    body_matches = list(re.finditer(r"</body\s*>", clean, flags=re.IGNORECASE))
    if not body_matches:
        raise IndexPreparationError(
            "В index.html не найден закрывающий тег body."
        )

    body_end = body_matches[-1].start()
    block = os.linesep.join((BEGIN_MARKER, SCRIPT_ELEMENT, END_MARKER))
    result = clean[:body_end] + block + os.linesep + clean[body_end:]
    validate_external_index(result)
    return result


def validate_external_index(value: str) -> None:
    checks = {
        "начальный маркер": value.count(BEGIN_MARKER),
        "конечный маркер": value.count(END_MARKER),
        "endpoint веб-клиента": value.count(WEB_CLIENT_PATH),
        "признак внешнего управления": value.count(EXTERNAL_ATTRIBUTE),
        "точный script-элемент": value.count(SCRIPT_ELEMENT.strip()),
    }
    invalid = [name for name, count in checks.items() if count != 1]
    if invalid:
        raise IndexPreparationError(
            "Некорректный внешний блок КиноПоиска: " + ", ".join(invalid) + "."
        )

    start = value.index(BEGIN_MARKER)
    end = value.index(END_MARKER, start)
    endpoint = value.index(WEB_CLIENT_PATH)
    attribute = value.index(EXTERNAL_ATTRIBUTE)
    script = value.index(SCRIPT_ELEMENT.strip())
    if not (start < endpoint < end and start < attribute < end and start < script < end):
        raise IndexPreparationError(
            "Endpoint или атрибут внешнего управления находятся вне управляемого блока."
        )

    if "?v=" in value[start:end]:
        raise IndexPreparationError(
            "Внешний read-only режим не должен фиксировать версию bundle в URL."
        )


def read_utf8(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8-sig")
    except OSError as error:
        raise IndexPreparationError(f"Не удалось прочитать {path}: {error}") from error
    except UnicodeError as error:
        raise IndexPreparationError(
            f"Файл {path} не является корректным UTF-8: {error}"
        ) from error


def write_utf8_atomically(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    file_descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary_path = Path(temporary_name)
    try:
        with os.fdopen(file_descriptor, "w", encoding="utf-8", newline="") as stream:
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary_path, 0o644)
        os.replace(temporary_path, path)
    finally:
        temporary_path.unlink(missing_ok=True)


def create_backup(source: Path, backup_directory: Path, label: str) -> Path:
    backup_directory.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    backup_path = backup_directory / f"{label}.{timestamp}.bak"
    shutil.copy2(source, backup_path)
    return backup_path


def prepare(input_path: Path, output_path: Path, backup_directory: Path) -> int:
    source = read_utf8(input_path)
    prepared = build_external_index(source)

    input_backup = create_backup(input_path, backup_directory, "index.original")
    output_backup: Path | None = None
    if output_path.exists():
        current = read_utf8(output_path)
        if current == prepared:
            validate_external_index(current)
            print(f"index.html уже актуален: {output_path}")
            print(f"SHA-256: {sha256_text(current)}")
            print(f"Резервная копия источника: {input_backup}")
            return 0
        output_backup = create_backup(output_path, backup_directory, "index.previous")

    write_utf8_atomically(output_path, prepared)
    stored = read_utf8(output_path)
    if stored != prepared:
        raise IndexPreparationError(
            "Записанный index.html не совпал с подготовленным содержимым."
        )
    validate_external_index(stored)

    manifest = {
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "input": str(input_path.resolve()),
        "output": str(output_path.resolve()),
        "inputBackup": str(input_backup.resolve()),
        "outputBackup": str(output_backup.resolve()) if output_backup else None,
        "inputSha256": sha256_text(source),
        "outputSha256": sha256_text(stored),
        "endpoint": WEB_CLIENT_PATH,
        "mode": "external-read-only",
    }
    manifest_path = backup_directory / "LATEST.json"
    write_utf8_atomically(
        manifest_path,
        json.dumps(manifest, ensure_ascii=False, indent=2) + os.linesep,
    )

    print(f"Подготовлен внешний read-only index.html: {output_path}")
    print(f"SHA-256: {manifest['outputSha256']}")
    print(f"Резервная копия источника: {input_backup}")
    if output_backup:
        print(f"Резервная копия предыдущего результата: {output_backup}")
    print(f"Манифест: {manifest_path}")
    return 0


def check(input_path: Path) -> int:
    value = read_utf8(input_path)
    validate_external_index(value)
    print(f"Внешний read-only index.html корректен: {input_path}")
    print(f"SHA-256: {sha256_text(value)}")
    return 0


def remove(input_path: Path, output_path: Path, backup_directory: Path) -> int:
    source = read_utf8(input_path)
    clean = remove_managed_block(source)
    if clean == source:
        print("Управляемый блок КиноПоиска отсутствует; изменений нет.")
        return 0

    backup = create_backup(input_path, backup_directory, "index.before-remove")
    write_utf8_atomically(output_path, clean)
    print(f"Управляемый блок КиноПоиска удалён: {output_path}")
    print(f"Резервная копия: {backup}")
    print(f"SHA-256: {sha256_text(clean)}")
    return 0


def self_test() -> int:
    original = "<!doctype html><html><body><main>Jellyfin</main></body></html>"
    prepared = build_external_index(original)
    validate_external_index(prepared)
    if build_external_index(prepared) != prepared:
        raise IndexPreparationError("Повторная подготовка оказалась неидемпотентной.")
    if remove_managed_block(prepared) != original:
        raise IndexPreparationError("Удаление управляемого блока изменило исходный HTML.")

    malformed_values = (
        original.replace("</body>", BEGIN_MARKER + "</body>"),
        original.replace("</body>", END_MARKER + "</body>"),
        original.replace("</body>", BEGIN_MARKER + BEGIN_MARKER + END_MARKER + "</body>"),
        "<html><head></head></html>",
    )
    for malformed in malformed_values:
        try:
            build_external_index(malformed)
        except IndexPreparationError:
            continue
        raise IndexPreparationError(
            "Самопроверка не отклонила повреждённый index.html."
        )

    print("Самопроверка подготовки read-only index.html успешно пройдена.")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Безопасная подготовка внешнего read-only index.html для "
            "автономного веб-клиента КиноПоиска."
        )
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    prepare_parser = subparsers.add_parser("prepare", help="подготовить index.html")
    prepare_parser.add_argument("--input", required=True, type=Path)
    prepare_parser.add_argument("--output", required=True, type=Path)
    prepare_parser.add_argument("--backup-dir", required=True, type=Path)

    check_parser = subparsers.add_parser("check", help="проверить index.html")
    check_parser.add_argument("--input", required=True, type=Path)

    remove_parser = subparsers.add_parser("remove", help="удалить управляемый блок")
    remove_parser.add_argument("--input", required=True, type=Path)
    remove_parser.add_argument("--output", required=True, type=Path)
    remove_parser.add_argument("--backup-dir", required=True, type=Path)

    subparsers.add_parser("self-test", help="выполнить встроенную самопроверку")
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    try:
        if arguments.command == "prepare":
            return prepare(arguments.input, arguments.output, arguments.backup_dir)
        if arguments.command == "check":
            return check(arguments.input)
        if arguments.command == "remove":
            return remove(arguments.input, arguments.output, arguments.backup_dir)
        if arguments.command == "self-test":
            return self_test()
        raise IndexPreparationError("Неизвестная команда.")
    except IndexPreparationError as error:
        print(f"ОШИБКА: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
