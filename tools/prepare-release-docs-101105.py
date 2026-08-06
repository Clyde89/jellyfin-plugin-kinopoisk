from pathlib import Path


VERSION = "10.11.0.5"


def update_changelog() -> None:
    path = Path("CHANGELOG.md")
    text = path.read_text(encoding="utf-8")

    old_heading = "## [10.11.0.0]"
    new_heading = f"## [{VERSION}]"
    if text.count(old_heading) != 1:
        raise RuntimeError("Исходный раздел 10.11.0.0 не найден однозначно.")
    if new_heading in text:
        raise RuntimeError(f"Раздел {VERSION} уже существует.")

    text = text.replace(old_heading, new_heading, 1)
    section_start = text.index(new_heading)
    next_section = text.find("\n## [", section_start + len(new_heading))
    if next_section < 0:
        next_section = len(text)
    section = text[section_start:next_section]

    added_marker = "### Добавлено\n\n"
    fixed_marker = "### Исправлено\n\n"
    security_marker = "### Безопасность\n\n"
    for marker in (added_marker, fixed_marker, security_marker):
        if marker not in section:
            raise RuntimeError(f"Раздел релизных заметок не найден: {marker.strip()}")

    added_lines = (
        "- Добавлен автономный endpoint `Kinopoisk/WebClient.js` с MIME-проверкой, `ETag`, ответом `304 Not Modified` и заголовком `X-Content-Type-Options: nosniff`.\n"
        "- Добавлены объединённый блок «Похожие и рекомендации» и вкладки `КиноПоиск` / `The Movie Database`.\n"
        "- Добавлено серверное обогащение похожих фильмов КиноПоиска годом, рейтингом, описанием, IMDb ID и типом медиа.\n"
        "- Добавлено сопоставление карточек КиноПоиска с TMDB через IMDb ID и резервный точный поиск Seerr по типу, году и нормализованному названию.\n"
        "- Добавлено переиспользование штатных карточек Jellyfin Enhanced с состоянием Seerr и кнопкой запроса.\n"
        "- Добавлены транзакционный установщик для `media-core`, точный откат, read-only bind mount управляемого `index.html` и полная runtime-проверка.\n"
    )
    fixed_lines = (
        "- Исправлена гонка SPA-навигации, из-за которой блок «Похожие и рекомендации» не появлялся при первом открытии или пропадал после переходов.\n"
        "- Исправлено повторное использование устаревшей DOM-страницы после асинхронной загрузки карточки.\n"
        "- Добавлена отмена устаревших попыток рендера по `itemId` и поколению, ограниченные повторные попытки и контроль отсутствующего блока.\n"
        "- Исправлено геометрическое выравнивание стрелок рекомендаций относительно ближайшей штатной карусели Jellyfin на десктопе и мобильных экранах.\n"
        "- Удалено дополнительное вмешательство в штатное поле дат релиза Jellyfin Enhanced, включая кнопку календаря, собственное окно и резервные запросы TMDB.\n"
    )
    security_lines = (
        "- Исключена передача API-токена КиноПоиска и пользовательских данных в автономный JavaScript bundle.\n"
        "- Сохранены неизменяемый корневой слой контейнера и read-only подключение управляемого `index.html` в защищённой схеме `media-core`.\n"
    )

    section = section.replace(added_marker, added_marker + added_lines, 1)
    section = section.replace(fixed_marker, fixed_marker + fixed_lines, 1)
    section = section.replace(security_marker, security_marker + security_lines, 1)
    text = text[:section_start] + section + text[next_section:]
    path.write_text(text, encoding="utf-8")


def update_readme() -> None:
    path = Path("README.md")
    text = path.read_text(encoding="utf-8")

    old_version = "Первая самостоятельная версия форка подготовлена под номером `10.11.0.0`."
    new_version = f"Первая самостоятельная версия форка подготовлена под номером `{VERSION}`."
    if text.count(old_version) != 1:
        raise RuntimeError("Строка версии README не найдена однозначно.")
    text = text.replace(old_version, new_version, 1)

    features_marker = "## Возможности\n\n"
    web_section = """### Дополнительный веб-клиент

- В DLL добавлен автономный endpoint `Kinopoisk/WebClient.js`.
- Добавлены рекомендации КиноПоиска и TMDB, интеграция с Seerr/Jellyfin Enhanced, карточки запросов и устойчивый SPA-жизненный цикл.
- Добавлено геометрическое выравнивание навигации со штатными каруселями Jellyfin.
- Исключено вмешательство в штатное поле дат релиза Jellyfin Enhanced.

Стандартной установкой из каталога установлен серверный плагин и встроен endpoint веб-клиента, но `index.html` Jellyfin Web автоматически не изменён. Для активации дополнительных клиентских функций требуется отдельное безопасное подключение `Kinopoisk/WebClient.js`.

Сценарий `tools/install-media-core-autonomous.sh` подготовлен для согласованной инфраструктуры `media-core`, сохраняет `read_only: true` и не является универсальным установщиком для произвольной инсталляции Jellyfin.

"""
    if features_marker not in text:
        raise RuntimeError("Раздел возможностей README не найден.")
    if "### Дополнительный веб-клиент" not in text:
        text = text.replace(features_marker, features_marker + web_section, 1)

    old_note = (
        "> [!NOTE]\n"
        "> До публикации первого самостоятельного релиза каталог может не содержать доступных версий."
    )
    new_note = (
        "> [!NOTE]\n"
        "> Каталог обновляется автоматически после успешной публикации GitHub Release и ветки `release`."
    )
    if old_note in text:
        text = text.replace(old_note, new_note, 1)

    plan_start = text.find("## План развития\n")
    development_start = text.find("## Разработка\n", plan_start)
    if plan_start < 0 or development_start < 0:
        raise RuntimeError("Границы раздела плана развития README не найдены.")
    plan = """## План развития

Запланированы следующие направления:

- подготовка универсального и документированного способа подключения автономного веб-клиента без изменения защищённой архитектуры Jellyfin;
- расширение runtime-проверок на дополнительных клиентах Jellyfin и мобильных разрешениях;
- отдельная приёмка фактического применения управляемых коллекций перед выводом write-переключателей в интерфейс;
- последующая адаптация к новым стабильным версиям Jellyfin;
- дальнейшее повышение наблюдаемости API, кэшей и клиентских интеграций.

Автоматические операции изменения коллекций не включены без свежего совпадающего preview-отчёта и явного ручного запуска.

"""
    text = text[:plan_start] + plan + text[development_start:]
    path.write_text(text, encoding="utf-8")


def validate() -> None:
    changelog = Path("CHANGELOG.md").read_text(encoding="utf-8")
    readme = Path("README.md").read_text(encoding="utf-8")

    if f"## [{VERSION}]" not in changelog:
        raise RuntimeError("Раздел релиза не добавлен в CHANGELOG.md.")
    if "## [10.11.0.0]" in changelog:
        raise RuntimeError("Устаревший раздел 10.11.0.0 сохранён в CHANGELOG.md.")
    if "### Дополнительный веб-клиент" not in readme:
        raise RuntimeError("Раздел дополнительного веб-клиента не добавлен в README.md.")
    if f"под номером `{VERSION}`" not in readme:
        raise RuntimeError("Версия README.md не обновлена.")


def main() -> None:
    update_changelog()
    update_readme()
    validate()


if __name__ == "__main__":
    main()
