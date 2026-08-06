# Автономный КиноПоиск в защищённом `media-core`

Комплект предназначен для Jellyfin 10.11.11, запущенного с `read_only: true`.
JavaScript Injector не требуется.

## Архитектура

- Клиентский JavaScript хранится внутри `Jellyfin.Plugin.Kinopoisk.dll`.
- Jellyfin выдаёт bundle через `Kinopoisk/WebClient.js`.
- На хосте создаётся отдельный управляемый `index.html`.
- В контейнер он подключается точечным bind mount с `read_only: true`.
- Корневой слой контейнера и сам `index.html` остаются неизменяемыми.
- Отдельный JavaScript-файл в Jellyfin Web не создаётся.

## Состав комплекта

- `install-media-core-autonomous.sh` — транзакционные `plan`, `apply`, `verify` и `rollback`.
- `prepare-kinopoisk-readonly-index.py` — атомарная подготовка, проверка и удаление управляемого блока.
- `prepare-media-core-readonly.sh` — подготовка внешнего `index.html` и Compose override без замены DLL и без перезапуска.
- `Jellyfin.Plugin.Kinopoisk.dll` и `.pdb` — основной плагин.
- `KinopoiskUnofficialInfo.ApiClient.dll` и `.pdb` — API-клиент.
- `SHA256SUMS` — контрольные суммы всех файлов комплекта.

## Рекомендуемый порядок

Сначала выполнить только read-only план:

```bash
chmod +x \
  install-media-core-autonomous.sh \
  prepare-media-core-readonly.sh \
  prepare-kinopoisk-readonly-index.py

./install-media-core-autonomous.sh plan
```

Команда `plan`:

- проверяет SHA-256 комплекта;
- проверяет базовый Compose и `read_only: true`;
- использует существующий Compose-проект `media-core-jellyfin`;
- обнаруживает реальный `/config` mount и фактически загруженный каталог КиноПоиска;
- показывает текущие и целевые SHA-256;
- не изменяет файлы и контейнеры.

После проверки плана применяется явная транзакционная команда:

```bash
./install-media-core-autonomous.sh apply --confirm
```

## Что делает `apply`

1. Создаёт отдельную транзакцию в `/srv/media-core/backups/jellyfin-kinopoisk/`.
2. Резервирует текущий каталог плагина, XML-конфигурацию, Compose-файл, прежний override и внешний `index.html`.
3. Создаёт самостоятельный `rollback.sh` внутри транзакции.
4. Подготавливает внешний read-only `index.html`.
5. Проверяет временный Compose override до установки и установленный override после записи.
6. Создаёт отдельный каталог `КиноПоиск_10.11.0.3` и меняет только DLL/PDB и версию в `meta.json`.
7. Сохраняет `Jellyfin.Plugin.Kinopoisk.xml` без изменений.
8. Пересоздаёт только сервис Jellyfin в проекте `media-core-jellyfin`.
9. Не пересоздаёт и не перезапускает `jellyfin-egress-proxy`.
10. Проверяет `healthy`, Jellyfin 10.11.11, `ReadonlyRootfs`, read-only mount, API endpoint, MIME, ETag, `304 Not Modified`, состав bundle и журналы загрузки.
11. Подтверждает отсутствие автоматического запуска Preview и Apply.
12. При любой критической ошибке автоматически выполняет точный откат.

Пакет не запускает обновление библиотек.

## Проверка установленного состояния

```bash
./install-media-core-autonomous.sh verify
```

При наличии `LATEST` проверяется последняя транзакция, включая неизменность XML-конфигурации и состояние `jellyfin-egress-proxy`.

## Ручной откат

Откат последней транзакции:

```bash
./install-media-core-autonomous.sh rollback --confirm
```

Откат конкретной транзакции:

```bash
./install-media-core-autonomous.sh rollback \
  /srv/media-core/backups/jellyfin-kinopoisk/ТРАНЗАКЦИЯ \
  --confirm
```

Также каждая транзакция содержит самостоятельный сценарий:

```bash
/srv/media-core/backups/jellyfin-kinopoisk/ТРАНЗАКЦИЯ/rollback.sh
```

## Только подготовка Web-слоя

Для диагностики и формирования Web-слоя без замены DLL и без перезапуска Jellyfin:

```bash
./prepare-media-core-readonly.sh plan
./prepare-media-core-readonly.sh prepare
```

## Управляемый блок

```html
<!-- KINOPOISK_WEB_CLIENT_BEGIN -->
    <script src="../Kinopoisk/WebClient.js" defer data-kinopoisk-managed="external"></script>
<!-- KINOPOISK_WEB_CLIENT_END -->
```

Относительный путь сохраняет совместимость с обычным `/web/` и Jellyfin Base URL.
URL не фиксирует версию bundle: после замены DLL браузер безопасно перепроверяет ресурс по ETag.

## Проверка отдельного `index.html`

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
