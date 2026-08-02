(function () {
    'use strict';

    if (window.__kinopoiskEnhancedPresentationInstalled) {
        return;
    }

    window.__kinopoiskEnhancedPresentationInstalled = true;

    var coreCache = new Map();
    var tmdbCache = new Map();
    var supplementalCache = new Map();
    var renderTimer = null;
    var activeDatesDialog = null;
    var lastRenderedItemId = null;

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

    var releaseTypeLabels = {
        WORLD_PREMIER: 'Мировая премьера',
        PREMIERE: 'Премьера',
        COUNTRY_SPECIFIC: 'Прокат в стране',
        ALL: 'Дата релиза'
    };

    var boxOfficeLabels = {
        BUDGET: 'Бюджет',
        RUS: 'Сборы в России',
        USA: 'Сборы в США',
        WORLD: 'Сборы в мире'
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

    function formatVotes(value) {
        var number = Number(value || 0);
        return number > 0
            ? new Intl.NumberFormat('ru-RU').format(number) + ' оценок'
            : '';
    }

    function formatRating(value, percentage) {
        var number = Number(value || 0);
        if (!(number > 0)) {
            return '';
        }
        return percentage
            ? new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 0 }).format(number) + '%'
            : new Intl.NumberFormat('ru-RU', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(number);
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

    function ensureStyles() {
        if (document.getElementById('kinopoiskEnhancedPresentationStyles')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'kinopoiskEnhancedPresentationStyles';
        style.textContent = [
            '.kp-presentation{margin:1.2em 0;padding:1.05em;border:1px solid rgba(255,255,255,.12);border-radius:.75em;background:rgba(12,15,25,.62);backdrop-filter:blur(8px);color:inherit}',
            '.kp-presentation__header{display:flex;align-items:center;gap:.7em;flex-wrap:wrap;margin-bottom:.9em}',
            '.kp-presentation__title{font-size:1.35em;font-weight:700;margin-right:auto}',
            '.kp-links{display:flex;gap:.5em;flex-wrap:wrap}',
            '.kp-link,.kp-action{display:inline-flex;align-items:center;gap:.3em;padding:.42em .68em;border-radius:999px;border:1px solid rgba(255,255,255,.18);background:rgba(255,255,255,.08);color:inherit;text-decoration:none;cursor:pointer;font:inherit}',
            '.kp-link:hover,.kp-action:hover{background:rgba(255,255,255,.16)}',
            '.kp-ratings{display:flex;gap:.65em;flex-wrap:wrap;margin:.65em 0 1em}',
            '.kp-rating{display:flex;align-items:baseline;gap:.4em;padding:.58em .72em;border-radius:.62em;background:rgba(255,255,255,.08)}',
            '.kp-rating__source{font-weight:600;opacity:.86}',
            '.kp-rating__value{font-size:1.28em;font-weight:800}',
            '.kp-rating__votes{font-size:.83em;opacity:.68}',
            '.kp-date-list{display:flex;gap:.55em;flex-wrap:wrap;margin:.65em 0}',
            '.kp-date-chip{display:inline-flex;align-items:center;gap:.38em;padding:.38em .58em;border-radius:.55em;background:rgba(255,255,255,.08)}',
            '.kp-date-chip__label{font-weight:650}',
            '.kp-sections{display:grid;grid-template-columns:repeat(auto-fit,minmax(17em,1fr));gap:.75em;margin-top:.8em}',
            '.kp-details{border:1px solid rgba(255,255,255,.11);border-radius:.62em;background:rgba(255,255,255,.045);overflow:hidden}',
            '.kp-details>summary{cursor:pointer;padding:.72em .82em;font-weight:650;list-style:none}',
            '.kp-details>summary::-webkit-details-marker{display:none}',
            '.kp-details>summary:after{content:"＋";float:right;opacity:.7}',
            '.kp-details[open]>summary:after{content:"−"}',
            '.kp-details__body{padding:0 .82em .82em}',
            '.kp-row{display:grid;grid-template-columns:minmax(7.5em,.36fr) 1fr;gap:.7em;padding:.38em 0;border-top:1px solid rgba(255,255,255,.07)}',
            '.kp-row__label{font-weight:600;opacity:.78}',
            '.kp-person-list{display:flex;gap:.35em;flex-wrap:wrap}',
            '.kp-person{padding:.2em .45em;border-radius:.38em;background:rgba(255,255,255,.07)}',
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
            '.kp-release-enhanced{display:inline-flex;align-items:center;gap:.28em}',
            '.kp-release-enhanced .kp-release-label{font-weight:650}',
            '.kp-release-more{border:0;background:transparent;color:inherit;cursor:pointer;padding:.15em .3em;opacity:.82}',
            '.kp-modal{position:fixed;inset:0;z-index:100001;background:rgba(0,0,0,.8);display:flex;align-items:center;justify-content:center;padding:1em}',
            '.kp-modal__card{width:min(42em,100%);max-height:min(80vh,48em);overflow:auto;border-radius:.8em;background:#151824;color:#fff;padding:1em;box-shadow:0 1.2em 4em rgba(0,0,0,.55)}',
            '.kp-modal__header{display:flex;align-items:center;gap:.7em;margin-bottom:.7em}',
            '.kp-modal__title{font-size:1.25em;font-weight:700;margin-right:auto}',
            '.kp-modal__date{display:grid;grid-template-columns:minmax(9em,1fr) auto;gap:.6em;padding:.6em 0;border-top:1px solid rgba(255,255,255,.1)}',
            '.kp-modal__date-meta{font-size:.85em;opacity:.7}',
            '@media(max-width:600px){.kp-presentation{padding:.8em}.kp-release-label{display:none}.kp-row{grid-template-columns:1fr}.kp-ratings{gap:.4em}.kp-rating{flex:1 1 9em}.kp-modal__date{grid-template-columns:1fr}}'
        ].join('');
        document.head.appendChild(style);
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

    function normalizeCoreReleaseDates(core) {
        return asArray(pick(core, 'releaseDates', 'ReleaseDates')).map(function (item) {
            var type = String(pick(item, 'type', 'Type') || '');
            var country = String(pick(item, 'country', 'Country') || '');
            return {
                key: 'kp|' + type + '|' + String(pick(item, 'date', 'Date') || '') + '|' + country,
                kind: 'kinopoisk',
                label: releaseTypeLabels[type] || 'Дата релиза',
                date: String(pick(item, 'date', 'Date') || ''),
                country: country,
                source: 'КиноПоиск',
                icon: type === 'WORLD_PREMIER' ? 'public' : 'event',
                reRelease: !!pick(item, 'reRelease', 'ReRelease')
            };
        }).filter(function (item) { return item.date; });
    }

    function getPreferredRegion() {
        var enhanced = window.JellyfinEnhanced;
        return String(
            enhanced && enhanced.pluginConfig && enhanced.pluginConfig.DEFAULT_REGION
                ? enhanced.pluginConfig.DEFAULT_REGION
                : 'RU'
        ).toUpperCase();
    }

    function earliestOfBucket(releaseDates, types) {
        var matches = asArray(releaseDates).filter(function (item) {
            return types.indexOf(Number(item.type)) >= 0 && item.release_date;
        });
        if (!matches.length) {
            return null;
        }
        matches.sort(function (left, right) {
            return String(left.release_date).localeCompare(String(right.release_date));
        });
        return matches[0];
    }

    function resolveTmdbBuckets(results) {
        var region = getPreferredRegion();
        var preferred = [region, 'US'].filter(function (value, index, array) {
            return value && array.indexOf(value) === index;
        });
        var buckets = [
            { kind: 'cinema', label: 'Кино', types: [1, 2, 3], icon: 'local_movies' },
            { kind: 'digital', label: 'Цифра', types: [4], icon: 'ondemand_video' },
            { kind: 'physical', label: 'Носитель', types: [5], icon: 'album' }
        ];

        return buckets.map(function (bucket) {
            var selected = null;
            var selectedCountry = '';
            for (var preferredIndex = 0; preferredIndex < preferred.length && !selected; preferredIndex++) {
                var entry = results.find(function (candidate) {
                    return candidate.iso_3166_1 === preferred[preferredIndex];
                });
                selected = entry && earliestOfBucket(entry.release_dates, bucket.types);
                selectedCountry = selected ? preferred[preferredIndex] : '';
            }
            if (!selected) {
                for (var resultIndex = 0; resultIndex < results.length && !selected; resultIndex++) {
                    selected = earliestOfBucket(results[resultIndex].release_dates, bucket.types);
                    selectedCountry = selected ? results[resultIndex].iso_3166_1 : '';
                }
            }
            return selected ? {
                key: 'tmdb|' + bucket.kind + '|' + selected.release_date + '|' + selectedCountry,
                kind: bucket.kind,
                label: bucket.label,
                date: selected.release_date,
                country: selectedCountry,
                source: 'TMDB',
                icon: bucket.icon,
                reRelease: false
            } : null;
        }).filter(Boolean);
    }

    function fetchTmdbReleaseDates(item) {
        var tmdbId = getProviderId(item, 'tmdb');
        if (!tmdbId) {
            return Promise.resolve([]);
        }

        var key = String(tmdbId);
        if (!tmdbCache.has(key)) {
            tmdbCache.set(
                key,
                fetchJson('/JellyfinEnhanced/tmdb/movie/' + encodeURIComponent(key) + '/release_dates')
                    .then(function (data) {
                        return resolveTmdbBuckets(asArray(data && data.results));
                    })
                    .catch(function (error) {
                        console.warn('[КиноПоиск] Даты TMDB не загружены.', error);
                        return [];
                    })
            );
        }
        return tmdbCache.get(key);
    }

    function createDateChip(item, includeSource) {
        var chip = createElement('span', 'kp-date-chip');
        chip.title = item.label
            + (item.country ? ' · ' + item.country : '')
            + (includeSource ? ' · ' + item.source : '');
        chip.append(
            createElement('span', 'material-icons', item.icon || 'event'),
            createElement('span', 'kp-date-chip__label', item.label),
            createElement('span', '', formatDate(item.date))
        );
        return chip;
    }

    function deduplicateDates(items) {
        var seen = new Set();
        return items.filter(function (item) {
            var key = item.key || [item.label, item.date, item.country, item.source].join('|');
            if (seen.has(key)) {
                return false;
            }
            seen.add(key);
            return true;
        }).sort(function (left, right) {
            return String(left.date).localeCompare(String(right.date));
        });
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
        var title = createElement('div', 'kp-modal__title', 'Все даты релиза');
        var close = createElement('button', 'kp-action', 'Закрыть');
        close.type = 'button';
        close.addEventListener('click', closeDatesDialog);
        header.append(title, close);
        card.appendChild(header);

        if (!items.length) {
            card.appendChild(createElement('div', 'kp-empty', 'Даты релиза не найдены.'));
        } else {
            items.forEach(function (item) {
                var row = createElement('div', 'kp-modal__date');
                var left = createElement('div');
                left.append(
                    createElement('div', '', item.label),
                    createElement(
                        'div',
                        'kp-modal__date-meta',
                        [item.country || 'Страна не указана', item.source, item.reRelease ? 'повторный прокат' : '']
                            .filter(Boolean)
                            .join(' · ')
                    )
                );
                row.append(left, createElement('div', '', formatDate(item.date)));
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

    function enhanceReleaseChip(tmdbDates, allDates) {
        var chip = document.querySelector('.mediaInfoItem-releaseDate');
        if (!chip || chip.dataset.kinopoiskEnhanced === 'true') {
            return;
        }

        if (!tmdbDates.length) {
            return;
        }

        chip.dataset.kinopoiskEnhanced = 'true';
        chip.textContent = '';
        chip.style.display = 'flex';
        chip.style.alignItems = 'center';
        chip.style.gap = '.55em';
        chip.style.flexWrap = 'wrap';

        tmdbDates.forEach(function (item) {
            var span = createElement('span', 'kp-release-enhanced');
            span.title = item.label + ' · ' + (item.country || 'страна не указана') + ' · TMDB';
            span.append(
                createElement('span', 'material-icons', item.icon),
                createElement('span', 'kp-release-label', item.label),
                createElement('span', '', formatDate(item.date))
            );
            chip.appendChild(span);
        });

        var more = createElement('button', 'kp-release-more material-icons', 'calendar_month');
        more.type = 'button';
        more.title = 'Показать все даты релиза';
        more.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            showDatesDialog(allDates);
        });
        chip.appendChild(more);
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
            row.appendChild(createElement('div', 'kp-row__label', label));
            var list = createElement('div', 'kp-person-list');
            people.slice(0, key === 'ACTOR' ? 20 : 12).forEach(function (person) {
                var name = String(pick(person, 'name', 'Name') || pick(person, 'originalName', 'OriginalName') || '');
                if (name) {
                    list.appendChild(createElement('span', 'kp-person', name));
                }
            });
            if (people.length > (key === 'ACTOR' ? 20 : 12)) {
                list.appendChild(createElement('span', 'kp-person', 'ещё ' + String(people.length - (key === 'ACTOR' ? 20 : 12))));
            }
            row.appendChild(list);
            body.appendChild(row);
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
                image.src = imageUrl;
                image.alt = '';
                image.loading = 'lazy';
                image.referrerPolicy = 'no-referrer';
                card.appendChild(image);
            }
            var name = String(pick(relation, 'name', 'Name') || pick(relation, 'originalName', 'OriginalName') || 'Связанный фильм');
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

        var visible = items.filter(function (item) { return !pick(item, 'spoiler', 'Spoiler'); });
        var spoilers = items.filter(function (item) { return !!pick(item, 'spoiler', 'Spoiler'); });
        var list = createElement('ul', 'kp-list');
        visible.forEach(function (item) {
            var type = String(pick(item, 'type', 'Type') || 'FACT');
            var prefix = type === 'BLOOPER' ? 'Ошибка: ' : '';
            list.appendChild(createElement('li', '', prefix + String(pick(item, 'text', 'Text') || '')));
        });
        body.appendChild(list);

        if (spoilers.length) {
            var button = createElement('button', 'kp-action', 'Показать факты со спойлерами (' + spoilers.length + ')');
            button.type = 'button';
            button.addEventListener('click', function () {
                button.remove();
                spoilers.forEach(function (item) {
                    list.appendChild(createElement('li', '', String(pick(item, 'text', 'Text') || '')));
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
            var heading = createElement('div', 'kp-award__name' + (win ? ' kp-win' : ''), (win ? 'Победа · ' : 'Номинация · ') + name);
            award.appendChild(heading);
            award.appendChild(createElement('div', 'kp-award__meta', [nomination, year || ''].filter(Boolean).join(' · ')));
            var persons = asArray(pick(item, 'persons', 'Persons'))
                .map(function (person) {
                    return String(pick(person, 'name', 'Name') || pick(person, 'originalName', 'OriginalName') || '');
                })
                .filter(Boolean);
            if (persons.length) {
                award.appendChild(createElement('div', 'kp-award__meta', persons.join(', ')));
            }
            body.appendChild(award);
        });
    }

    function findPanelContainer() {
        return document.querySelector('.detailPagePrimaryContent')
            || document.querySelector('.itemDetailsGroup')
            || document.querySelector('.detailPageContent');
    }

    function renderPanel(itemId, item, core, tmdbDates) {
        ensureStyles();
        var existing = document.getElementById('kinopoiskEnhancedPresentationPanel');
        if (existing) {
            if (existing.dataset.itemId === itemId) {
                enhanceReleaseChip(tmdbDates, deduplicateDates(tmdbDates.concat(normalizeCoreReleaseDates(core))));
                return;
            }
            existing.remove();
        }

        var container = findPanelContainer();
        if (!container) {
            return;
        }

        var kinopoiskId = Number(pick(core, 'kinopoiskId', 'KinopoiskId') || 0);
        var panel = createElement('section', 'kp-presentation');
        panel.id = 'kinopoiskEnhancedPresentationPanel';
        panel.dataset.itemId = itemId;

        var header = createElement('div', 'kp-presentation__header');
        header.appendChild(createElement('div', 'kp-presentation__title', 'КиноПоиск'));
        var links = createElement('div', 'kp-links');
        var kinopoiskLink = createSafeLink(
            String(pick(core, 'kinopoiskUrl', 'KinopoiskUrl') || ''),
            'КиноПоиск ↗'
        );
        var imdbLink = createSafeLink(
            String(pick(core, 'imdbUrl', 'ImdbUrl') || ''),
            'IMDb ↗'
        );
        if (kinopoiskLink) {
            links.appendChild(kinopoiskLink);
        }
        if (imdbLink) {
            links.appendChild(imdbLink);
        }
        header.appendChild(links);
        panel.appendChild(header);

        var ratings = renderRatings(core);
        if (ratings) {
            panel.appendChild(ratings);
        }

        var allDates = deduplicateDates(tmdbDates.concat(normalizeCoreReleaseDates(core)));
        if (allDates.length) {
            var dates = createElement('div', 'kp-date-list');
            allDates.slice(0, 5).forEach(function (date) {
                dates.appendChild(createDateChip(date, true));
            });
            var moreDates = createElement('button', 'kp-action', 'Все даты');
            moreDates.type = 'button';
            moreDates.addEventListener('click', function () { showDatesDialog(allDates); });
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
        lastRenderedItemId = itemId;

        enhanceReleaseChip(tmdbDates, allDates);
    }

    function renderCurrentItem() {
        var itemId = getCurrentItemId();
        if (!itemId) {
            return;
        }

        fetchCurrentItem(itemId).then(function (item) {
            if (!item || getCurrentItemId() !== itemId) {
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

            Promise.all([
                fetchCore(kinopoiskId),
                itemType === 'Movie' ? fetchTmdbReleaseDates(item) : Promise.resolve([])
            ]).then(function (values) {
                if (getCurrentItemId() !== itemId) {
                    return;
                }
                renderPanel(itemId, item, values[0], values[1]);
            }).catch(function (error) {
                console.warn('[КиноПоиск] Расширенная карточка не отображена.', error);
            });
        });
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentItem, 250);
    }

    ensureStyles();
    var observer = new MutationObserver(scheduleRender);
    observer.observe(document.documentElement, {
        childList: true,
        subtree: true
    });
    window.addEventListener('hashchange', scheduleRender);
    window.addEventListener('popstate', scheduleRender);
    document.addEventListener('viewshow', scheduleRender, true);
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') {
            closeDatesDialog();
        }
    }, true);
    scheduleRender();
    console.info('[КиноПоиск] Расширенная карточка КиноПоиска зарегистрирована.');
}());
