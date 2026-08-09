# Самодостаточный КиноПоиск в защищённом `media-core`

Комплект подготовлен для Jellyfin 10.11.11 с `read_only: true`.
JavaScript Injector исключён из обязательных зависимостей.

## Архитектура

- Клиентский JavaScript встроен в `Jellyfin.Plugin.Kinopoisk.dll`.
- Bundle опубликован серверным endpoint `Kinopoisk/WebClient.js`.
- Подключение bundle перенесено в Runtime Web Bootstrap внутри HTTP pipeline Jellyfin.
- Штатный `jellyfin-web/index.html` сохранён без изменений на диске.
- Внешний bind mount `index.html` исключён из новой схемы.
- Совместимость с Jellyfin Base URL сохранена.
- Сильный ETag и ответ `304 Not Modified` добавлены для runtime HTML и `WebClient.js`.
- Для композитного HTML, дополнительно изменённого Jellyfin Enhanced, учтено штатное
  удаление устаревшего ETag внешним middleware; собственный ETag `WebClient.js` сохранён.
- Работа с `ReadonlyRootfs=true` подтверждена постоянными runtime-проверками.

## Состав автономного комплекта

- `install-media-core-autonomous.sh` — добавлены транзакционные режимы `plan`, `apply`, `verify` и `rollback`.
- `Jellyfin.Plugin.Kinopoisk.dll` и `.pdb` — добавлены основной плагин и отладочные символы.
- `KinopoiskUnofficialInfo.ApiClient.dll` и `.pdb` — добавлены API-клиент и отладочные символы.
- `PACKAGE-INFO.txt` — добавлена сводка кандидата и его установочной модели.
- `SHA256SUMS` — добавлены контрольные суммы файлов комплекта.

Утилиты прежнего external Web-слоя сохранены только в репозитории для совместимости и тестов миграции. В автономный пакет `10.11.0.6` они не включены.

## Read-only план

Сначала запущен только диагностический режим:

```bash
chmod 750 install-media-core-autonomous.sh
./install-media-core-autonomous.sh plan
```

В режиме `plan` выполнены следующие проверки:

- подтверждена целостность комплекта по SHA-256;
- подтверждён базовый Compose и `read_only: true`;
- обнаружен существующий Compose-проект `media-core-jellyfin`;
- обнаружены реальный `/config` mount и фактически загруженный каталог КиноПоиска;
- определён фактический путь штатного `index.html` внутри контейнера;
- проверено отсутствие Web mount в базовом Compose;
- распознан прежний управляемый external mount, когда он присутствовал;
- выведены текущие и целевые SHA-256;
- изменения файлов и контейнеров не выполнены.

## Транзакционное применение

После проверки вывода `plan` запущена явная команда применения:

```bash
./install-media-core-autonomous.sh apply --confirm
```

В режиме `apply` выполнены следующие действия:

1. Создана отдельная транзакция в `/srv/media-core/backups/jellyfin-kinopoisk/`.
2. Сохранены текущий каталог плагина, XML-конфигурация, базовый Compose и `docker inspect` Jellyfin.
3. Сохранены прежний Compose override и внешний `index.html`, когда они существовали.
4. Создан самостоятельный `rollback.sh` внутри транзакции.
5. Подготовлен отдельный каталог `КиноПоиск_10.11.0.6`.
6. Заменены только DLL/PDB и версия в `meta.json`.
7. `Jellyfin.Plugin.Kinopoisk.xml` сохранён без изменений.
8. Удалены только распознанные legacy-файлы external Web-слоя.
9. Jellyfin пересоздан только из базового Compose проекта `media-core-jellyfin`.
10. `jellyfin-egress-proxy` сохранён без пересоздания и перезапуска.
11. Подтверждены `healthy`, Jellyfin 10.11.11 и `ReadonlyRootfs=true`.
12. Подтверждено отсутствие mount на штатный `index.html`.
13. Подтверждены Runtime Web Bootstrap и единственный runtime-блок. Для самостоятельного
    ответа подтверждены сильный ETag и `304 Not Modified`; отсутствие ETag разрешено только
    при однозначно подтверждённой последующей инъекции Jellyfin Enhanced.
14. Подтверждены MIME, ETag, `nosniff` и состав `WebClient.js`.
15. Подтверждена загрузка версии `10.11.0.6` без критических ошибок DI/assembly loading.
16. Подтверждено отсутствие автоматического запуска Preview и Apply.
17. При критической ошибке запущен автоматический точный откат.

Обновление библиотек медиатеки установщиком не запускалось.

## Проверка установленного состояния

Отдельная runtime-проверка выполнена командой:

```bash
./install-media-core-autonomous.sh verify
```

При переданном пути транзакции дополнительно подтверждены исходный SHA-256 XML-конфигурации и неизменность `jellyfin-egress-proxy`:

```bash
./install-media-core-autonomous.sh verify \
  /srv/media-core/backups/jellyfin-kinopoisk/ТРАНЗАКЦИЯ
```

## Ручной откат

Откат последней транзакции выполнен командой:

```bash
./install-media-core-autonomous.sh rollback --confirm
```

Откат выбранной транзакции выполнен командой:

```bash
./install-media-core-autonomous.sh rollback \
  /srv/media-core/backups/jellyfin-kinopoisk/ТРАНЗАКЦИЯ \
  --confirm
```

В каждой транзакции также создан самостоятельный сценарий:

```bash
/srv/media-core/backups/jellyfin-kinopoisk/ТРАНЗАКЦИЯ/rollback.sh
```

При откате восстановлены прежний каталог плагина, XML-конфигурация и legacy external Web-слой, когда он существовал до транзакции.

## Миграция прежней external-схемы

Для известной прежней схемы использованы следующие значения по умолчанию:

- Compose override: `/srv/media-core/compose/compose.jellyfin.kinopoisk-web.yaml`;
- внешний HTML: `/srv/media-core/appdata/jellyfin-web/kinopoisk/index.html`.

Перед удалением выполнено строгое сопоставление фактического mount с известным управляемым источником. Неизвестный mount на штатный `index.html` остановил применение до внесения изменений.

После успешной миграции внешний override и внешний HTML удалены, а штатный `index.html` внутри Jellyfin остался неизменным.

## Runtime-блок

Runtime Web Bootstrap сформировал в HTTP-ответе один управляемый блок:

```html
<!-- KINOPOISK_WEB_CLIENT_BEGIN -->
<script data-kinopoisk-managed="runtime" src="../Kinopoisk/WebClient.js?v=..."></script>
<!-- KINOPOISK_WEB_CLIENT_END -->
```

Этот блок сформирован только в ответе HTTP. Запись блока в файл Jellyfin Web не выполнялась.
