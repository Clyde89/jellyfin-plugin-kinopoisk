(function () {
    'use strict';

    if (window.__kinopoiskRecommendationsInstalled) {
        return;
    }

    window.__kinopoiskRecommendationsInstalled = true;

    var dataCache = new Map();
    var renderTimer = null;

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
            var itemId = new URLSearchParams(candidates[index]).get('id');
            if (itemId) {
                return itemId;
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
                console.warn('[КиноПоиск] Карточка для рекомендаций не загружена.', error);
                return null;
            });
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
        if (document.getElementById('kinopoiskRecommendationsStyles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'kinopoiskRecommendationsStyles';
        style.textContent = [
            '.kp-recommendations-section{margin-top:1.4em}',
            '.kp-recommendations-header{display:flex;align-items:center;gap:.8em;flex-wrap:wrap;padding-right:1em}',
            '.kp-recommendations-title{margin-right:auto}',
            '.kp-recommendations-tabs{display:flex;gap:.45em;flex-wrap:wrap}',
            '.kp-recommendations-tab{border:1px solid rgba(255,255,255,.16);border-radius:999px;background:rgba(255,255,255,.06);color:inherit;padding:.4em .72em;cursor:pointer;font:inherit}',
            '.kp-recommendations-tab.is-active{background:rgba(255,255,255,.18);border-color:rgba(255,255,255,.34)}',
            '.kp-recommendations-status{padding:1em;opacity:.72}',
            '.kp-recommendations-scroller{overflow-x:auto;padding:.7em 0 1em;scrollbar-width:thin}',
            '.kp-recommendations-items{display:flex;gap:.75em;padding:.25em 1em .5em .25em}',
            '.kp-similar-card{width:9.5em;flex:0 0 9.5em;color:inherit;text-decoration:none;position:relative}',
            '.kp-similar-card img{display:block;width:100%;aspect-ratio:2/3;object-fit:cover;border-radius:.58em;background:rgba(255,255,255,.06)}',
            '.kp-similar-card__name{display:block;margin-top:.45em;font-size:.92em;line-height:1.25;white-space:normal}',
            '.kp-similar-card__source{display:inline-flex;margin-top:.3em;padding:.18em .42em;border-radius:.35em;background:rgba(255,255,255,.08);font-size:.76em;opacity:.78}',
            '.kp-recommendations-items>.card{flex:0 0 auto}',
            '@media(max-width:600px){.kp-recommendations-header{align-items:flex-start}.kp-recommendations-tabs{width:100%}.kp-recommendations-tab{flex:1 1 auto}}'
        ].join('');
        document.head.appendChild(style);
    }

    function getVisiblePage() {
        return document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage:not(.hide)')
            || document.querySelector('.libraryPage:not(.hide)')
            || document;
    }

    function findInsertionAnchor(page) {
        return page.querySelector('#similarCollapsible')
            || page.querySelector('.similarCollapsible');
    }

    function removeLegacySeerrSections(page) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.jellyseerr-details-section'),
            function (section) { section.remove(); }
        );
    }

    function fetchKinopoiskItems(kinopoiskId) {
        var key = 'kp:' + kinopoiskId;
        if (!dataCache.has(key)) {
            dataCache.set(
                key,
                fetchJson(
                    '/KinopoiskPresentation/'
                    + encodeURIComponent(String(kinopoiskId))
                    + '/similars'
                ).then(function (data) {
                    return asArray(pick(data, 'items', 'Items'));
                }).catch(function (error) {
                    dataCache.delete(key);
                    throw error;
                })
            );
        }
        return dataCache.get(key);
    }

    function resolveTmdbId(item) {
        var saved = String(getProviderId(item, 'tmdb') || '').trim();
        if (/^\d+$/.test(saved)) {
            return Promise.resolve(saved);
        }
        var imdbId = String(getProviderId(item, 'imdb') || '').trim();
        var itemType = String(pick(item, 'Type', 'type') || '');
        var resolver = window.KinopoiskTmdbResolver;
        if (!/^tt\d+$/.test(imdbId)
            || !resolver
            || typeof resolver.resolve !== 'function') {
            return Promise.resolve(null);
        }
        return resolver.resolve(imdbId, itemType);
    }

    function fetchExtendedItems(item) {
        var enhanced = window.JellyfinEnhanced;
        if (!enhanced
            || !enhanced.jellyseerrAPI
            || !enhanced.jellyseerrUI
            || !enhanced.pluginConfig) {
            return Promise.resolve({ items: [], reason: 'Интеграция Jellyfin Enhanced недоступна.' });
        }

        var showSimilar = enhanced.pluginConfig.JellyseerrShowSimilar === true;
        var showRecommended = enhanced.pluginConfig.JellyseerrShowRecommended === true;
        if (!showSimilar && !showRecommended) {
            return Promise.resolve({
                items: [],
                reason: 'Похожие фильмы и рекомендации отключены в настройках Jellyfin Enhanced.'
            });
        }

        var itemId = String(pick(item, 'Id', 'id') || '');
        var key = 'extended:' + itemId;
        if (!dataCache.has(key)) {
            dataCache.set(key, Promise.all([
                enhanced.jellyseerrAPI.checkUserStatus(),
                resolveTmdbId(item)
            ]).then(function (values) {
                var status = values[0];
                var tmdbId = values[1];
                if (!status || !status.active || !tmdbId) {
                    return { items: [], reason: 'Расширенная подборка временно недоступна.' };
                }

                var itemType = String(pick(item, 'Type', 'type') || '');
                var mediaType = itemType === 'Series' ? 'tv' : 'movie';
                var promises = [];
                if (showSimilar) {
                    promises.push(
                        mediaType === 'movie'
                            ? enhanced.jellyseerrAPI.fetchSimilarMovies(Number(tmdbId), {})
                            : enhanced.jellyseerrAPI.fetchSimilarTvShows(Number(tmdbId), {})
                    );
                } else {
                    promises.push(Promise.resolve({ results: [] }));
                }
                if (showRecommended) {
                    promises.push(
                        mediaType === 'movie'
                            ? enhanced.jellyseerrAPI.fetchRecommendedMovies(Number(tmdbId), {})
                            : enhanced.jellyseerrAPI.fetchRecommendedTvShows(Number(tmdbId), {})
                    );
                } else {
                    promises.push(Promise.resolve({ results: [] }));
                }

                return Promise.all(promises).then(function (responses) {
                    var combined = asArray(responses[1] && responses[1].results)
                        .concat(asArray(responses[0] && responses[0].results));
                    var seen = new Set();
                    var items = combined.filter(function (candidate) {
                        var candidateType = String(candidate.mediaType || candidate.media_type || mediaType);
                        var candidateId = String(candidate.id || candidate.mediaId || '');
                        var candidateKey = candidateType + ':' + candidateId;
                        if (!candidateId || candidateId === String(tmdbId) || seen.has(candidateKey)) {
                            return false;
                        }
                        seen.add(candidateKey);
                        return true;
                    }).slice(0, 20);
                    return { items: items, reason: '' };
                });
            }).catch(function (error) {
                dataCache.delete(key);
                console.warn('[КиноПоиск] Расширенная подборка не загружена.', error);
                return { items: [], reason: 'Расширенная подборка временно недоступна.' };
            }));
        }
        return dataCache.get(key);
    }

    function createKinopoiskCard(item) {
        var kinopoiskId = String(pick(item, 'kinopoiskId', 'KinopoiskId') || '');
        if (!/^\d+$/.test(kinopoiskId)) {
            return null;
        }
        var link = document.createElement('a');
        link.className = 'kp-similar-card';
        link.href = 'https://www.kinopoisk.ru/film/' + kinopoiskId + '/';
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.referrerPolicy = 'strict-origin-when-cross-origin';

        var imageUrl = normalizeImageUrl(
            pick(item, 'posterUrlPreview', 'PosterUrlPreview')
            || pick(item, 'posterUrl', 'PosterUrl')
        );
        if (imageUrl) {
            var image = document.createElement('img');
            image.src = imageUrl;
            image.alt = '';
            image.loading = 'lazy';
            image.referrerPolicy = 'no-referrer';
            link.appendChild(image);
        }

        var name = String(
            pick(item, 'name', 'Name')
            || pick(item, 'originalName', 'OriginalName')
            || 'Похожий фильм'
        );
        link.append(
            createElement('span', 'kp-similar-card__name', name),
            createElement('span', 'kp-similar-card__source', 'КиноПоиск')
        );
        return link;
    }

    function renderKinopoisk(container, items) {
        if (!items.length) {
            container.appendChild(createElement(
                'div',
                'kp-recommendations-status',
                'Похожие фильмы КиноПоиска отсутствуют.'
            ));
            return;
        }
        var scroller = createElement('div', 'kp-recommendations-scroller');
        var list = createElement('div', 'kp-recommendations-items');
        items.forEach(function (item) {
            var card = createKinopoiskCard(item);
            if (card) {
                list.appendChild(card);
            }
        });
        scroller.appendChild(list);
        container.appendChild(scroller);
    }

    function renderExtended(container, result) {
        var items = asArray(result && result.items);
        if (!items.length) {
            container.appendChild(createElement(
                'div',
                'kp-recommendations-status',
                result && result.reason
                    ? result.reason
                    : 'Расширенная подборка не найдена.'
            ));
            return;
        }

        var enhanced = window.JellyfinEnhanced;
        var scroller = createElement('div', 'kp-recommendations-scroller');
        var list = createElement('div', 'kp-recommendations-items');
        items.forEach(function (item) {
            var card = enhanced.jellyseerrUI.createJellyseerrCard(item, true, true);
            if (card) {
                list.appendChild(card);
            }
        });
        scroller.appendChild(list);
        container.appendChild(scroller);
    }

    function createSection(itemId, item, kinopoiskId) {
        var section = createElement(
            'section',
            'verticalSection emby-scroller-container kp-recommendations-section'
        );
        section.id = 'kinopoiskRecommendationsSection';
        section.dataset.itemId = itemId;

        var header = createElement('div', 'kp-recommendations-header');
        header.appendChild(createElement(
            'h2',
            'sectionTitle sectionTitle-cards focuscontainer-x padded-right kp-recommendations-title',
            'Похожие и рекомендации'
        ));
        var tabs = createElement('div', 'kp-recommendations-tabs');
        var content = createElement('div', 'kp-recommendations-content');
        var activeSource = 'kinopoisk';
        var loaded = Object.create(null);

        var sources = [
            { key: 'kinopoisk', label: 'КиноПоиск' },
            { key: 'extended', label: 'Расширенная подборка' }
        ];

        function setActive(source) {
            activeSource = source;
            Array.prototype.forEach.call(
                tabs.querySelectorAll('.kp-recommendations-tab'),
                function (button) {
                    var active = button.dataset.source === source;
                    button.classList.toggle('is-active', active);
                    button.setAttribute('aria-selected', active ? 'true' : 'false');
                }
            );
            content.textContent = '';
            content.appendChild(createElement('div', 'kp-recommendations-status', 'Загрузка…'));

            if (loaded[source]) {
                content.textContent = '';
                if (source === 'kinopoisk') {
                    renderKinopoisk(content, loaded[source]);
                } else {
                    renderExtended(content, loaded[source]);
                }
                return;
            }

            var request = source === 'kinopoisk'
                ? fetchKinopoiskItems(kinopoiskId)
                : fetchExtendedItems(item);
            request.then(function (result) {
                loaded[source] = result;
                if (activeSource !== source || getCurrentItemId() !== itemId) {
                    return;
                }
                content.textContent = '';
                if (source === 'kinopoisk') {
                    renderKinopoisk(content, result);
                } else {
                    renderExtended(content, result);
                }
            }).catch(function (error) {
                console.warn('[КиноПоиск] Источник рекомендаций не загружен.', error);
                if (activeSource === source) {
                    content.textContent = '';
                    content.appendChild(createElement(
                        'div',
                        'kp-recommendations-status',
                        'Источник рекомендаций временно недоступен.'
                    ));
                }
            });
        }

        sources.forEach(function (source) {
            var button = createElement('button', 'kp-recommendations-tab', source.label);
            button.type = 'button';
            button.dataset.source = source.key;
            button.setAttribute('role', 'tab');
            button.addEventListener('click', function () {
                setActive(source.key);
            });
            tabs.appendChild(button);
        });

        header.appendChild(tabs);
        section.append(header, content);
        setActive('kinopoisk');
        return section;
    }

    function renderCurrentItem() {
        ensureStyles();
        var itemId = getCurrentItemId();
        if (!itemId) {
            return;
        }
        var page = getVisiblePage();
        removeLegacySeerrSections(page);

        var existing = page.querySelector('#kinopoiskRecommendationsSection');
        if (existing && existing.dataset.itemId === itemId) {
            return;
        }
        if (existing) {
            existing.remove();
        }

        fetchCurrentItem(itemId).then(function (item) {
            if (!item || getCurrentItemId() !== itemId) {
                return;
            }
            var itemType = String(pick(item, 'Type', 'type') || '');
            if (itemType !== 'Movie' && itemType !== 'Series') {
                return;
            }
            var kinopoiskId = String(getProviderId(item, 'kinopoisk') || '');
            if (!/^\d+$/.test(kinopoiskId)) {
                return;
            }

            var anchor = findInsertionAnchor(page);
            if (!anchor || !anchor.parentNode) {
                return;
            }
            removeLegacySeerrSections(page);
            anchor.parentNode.insertBefore(
                createSection(itemId, item, kinopoiskId),
                anchor.nextSibling
            );
        });
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentItem, 350);
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
    scheduleRender();
    console.info('[КиноПоиск] Объединённый блок рекомендаций зарегистрирован.');
}());
