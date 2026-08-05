# Автономный веб-клиент КиноПоиска в защищённом контейнере

Этот комплект предназначен для Jellyfin, запущенного с `read_only: true`.
JavaScript Injector не требуется.

## Архитектура

- Клиентский JavaScript хранится внутри `Jellyfin.Plugin.Kinopoisk.dll`.
- Jellyfin выдаёт bundle через `Kinopoisk/WebClient.js`.
- На хосте создаётся отдельный управляемый `index.html`.
- В контейнер он подключается точечным bind mount с `read_only: true`.
- Корневой слой контейнера остаётся неизменяемым.
- Отдельный JavaScript-файл в Jellyfin Web не создаётся.

## Файлы

- `prepare-kinopoisk-readonly-index.py` — атомарно подготавливает, проверяет и удаляет управляемый блок.
- `prepare-media-core-readonly.sh` — диагностирует `media-core`, формирует внешний `index.html` и отдельный Compose override.
- `Jellyfin.Plugin.Kinopoisk.dll` — основной плагин.
- `KinopoiskUnofficialInfo.ApiClient.dll` — API-клиент.
- `SHA256SUMS` — контрольные суммы файлов комплекта.

## Безопасная подготовка

Сначала проверить план без записи файлов:

```bash
chmod +x prepare-media-core-readonly.sh prepare-kinopoisk-readonly-index.py
./prepare-media-core-readonly.sh plan
```

Затем подготовить внешний индекс и Compose override без перезапуска Jellyfin:

```bash
./prepare-media-core-readonly.sh prepare
```

Команда `prepare`:

1. Копирует штатный `index.html` из работающего контейнера.
2. Создаёт резервную копию.
3. Удаляет прежний управляемый блок КиноПоиска, если он существует.
4. Добавляет один внешний read-only блок.
5. Создаёт отдельный `compose.jellyfin.kinopoisk-web.yaml`.
6. Проверяет объединённую конфигурацию через `docker compose config --format json`.
7. Подтверждает, что `read_only: true` сохранён и bind mount также read-only.

Утилита не заменяет DLL, не выполняет `docker compose up`, не перезапускает Jellyfin и не запускает Preview, Apply или обновление библиотек.

## Управляемый блок

```html
<!-- KINOPOISK_WEB_CLIENT_BEGIN -->
    <script src="../Kinopoisk/WebClient.js" defer data-kinopoisk-managed="external"></script>
<!-- KINOPOISK_WEB_CLIENT_END -->
```

Относительный путь сохраняет совместимость как с обычным `/web/`, так и с Jellyfin Base URL.
URL не содержит фиксированную версию: браузер выполняет безопасную повторную проверку по ETag после замены DLL.

## Проверка отдельного index.html

```bash
python3 prepare-kinopoisk-readonly-index.py check \
  --input /srv/media-core/appdata/jellyfin-web/kinopoisk/index.html
```

## Удаление управляемого блока

```bash
python3 prepare-kinopoisk-readonly-index.py remove \
  --input /srv/media-core/appdata/jellyfin-web/kinopoisk/index.html \
  --output /srv/media-core/appdata/jellyfin-web/kinopoisk/index.clean.html \
  --backup-dir /srv/media-core/appdata/jellyfin-web/kinopoisk/backups
```

Фактическое применение DLL и пересоздание контейнера выполняются отдельным контролируемым этапом после резервного копирования текущего каталога плагина.
