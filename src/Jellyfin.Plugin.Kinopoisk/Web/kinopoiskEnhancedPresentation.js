(function () {
    'use strict';

    if (window.__kinopoiskEnhancedPresentationInstalled) {
        return;
    }

    window.__kinopoiskEnhancedPresentationInstalled = true;

    var coreCache = new Map();
    var supplementalCache = new Map();
    var activeDatesDialog = null;

    var professionLabels = {
        DIRECTOR: 'Режиссёры',
        OPERATOR: 'Операторы',
        WRITER: 'Сценаристы',
        PRODUCER: 'Продюсеры',
        COMPOSER: 'Композиторы',
        EDITOR: 'Монтаж',
        DESIGN: 'Художники',
        ACTOR: 'Актёры',
        TRANSLATOR: 'Переводчики',
        VOICE_DIRECTOR: 'Режиссёры дубляжа',
        UNKNOWN: 'Другие участники'
    };

    var relationLabels = {
        PREQUEL: 'Приквел',
        SEQUEL: 'Сиквел',
        REMAKE: 'Ремейк'
    };

    var boxOfficeLabels = {
        BUDGET: 'Бюджет',
        RUS: 'Сборы в России',
        USA: 'Сборы в США',
        WORLD: 'Сборы в мире'
    };

    var countryAliases = {
        RU: { display: 'Россия', prepositional: 'России', compact: 'Россия' },
        RUSSIA: { display: 'Россия', prepositional: 'России', compact: 'Россия' },
        'РОССИЯ': { display: 'Россия', prepositional: 'России', compact: 'Россия' },
        US: { display: 'США', prepositional: 'США', compact: 'США' },
        USA: { display: 'США', prepositional: 'США', compact: 'США' },
        'UNITED STATES': { display: 'США', prepositional: 'США', compact: 'США' },
        'США': { display: 'США', prepositional: 'США', compact: 'США' }
    };

    function pick(object) {
        if (!object) {
            return undefined;
        }

        for (var index = 1; index < arguments.length; index++) {
            var key = arguments[index];
            if (Object.prototype.hasOwnProperty.call(object, key)) {
                return object[key];
            }
        }

        return undefined;
    }

    function asArray(value) {
        return Array.isArray(value) ? value : [];
    }

    function getCurrentItemId() {
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        if (lifecycle) {
            return lifecycle.getCurrentItemId();
        }
        var candidates = [window.location.search || ''];
        var hash = window.location.hash || '';
        var queryIndex = hash.indexOf('?');
        if (queryIndex >= 0) {
            candidates.push(hash.substring(queryIndex));
        }

        for (var index = 0; index < candidates.length; index++) {
            var value = new URLSearchParams(candidates[index]).get('id');
            if (value) {
                return value;
            }
        }

        return null;
    }

    function getApiClient() {
        return window.ApiClient || null;
    }

    function getProviderId(item, requestedKey) {
        var ids = pick(item, 'ProviderIds', 'providerIds') || {};
        var keys = Object.keys(ids);
        for (var index = 0; index < keys.length; index++) {
            if (keys[index].toLowerCase() === requestedKey.toLowerCase()) {
                return ids[keys[index]];
            }
        }
        return null;
    }

    function getAuthHeaders() {
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        if (lifecycle) {
            return lifecycle.getAuthHeaders('application/json');
        }
        var apiClient = getApiClient();
        var token = apiClient && typeof apiClient.accessToken === 'function'
            ? apiClient.accessToken()
            : '';
        return {
            'Authorization': 'MediaBrowser Token="' + token + '"',
            'X-Emby-Token': token,
            'Accept': 'application/json'
        };
    }

    function fetchJson(path) {
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        if (lifecycle) {
            return lifecycle.fetchJson(path);
        }
        var apiClient = getApiClient();
        if (!apiClient) {
            return Promise.reject(new Error('ApiClient недоступен.'));
        }

        return fetch(apiClient.getUrl(path), {
            credentials: 'same-origin',
            headers: getAuthHeaders()
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        });
    }

    function fetchCurrentItem(itemId) {
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        if (lifecycle) {
            return lifecycle.fetchItem(itemId);
        }
        var apiClient = getApiClient();
        if (!apiClient || !itemId || typeof apiClient.getItem !== 'function') {
            return Promise.resolve(null);
        }

        return apiClient.getItem(apiClient.getCurrentUserId(), itemId)
            .catch(function (error) {
                console.warn('[КиноПоиск] Карточка Jellyfin не загружена.', error);
                return null;
            });
    }

    function fetchCore(kinopoiskId) {
        var key = String(kinopoiskId);
        if (!coreCache.has(key)) {
            coreCache.set(
                key,
                fetchJson('/KinopoiskPresentation/' + encodeURIComponent(key))
                    .catch(function (error) {
                        coreCache.delete(key);
                        throw error;
                    })
            );
        }
        return coreCache.get(key);
    }

    function fetchSupplemental(kinopoiskId, section) {
        var key = String(kinopoiskId) + ':' + section;
        if (!supplementalCache.has(key)) {
            supplementalCache.set(
                key,
                fetchJson(
                    '/KinopoiskPresentation/'
                    + encodeURIComponent(String(kinopoiskId))
                    + '/'
                    + section
                ).catch(function (error) {
                    supplementalCache.delete(key);
                    throw error;
                })
            );
        }
        return supplementalCache.get(key);
    }

    function formatVotes(value) {
        var number = Number(value || 0);
        return number > 0
            ? new Intl.NumberFormat('ru-RU').format(number) + ' оценок'
            : '';
    }

    function formatDate(value) {
        if (!value) {
            return '';
        }
        var normalized = /^\d{4}-\d{2}-\d{2}$/.test(value)
            ? value + 'T12:00:00'
            : value;
        var date = new Date(normalized);
        if (Number.isNaN(date.getTime())) {
            return value;
        }
        return date.toLocaleDateString('ru-RU', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric'
        });
    }

    function formatRating(value, percentage) {
        var number = Number(value || 0);
        if (!(number > 0)) {
            return '';
        }
        return percentage
            ? new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 0 }).format(number) + '%'
            : new Intl.NumberFormat('ru-RU', {
                minimumFractionDigits: 1,
                maximumFractionDigits: 1
            }).format(number);
    }

    function formatMoney(item) {
        var amount = Number(pick(item, 'amount', 'Amount') || 0);
        var currency = String(pick(item, 'currency', 'Currency') || '').trim();
        var symbol = String(pick(item, 'symbol', 'Symbol') || '').trim();
        if (!Number.isFinite(amount)) {
            return '';
        }

        var formatted = new Intl.NumberFormat('ru-RU', {
            maximumFractionDigits: amount % 1 === 0 ? 0 : 2
        }).format(amount);
        return symbol
            ? symbol + formatted
            : formatted + (currency ? ' ' + currency : '');
    }

    function createElement(tagName, className, text) {
        var element = document.createElement(tagName);
        if (className) {
            element.className = className;
        }
        if (text != null) {
            element.textContent = text;
        }
        return element;
    }

    function createSafeLink(url, text, className) {
        var link = createElement('a', className || 'kp-link', text);
        try {
            var parsed = new URL(url);
            var host = parsed.hostname.toLowerCase();
            var allowed = host === 'www.kinopoisk.ru'
                || host === 'kinopoisk.ru'
                || host === 'www.imdb.com'
                || host === 'imdb.com';
            if (!allowed || parsed.protocol !== 'https:') {
                return null;
            }
            link.href = parsed.href;
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            link.referrerPolicy = 'strict-origin-when-cross-origin';
            return link;
        } catch (_) {
            return null;
        }
    }

    function normalizeImageUrl(value) {
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        if (lifecycle) {
            return lifecycle.normalizeImageUrl(value);
        }
        if (!value) {
            return null;
        }

        try {
            var parsed = new URL(String(value));
            var host = parsed.hostname.toLowerCase();
            var allowed = host === 'kinopoiskapiunofficial.tech'
                || host.endsWith('.yandex.net')
                || host.endsWith('.kinopoisk.ru')
                || host.endsWith('.kp.yandex.net');
            if (!allowed || (parsed.protocol !== 'http:' && parsed.protocol !== 'https:')) {
                return null;
            }
            parsed.protocol = 'https:';
            parsed.username = '';
            parsed.password = '';
            parsed.hash = '';
            return parsed.href;
        } catch (_) {
            return null;
        }
    }

    function normalizeCountry(value) {
        var raw = String(value || '').trim();
        var alias = countryAliases[raw.toUpperCase()];
        return alias || {
            display: raw,
            prepositional: raw,
            compact: raw
        };
    }

    function formatKinopoiskReleaseLabel(type, country) {
        if (type === 'WORLD_PREMIER') {
            return 'Мировая премьера';
        }
        if ((type === 'PREMIERE' || type === 'COUNTRY_SPECIFIC') && country) {
            var normalized = normalizeCountry(country);
            if (normalized.prepositional) {
                return 'Премьера в ' + normalized.prepositional;
            }
        }
        if (type === 'PREMIERE') {
            return 'Премьера';
        }
        if (type === 'COUNTRY_SPECIFIC') {
            return 'Начало проката';
        }
        return 'Дата релиза';
    }

    function buildDateKey(item) {
        if ((item.type === 'PREMIERE' || item.type === 'COUNTRY_SPECIFIC')
            && item.country) {
            return ['kp-country', item.country, item.date, item.reRelease].join('|');
        }
        return item.key || [item.label, item.date, item.country].join('|');
    }

    function deduplicateDates(items) {
        var seen = new Set();
        return items.filter(function (item) {
            var key = buildDateKey(item);
            if (seen.has(key)) {
                return false;
            }
            seen.add(key);
            return true;
        }).sort(function (left, right) {
            return String(left.date).localeCompare(String(right.date));
        });
    }

    function normalizeReleaseDates(core) {
        return deduplicateDates(asArray(pick(core, 'releaseDates', 'ReleaseDates')).map(function (item) {
            var type = String(pick(item, 'type', 'Type') || '');
            var country = String(pick(item, 'country', 'Country') || '').trim();
            var normalizedCountry = normalizeCountry(country);
            return {
                key: [type, pick(item, 'date', 'Date'), normalizedCountry.display].join('|'),
                type: type,
                label: formatKinopoiskReleaseLabel(type, country),
                compactLabel: type === 'WORLD_PREMIER'
                    ? 'Мир'
                    : (normalizedCountry.compact || 'Премьера'),
                date: String(pick(item, 'date', 'Date') || ''),
                country: normalizedCountry.display,
                source: String(pick(item, 'source', 'Source') || 'КиноПоиск'),
                icon: type === 'WORLD_PREMIER' ? 'public' : 'event',
                reRelease: !!pick(item, 'reRelease', 'ReRelease')
            };
        }).filter(function (item) { return item.date; }));
    }

    function selectPrimaryReleaseDates(core) {
        var dates = normalizeReleaseDates(core).filter(function (item) {
            return !item.reRelease;
        });
        var world = dates.filter(function (item) {
            return item.type === 'WORLD_PREMIER';
        })[0];
        var russia = dates.filter(function (item) {
            return normalizeCountry(item.country).display === 'Россия'
                && (item.type === 'PREMIERE' || item.type === 'COUNTRY_SPECIFIC');
        })[0];
        return deduplicateDates([world, russia].filter(Boolean));
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskEnhancedPresentationStyles')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'kinopoiskEnhancedPresentationStyles';
        style.textContent = [
            '.kp-presentation{margin:1.2em 0;padding:1.05em;border:1px solid rgba(255,255,255,.12);border-radius:.75em;background:rgba(12,15,25,.62);backdrop-filter:blur(8px);color:inherit}',
            '.kp-presentation__header{display:flex;align-items:center;gap:.7em;flex-wrap:wrap;margin-bottom:.9em}',
            '.kp-presentation__title{font-size:1.35em;font-weight:700}',
            '.kp-link,.kp-action{display:inline-flex;align-items:center;gap:.3em;padding:.42em .68em;border-radius:999px;border:1px solid rgba(255,255,255,.18);background:rgba(255,255,255,.08);color:inherit;text-decoration:none;cursor:pointer;font:inherit}',
            '.kp-link:hover,.kp-action:hover{background:rgba(255,255,255,.16)}',
            '.kp-ratings{display:flex;gap:.65em;flex-wrap:wrap;margin:.65em 0 1em}',
            '.kp-rating{display:flex;align-items:baseline;gap:.4em;padding:.58em .72em;border-radius:.62em;background:rgba(255,255,255,.08)}',
            '.kp-rating__source{font-weight:600;opacity:.86}',
            '.kp-rating__value{font-size:1.28em;font-weight:800}',
            '.kp-rating__votes{font-size:.83em;opacity:.68}',
            '.kp-date-list{display:flex;gap:.55em;flex-wrap:wrap;margin:.65em 0 1em}',
            '.kp-date-chip{display:inline-flex;align-items:center;gap:.38em;padding:.38em .58em;border-radius:.55em;background:rgba(255,255,255,.08)}',
            '.kp-date-chip__label{font-weight:650}',
            '.kp-standalone-release-dates{display:flex;align-items:center;gap:.6em;flex-wrap:wrap}',
            '.kp-standalone-release-dates__item{display:inline-flex;align-items:center;gap:.28em}',
            '.kp-standalone-release-dates__label{font-weight:650}',
            '.kp-release-more{border:0;background:transparent;color:inherit;cursor:pointer;padding:.15em .3em;opacity:.82}',
            '.kp-sections{display:grid;grid-template-columns:repeat(auto-fit,minmax(17em,1fr));gap:.75em;margin-top:.8em}',
            '.kp-details{border:1px solid rgba(255,255,255,.11);border-radius:.62em;background:rgba(255,255,255,.045);overflow:hidden}',
            '.kp-details>summary{cursor:pointer;padding:.72em .82em;font-weight:650;list-style:none}',
            '.kp-details>summary::-webkit-details-marker{display:none}',
            '.kp-details>summary:after{content:"＋";float:right;opacity:.7}',
            '.kp-details[open]>summary:after{content:"−"}',
            '.kp-details__body{padding:0 .82em .82em}',
            '.kp-row{display:grid;grid-template-columns:minmax(7.5em,.36fr) 1fr;gap:.7em;padding:.38em 0;border-top:1px solid rgba(255,255,255,.07)}',
            '.kp-row__label{font-weight:600;opacity:.78}',
            '.kp-person-list{display:flex;gap:.35em;flex-wrap:wrap;align-items:center}',
            '.kp-person{padding:.2em .45em;border-radius:.38em;background:rgba(255,255,255,.07)}',
            '.kp-person-more{padding:.25em .55em;font-size:.9em}',
            '.kp-relations{display:flex;gap:.65em;overflow-x:auto;padding:.2em 0 .55em;scrollbar-width:thin}',
            '.kp-relation{width:8.2em;flex:0 0 8.2em;color:inherit;text-decoration:none}',
            '.kp-relation img{display:block;width:100%;aspect-ratio:2/3;object-fit:cover;border-radius:.52em;background:rgba(255,255,255,.06)}',
            '.kp-relation__name{display:block;margin-top:.35em;font-size:.9em;line-height:1.25}',
            '.kp-relation__type{display:block;font-size:.78em;opacity:.68}',
            '.kp-list{margin:.25em 0;padding-left:1.2em}',
            '.kp-list li{margin:.38em 0}',
            '.kp-empty,.kp-loading,.kp-error{opacity:.7;padding:.35em 0}',
            '.kp-award{padding:.48em 0;border-top:1px solid rgba(255,255,255,.07)}',
            '.kp-award__name{font-weight:650}',
            '.kp-award__meta{font-size:.86em;opacity:.72}',
            '.kp-win{color:#ffd55a}',
            '.kp-modal{position:fixed;inset:0;z-index:100001;background:rgba(0,0,0,.8);display:flex;align-items:center;justify-content:center;padding:1em}',
            '.kp-modal__card{width:min(42em,100%);max-height:min(80vh,48em);overflow:auto;border-radius:.8em;background:#151824;color:#fff;padding:1em;box-shadow:0 1.2em 4em rgba(0,0,0,.55)}',
            '.kp-modal__header{display:flex;align-items:center;gap:.7em;margin-bottom:.7em}',
            '.kp-modal__title{font-size:1.25em;font-weight:700;margin-right:auto}',
            '.kp-modal__date{display:grid;grid-template-columns:minmax(9em,1fr) auto;gap:.6em;padding:.6em 0;border-top:1px solid rgba(255,255,255,.1)}',
            '.kp-modal__date-meta{font-size:.85em;opacity:.7}',
            '.kp-image-loading{animation:kp-image-pulse 1.2s ease-in-out infinite alternate}',
            '.kp-image-unavailable{background-image:linear-gradient(145deg,rgba(255,255,255,.08),rgba(255,255,255,.02))!important}',
            '@keyframes kp-image-pulse{from{opacity:.55}to{opacity:1}}',
            '@media(max-width:600px){.kp-presentation{padding:.8em}.kp-row{grid-template-columns:1fr}.kp-ratings{gap:.4em}.kp-rating{flex:1 1 9em}.kp-modal__date{grid-template-columns:1fr}.kp-standalone-release-dates__label{display:none}}'
        ].join('');
        document.head.appendChild(style);
    }

    function createDateChip(item) {
        var chip = createElement('span', 'kp-date-chip');
        chip.title = [item.label, item.country, item.source].filter(Boolean).join(' · ');
        chip.append(
            createElement('span', 'material-icons', item.icon || 'event'),
            createElement('span', 'kp-date-chip__label', item.label),
            createElement('span', '', formatDate(item.date))
        );
        return chip;
    }

    function closeDatesDialog() {
        if (activeDatesDialog) {
            activeDatesDialog.remove();
            activeDatesDialog = null;
        }
    }

    function showDatesDialog(items) {
        closeDatesDialog();
        var overlay = createElement('div', 'kp-modal');
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        var card = createElement('div', 'kp-modal__card');
        var header = createElement('div', 'kp-modal__header');
        var close = createElement('button', 'kp-action', 'Закрыть');
        close.type = 'button';
        close.addEventListener('click', closeDatesDialog);
        header.append(
            createElement('div', 'kp-modal__title', 'Все даты релиза'),
            close
        );
        card.appendChild(header);
        if (!items.length) {
            card.appendChild(createElement('div', 'kp-empty', 'Даты релиза не найдены.'));
        } else {
            items.forEach(function (item) {
                var row = createElement('div', 'kp-modal__date');
                var description = createElement('div');
                description.append(
                    createElement('div', '', item.label),
                    createElement(
                        'div',
                        'kp-modal__date-meta',
                        [
                            item.country || 'Страна не указана',
                            item.source,
                            item.reRelease ? 'повторный прокат' : ''
                        ].filter(Boolean).join(' · ')
                    )
                );
                row.append(description, createElement('div', '', formatDate(item.date)));
                card.appendChild(row);
            });
        }
        overlay.appendChild(card);
        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) {
                closeDatesDialog();
            }
        });
        document.body.appendChild(overlay);
        activeDatesDialog = overlay;
        close.focus();
    }

    function renderStandaloneReleaseDates(context, primaryDates, allDates) {
        var page = context.page;
        var existing = page.querySelector('.kp-standalone-release-dates');
        if (window.JellyfinEnhanced || !primaryDates.length) {
            if (existing) {
                existing.remove();
            }
            return;
        }
        if (existing && existing.dataset.itemId === context.itemId) {
            return;
        }
        if (existing) {
            existing.remove();
        }
        var host = page.querySelector('.itemMiscInfo-primary')
            || page.querySelector('.itemMiscInfo')
            || page.querySelector('.mediaInfoItems');
        if (!host) {
            return;
        }
        var row = createElement('div', 'mediaInfoItem kp-standalone-release-dates');
        row.dataset.itemId = context.itemId;
        primaryDates.forEach(function (item) {
            var entry = createElement('span', 'kp-standalone-release-dates__item');
            entry.title = [item.label, item.country, item.source].filter(Boolean).join(' · ');
            entry.append(
                createElement('span', 'material-icons', item.icon || 'event'),
                createElement('span', 'kp-standalone-release-dates__label', item.compactLabel),
                createElement('span', '', formatDate(item.date))
            );
            row.appendChild(entry);
        });
        var more = createElement('button', 'kp-release-more material-icons', 'calendar_month');
        more.type = 'button';
        more.title = 'Показать все даты релиза КиноПоиска';
        more.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            showDatesDialog(allDates);
        });
        row.appendChild(more);
        host.appendChild(row);
    }

    function createRating(source, value, votes, percentage) {
        var formatted = formatRating(value, percentage);
        if (!formatted) {
            return null;
        }

        var card = createElement('div', 'kp-rating');
        card.append(
            createElement('span', 'kp-rating__source', source),
            createElement('span', 'kp-rating__value', formatted)
        );
        var votesText = formatVotes(votes);
        if (votesText) {
            card.appendChild(createElement('span', 'kp-rating__votes', votesText));
        }
        return card;
    }

    function renderRatings(core) {
        var ratings = pick(core, 'ratings', 'Ratings') || {};
        var container = createElement('div', 'kp-ratings');
        var cards = [
            createRating('КиноПоиск', pick(ratings, 'kinopoisk', 'Kinopoisk'), pick(ratings, 'kinopoiskVotes', 'KinopoiskVotes'), false),
            createRating('IMDb', pick(ratings, 'imdb', 'Imdb'), pick(ratings, 'imdbVotes', 'ImdbVotes'), false),
            createRating('Критики РФ', pick(ratings, 'russianCritics', 'RussianCritics'), pick(ratings, 'russianCriticsVotes', 'RussianCriticsVotes'), true),
            createRating('Критики мира', pick(ratings, 'worldCritics', 'WorldCritics'), pick(ratings, 'worldCriticsVotes', 'WorldCriticsVotes'), true)
        ].filter(Boolean);
        cards.forEach(function (card) { container.appendChild(card); });
        return cards.length ? container : null;
    }

    function renderProfessionDetails(core) {
        var groups = asArray(pick(core, 'professions', 'Professions'));
        if (!groups.length) {
            return null;
        }

        var details = createElement('details', 'kp-details');
        details.appendChild(createElement('summary', '', 'Участники и профессии'));
        var body = createElement('div', 'kp-details__body');
        groups.forEach(function (group) {
            var people = asArray(pick(group, 'people', 'People'));
            if (!people.length) {
                return;
            }

            var row = createElement('div', 'kp-row');
            var key = String(pick(group, 'key', 'Key') || 'UNKNOWN');
            var label = String(pick(group, 'label', 'Label') || professionLabels[key] || key);
            var list = createElement('div', 'kp-person-list');
            var pageSize = 10;
            var visibleCount = Math.min(pageSize, people.length);

            function renderPeople() {
                list.textContent = '';
                people.slice(0, visibleCount).forEach(function (person) {
                    var name = String(
                        pick(person, 'name', 'Name')
                        || pick(person, 'originalName', 'OriginalName')
                        || ''
                    );
                    if (name) {
                        list.appendChild(createElement('span', 'kp-person', name));
                    }
                });

                if (people.length <= pageSize) {
                    return;
                }

                var remaining = people.length - visibleCount;
                var expanded = remaining <= 0;
                var button = createElement(
                    'button',
                    'kp-action kp-person-more',
                    expanded
                        ? 'Свернуть'
                        : 'Показать ещё ' + String(Math.min(pageSize, remaining))
                );
                button.type = 'button';
                button.setAttribute('aria-expanded', expanded ? 'true' : 'false');
                button.addEventListener('click', function () {
                    visibleCount = expanded
                        ? Math.min(pageSize, people.length)
                        : Math.min(people.length, visibleCount + pageSize);
                    renderPeople();
                });
                list.appendChild(button);
            }

            row.appendChild(createElement('div', 'kp-row__label', label));
            row.appendChild(list);
            body.appendChild(row);
            renderPeople();
        });
        details.appendChild(body);
        return body.childElementCount ? details : null;
    }

    function renderRelations(core) {
        var relations = asArray(pick(core, 'relations', 'Relations'));
        if (!relations.length) {
            return null;
        }

        var details = createElement('details', 'kp-details');
        details.open = true;
        details.appendChild(createElement('summary', '', 'Связанные фильмы и франшизы'));
        var body = createElement('div', 'kp-details__body');
        var list = createElement('div', 'kp-relations');
        relations.forEach(function (relation) {
            var url = String(pick(relation, 'kinopoiskUrl', 'KinopoiskUrl') || '');
            var card = createSafeLink(url, '', 'kp-relation');
            if (!card) {
                return;
            }
            var imageUrl = normalizeImageUrl(pick(relation, 'posterUrl', 'PosterUrl'));
            if (imageUrl) {
                var image = document.createElement('img');
                image.alt = '';
                image.loading = 'lazy';
                card.appendChild(image);
                var lifecycle = window.KinopoiskDetailPageLifecycle;
                if (lifecycle) {
                    lifecycle.applyProtectedImage(image, imageUrl, 'image');
                }
            }
            var name = String(
                pick(relation, 'name', 'Name')
                || pick(relation, 'originalName', 'OriginalName')
                || 'Связанный фильм'
            );
            var type = String(pick(relation, 'relationType', 'RelationType') || '');
            card.append(
                createElement('span', 'kp-relation__name', name),
                createElement('span', 'kp-relation__type', relationLabels[type] || type)
            );
            list.appendChild(card);
        });
        body.appendChild(list);
        details.appendChild(body);
        return list.childElementCount ? details : null;
    }

    function createLazyDetails(title, section, renderer, kinopoiskId) {
        var details = createElement('details', 'kp-details');
        details.appendChild(createElement('summary', '', title));
        var body = createElement('div', 'kp-details__body');
        body.appendChild(createElement('div', 'kp-loading', 'Данные будут загружены при открытии раздела.'));
        details.appendChild(body);
        var loaded = false;

        details.addEventListener('toggle', function () {
            if (!details.open || loaded) {
                return;
            }
            loaded = true;
            body.textContent = '';
            body.appendChild(createElement('div', 'kp-loading', 'Загрузка…'));
            fetchSupplemental(kinopoiskId, section)
                .then(function (data) {
                    body.textContent = '';
                    renderer(body, data);
                })
                .catch(function (error) {
                    console.warn('[КиноПоиск] Дополнительный раздел не загружен: ' + section, error);
                    body.textContent = '';
                    body.appendChild(createElement('div', 'kp-error', 'Раздел временно недоступен.'));
                });
        });

        return details;
    }

    function renderFacts(body, data) {
        var items = asArray(pick(data, 'items', 'Items'));
        if (!items.length) {
            body.appendChild(createElement('div', 'kp-empty', 'Факты в API отсутствуют.'));
            return;
        }

        var visible = items.filter(function (item) {
            return !pick(item, 'spoiler', 'Spoiler');
        });
        var spoilers = items.filter(function (item) {
            return !!pick(item, 'spoiler', 'Spoiler');
        });
        var list = createElement('ul', 'kp-list');
        visible.forEach(function (item) {
            var type = String(pick(item, 'type', 'Type') || 'FACT');
            var prefix = type === 'BLOOPER' ? 'Ошибка: ' : '';
            list.appendChild(createElement(
                'li',
                '',
                prefix + String(pick(item, 'text', 'Text') || '')
            ));
        });
        body.appendChild(list);

        if (spoilers.length) {
            var button = createElement(
                'button',
                'kp-action',
                'Показать факты со спойлерами (' + spoilers.length + ')'
            );
            button.type = 'button';
            button.addEventListener('click', function () {
                button.remove();
                spoilers.forEach(function (item) {
                    list.appendChild(createElement(
                        'li',
                        '',
                        String(pick(item, 'text', 'Text') || '')
                    ));
                });
            });
            body.appendChild(button);
        }
    }

    function renderBoxOffice(body, data) {
        var items = asArray(pick(data, 'items', 'Items'));
        if (!items.length) {
            body.appendChild(createElement('div', 'kp-empty', 'Данные о бюджете и сборах отсутствуют.'));
            return;
        }

        items.forEach(function (item) {
            var type = String(pick(item, 'type', 'Type') || '');
            var row = createElement('div', 'kp-row');
            row.append(
                createElement('div', 'kp-row__label', boxOfficeLabels[type] || type || 'Сумма'),
                createElement('div', '', formatMoney(item))
            );
            body.appendChild(row);
        });
    }

    function renderAwards(body, data) {
        var items = asArray(pick(data, 'items', 'Items'));
        if (!items.length) {
            body.appendChild(createElement('div', 'kp-empty', 'Награды и номинации отсутствуют.'));
            return;
        }

        items.slice(0, 50).forEach(function (item) {
            var award = createElement('div', 'kp-award');
            var name = String(pick(item, 'name', 'Name') || 'Награда');
            var nomination = String(pick(item, 'nominationName', 'NominationName') || '');
            var year = Number(pick(item, 'year', 'Year') || 0);
            var win = !!pick(item, 'win', 'Win');
            var heading = createElement(
                'div',
                'kp-award__name' + (win ? ' kp-win' : ''),
                (win ? 'Победа · ' : 'Номинация · ') + name
            );
            award.appendChild(heading);
            award.appendChild(createElement(
                'div',
                'kp-award__meta',
                [nomination, year || ''].filter(Boolean).join(' · ')
            ));
            var persons = asArray(pick(item, 'persons', 'Persons'))
                .map(function (person) {
                    return String(
                        pick(person, 'name', 'Name')
                        || pick(person, 'originalName', 'OriginalName')
                        || ''
                    );
                })
                .filter(Boolean);
            if (persons.length) {
                award.appendChild(createElement('div', 'kp-award__meta', persons.join(', ')));
            }
            body.appendChild(award);
        });
    }

    function findPanelContainer(page) {
        return page.querySelector('.detailPagePrimaryContent')
            || page.querySelector('.itemDetailsGroup')
            || page.querySelector('.detailPageContent');
    }

    function renderPanel(context, core) {
    ensureStyles();
    var itemId = context.itemId;
    var page = context.page;
    var existing = page.querySelector('#kinopoiskEnhancedPresentationPanel');
    var allDates = normalizeReleaseDates(core);
    var primaryDates = selectPrimaryReleaseDates(core);
    renderStandaloneReleaseDates(context, primaryDates, allDates);
    if (existing) {
        if (existing.dataset.itemId === itemId) {
            return;
        }
        existing.remove();
    }

    var container = findPanelContainer(page);
    if (!container) {
        return;
    }

    var kinopoiskId = Number(pick(core, 'kinopoiskId', 'KinopoiskId') || 0);
    var panel = createElement('section', 'kp-presentation');
    panel.id = 'kinopoiskEnhancedPresentationPanel';
    panel.dataset.itemId = itemId;

    var header = createElement('div', 'kp-presentation__header');
    header.appendChild(createElement('div', 'kp-presentation__title', 'КиноПоиск'));
    panel.appendChild(header);

    var ratings = renderRatings(core);
    if (ratings) {
        panel.appendChild(ratings);
    }

    if (primaryDates.length) {
        var dates = createElement('div', 'kp-date-list');
        primaryDates.forEach(function (date) {
            dates.appendChild(createDateChip(date));
        });
        var moreDates = createElement('button', 'kp-action', 'Все даты');
        moreDates.type = 'button';
        moreDates.addEventListener('click', function () {
            showDatesDialog(allDates);
        });
        dates.appendChild(moreDates);
        panel.appendChild(dates);
    }

    var sections = createElement('div', 'kp-sections');
    var professions = renderProfessionDetails(core);
    var relations = renderRelations(core);
    if (professions) {
        sections.appendChild(professions);
    }
    if (relations) {
        sections.appendChild(relations);
    }
    sections.append(
        createLazyDetails('Факты и интересные детали', 'facts', renderFacts, kinopoiskId),
        createLazyDetails('Бюджет и сборы', 'box-office', renderBoxOffice, kinopoiskId),
        createLazyDetails('Награды и номинации', 'awards', renderAwards, kinopoiskId)
    );
    panel.appendChild(sections);
    container.appendChild(panel);
}

    function renderCurrentItem(context) {
    if (!context || !context.isCurrent()) {
        return;
    }
    var itemId = context.itemId;

    fetchCurrentItem(itemId).then(function (item) {
        if (!item || !context.isCurrent()) {
            return;
        }
        var itemType = String(pick(item, 'Type', 'type') || '');
        if (itemType !== 'Movie' && itemType !== 'Series') {
            return;
        }
        var kinopoiskId = getProviderId(item, 'kinopoisk');
        if (!kinopoiskId || !/^\d+$/.test(String(kinopoiskId))) {
            return;
        }

        fetchCore(kinopoiskId).then(function (core) {
            if (context.isCurrent()) {
                renderPanel(context, core);
            }
        }).catch(function (error) {
            console.warn('[КиноПоиск] Расширенная карточка не отображена.', error);
        });
    });
}

    ensureStyles();
    var lifecycle = window.KinopoiskDetailPageLifecycle;
    if (lifecycle) {
        lifecycle.subscribe(renderCurrentItem);
    }
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') {
            closeDatesDialog();
        }
    }, true);
    console.info('[КиноПоиск] Расширенная карточка КиноПоиска зарегистрирована.');
}());
