(function () {
    'use strict';

    if (window.__kinopoiskRecommendationsInstalled) {
        return;
    }

    window.__kinopoiskRecommendationsInstalled = true;

    var dataCache = new Map();
    var currentItemCache = new Map();
    var renderTimer = null;
    var retryTimers = [];
    var renderGeneration = 0;
    var activeItemId = null;
    var activeLifecycleGeneration = 0;
    var retryDelays = [0, 120, 280, 520, 900, 1450, 2200, 3400, 5200, 8000];

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
    if (!currentItemCache.has(itemId)) {
        var request = apiClient
            .getItem(apiClient.getCurrentUserId(), itemId)
            .catch(function (error) {
                currentItemCache.delete(itemId);
                console.warn('[КиноПоиск] Карточка для рекомендаций не загружена.', error);
                return null;
            });
        currentItemCache.set(itemId, request);
        if (currentItemCache.size > 50) {
            currentItemCache.delete(currentItemCache.keys().next().value);
        }
    }
    return currentItemCache.get(itemId);
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
            '.kp-recommendations-tab{border:1px solid rgba(255,255,255,.16)!important;border-radius:999px!important;background:rgba(255,255,255,.06)!important;color:inherit!important;padding:.4em .72em!important;cursor:pointer;font:inherit;min-width:0}',
            '.kp-recommendations-tab.is-active{background:rgba(255,255,255,.18)!important;border-color:rgba(255,255,255,.34)!important}',
            '.kp-recommendations-status{padding:1em;opacity:.72}',
            '.kp-recommendations-scroller{overflow-x:auto;padding:.7em 0 1em;scrollbar-width:thin}',
            '.kp-recommendations-items{display:flex;gap:1em;padding:.25em 1em .5em .25em}',
            '.kp-recommendations-items>.card{width:var(--kp-native-card-width);flex:0 0 var(--kp-native-card-width)}',
            '.kp-similar-card .cardImageContainer{background-position:center;background-size:cover}',
            '.kp-similar-card__source{font-size:.86em;opacity:.86;white-space:nowrap}',
            '.kp-similar-card__rating{display:flex;align-items:center;gap:.25em;color:#bdbdbd;white-space:nowrap}',
            '.kp-similar-card__star{color:#ffd700;font-size:1.15em;line-height:1}',
            '.kp-similar-card__fallback-button{opacity:.65;cursor:not-allowed}',
            '@media(max-width:600px){.kp-recommendations-header{align-items:flex-start}.kp-recommendations-tabs{width:100%}.kp-recommendations-tab{flex:1 1 auto}}'
        ].join('');
        document.head.appendChild(style);
    }

    function getVisiblePage() {
    var lifecycle = window.KinopoiskDetailPageLifecycle;
    if (lifecycle) {
        return lifecycle.getActivePage();
    }
    var pages = document.querySelectorAll('#itemDetailPage, .itemDetailPage');
    for (var index = 0; index < pages.length; index++) {
        var page = pages[index];
        if (!page.isConnected || page.hidden || page.classList.contains('hide')) {
            continue;
        }
        var style = window.getComputedStyle(page);
        if (style.display !== 'none' && style.visibility !== 'hidden') {
            return page;
        }
    }
    return null;
}

    function applyNativeCardWidth(page, section) {
        var preferred = page.querySelector(
            '#similarCollapsible .card:not(.kp-similar-card),'
            + '.similarCollapsible .card:not(.kp-similar-card)'
        );
        var cards = preferred ? [preferred] : page.querySelectorAll('.card');
        for (var index = 0; index < cards.length; index++) {
            var card = cards[index];
            if (section.contains(card) || card.classList.contains('kp-similar-card')) {
                continue;
            }
            var width = card.getBoundingClientRect().width;
            if (width >= 80 && width <= 600) {
                section.style.setProperty('--kp-native-card-width', width.toFixed(2) + 'px');
                return;
            }
        }
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

    function getSeerrStatus() {
        var key = 'seerr:user-status';
        if (!dataCache.has(key)) {
            var enhanced = window.JellyfinEnhanced;
            var request = enhanced
                && enhanced.jellyseerrAPI
                && typeof enhanced.jellyseerrAPI.checkUserStatus === 'function'
                ? enhanced.jellyseerrAPI.checkUserStatus()
                : Promise.resolve({ active: false, userFound: false });
            dataCache.set(key, Promise.resolve(request).catch(function () {
                return { active: false, userFound: false };
            }));
        }
        return dataCache.get(key);
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
                    return { items: [], reason: 'Подборка The Movie Database временно недоступна.' };
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
                console.warn('[КиноПоиск] Подборка The Movie Database не загружена.', error);
                return { items: [], reason: 'Подборка The Movie Database временно недоступна.' };
            }));
        }
        return dataCache.get(key);
    }

    function getKinopoiskCardData(item) {
        var kinopoiskId = String(pick(item, 'kinopoiskId', 'KinopoiskId') || '');
        var imdbId = String(pick(item, 'imdbId', 'ImdbId') || '').trim();
        var mediaType = String(pick(item, 'mediaType', 'MediaType') || 'movie') === 'tv'
            ? 'tv'
            : 'movie';
        var key = 'kp-card-data:' + kinopoiskId;
        if (!/^\d+$/.test(kinopoiskId)
            || !/^tt\d+$/.test(imdbId)
            || dataCache.has(key)) {
            return dataCache.get(key) || Promise.resolve(null);
        }

        var enhanced = window.JellyfinEnhanced;
        var resolver = window.KinopoiskTmdbResolver;
        if (!enhanced
            || !enhanced.jellyseerrAPI
            || !enhanced.jellyseerrUI
            || typeof enhanced.jellyseerrUI.createJellyseerrCard !== 'function'
            || !resolver
            || typeof resolver.resolve !== 'function') {
            return Promise.resolve(null);
        }

        var itemType = mediaType === 'tv' ? 'Series' : 'Movie';
        var request = Promise.all([
            getSeerrStatus(),
            resolver.resolve(imdbId, itemType)
        ]).then(function (values) {
            var status = values[0] || { active: false, userFound: false };
            var tmdbId = String(values[1] || '');
            if (!/^\d+$/.test(tmdbId)) {
                return null;
            }

            var detailsRequest = Promise.resolve(null);
            if (mediaType === 'movie'
                && typeof enhanced.jellyseerrAPI.fetchMovieDetails === 'function') {
                detailsRequest = enhanced.jellyseerrAPI.fetchMovieDetails(Number(tmdbId));
            } else if (mediaType === 'tv'
                && typeof enhanced.jellyseerrAPI.fetchTvShowDetails === 'function') {
                detailsRequest = enhanced.jellyseerrAPI.fetchTvShowDetails(Number(tmdbId));
            }

            return Promise.resolve(detailsRequest).catch(function () {
                return null;
            }).then(function (details) {
                var name = String(
                    pick(item, 'name', 'Name')
                    || pick(item, 'originalName', 'OriginalName')
                    || 'Похожий фильм'
                );
                var year = Number(pick(item, 'year', 'Year') || 0);
                var rating = Number(pick(item, 'ratingKinopoisk', 'RatingKinopoisk') || 0);
                var overview = String(pick(item, 'overview', 'Overview') || '');
                var result = Object.assign({}, details || {});
                result.id = Number(tmdbId);
                result.mediaType = mediaType;
                result.title = name;
                result.name = name;
                result.overview = overview || result.overview || '';
                result.voteAverage = rating > 0 ? rating : (result.voteAverage || 0);
                if (mediaType === 'tv') {
                    result.firstAirDate = year > 0
                        ? String(year) + '-01-01'
                        : (result.firstAirDate || '');
                } else {
                    result.releaseDate = year > 0
                        ? String(year) + '-01-01'
                        : (result.releaseDate || '');
                }
                return {
                    item: result,
                    active: status.active === true,
                    userFound: status.userFound === true
                };
            });
        }).catch(function (error) {
            console.debug('[КиноПоиск] Карточка не сопоставлена с Seerr.', error);
            return null;
        });

        dataCache.set(key, request);
        return request;
    }

    function appendKinopoiskMeta(container, item) {
        container.textContent = '';
        var source = createElement('span', 'kp-similar-card__source', 'КиноПоиск');
        var year = Number(pick(item, 'year', 'Year') || 0);
        var rating = Number(pick(item, 'ratingKinopoisk', 'RatingKinopoisk') || 0);
        container.appendChild(source);
        if (year > 0) {
            container.appendChild(createElement('bdi', '', String(year)));
        }
        if (rating > 0) {
            var ratingNode = createElement('span', 'kp-similar-card__rating');
            ratingNode.append(
                createElement('span', 'kp-similar-card__star', '★'),
                createElement('span', '', rating.toFixed(1))
            );
            container.appendChild(ratingNode);
        }
    }

    function addFallbackOverview(cardScalable, item) {
        var overview = null;
        function removeOverview() {
            if (overview && overview.parentNode) {
                overview.remove();
            }
            overview = null;
        }
        function createOverview() {
            if (overview) {
                return;
            }
            overview = createElement('div', 'jellyseerr-overview');
            var description = String(
                pick(item, 'overview', 'Overview')
                || 'Краткое описание отсутствует.'
            );
            overview.appendChild(createElement('div', 'content', description.slice(0, 500)));
            var button = createElement(
                'button',
                'jellyseerr-request-button jellyseerr-button-offline kp-similar-card__fallback-button',
                'Недоступно в Seerr'
            );
            button.type = 'button';
            button.disabled = true;
            overview.appendChild(button);
            cardScalable.appendChild(overview);
        }
        cardScalable.addEventListener('mouseenter', createOverview);
        cardScalable.addEventListener('mouseleave', removeOverview);
        cardScalable.setAttribute('tabindex', '0');
        cardScalable.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                if (overview) {
                    removeOverview();
                } else {
                    createOverview();
                }
            }
        });
    }

    function createKinopoiskFallbackCard(item) {
        var kinopoiskId = String(pick(item, 'kinopoiskId', 'KinopoiskId') || '');
        if (!/^\d+$/.test(kinopoiskId)) {
            return null;
        }

        var name = String(
            pick(item, 'name', 'Name')
            || pick(item, 'originalName', 'OriginalName')
            || 'Похожий фильм'
        );
        var mediaType = String(pick(item, 'mediaType', 'MediaType') || 'movie') === 'tv'
            ? 'tv'
            : 'movie';
        var imageUrl = normalizeImageUrl(
            pick(item, 'posterUrl', 'PosterUrl')
            || pick(item, 'posterUrlPreview', 'PosterUrlPreview')
        );
        var card = createElement(
            'div',
            'card overflowPortraitCard card-hoverable card-withuserdata jellyseerr-card kp-similar-card'
        );
        card.dataset.kinopoiskId = kinopoiskId;
        var box = createElement('div', 'cardBox cardBox-bottompadded');
        var scalable = createElement('div', 'cardScalable');
        scalable.appendChild(createElement('div', 'cardPadder cardPadder-overflowPortrait'));
        var image = createElement('div', 'cardImageContainer coveredImage cardContent');
        if (imageUrl) {
            var lifecycle = window.KinopoiskDetailPageLifecycle;
            if (lifecycle) {
                lifecycle.applyProtectedImage(image, imageUrl, 'background');
            }
        }
        var badge = createElement(
            'span',
            'jellyseerr-media-badge '
                + (mediaType === 'tv'
                    ? 'jellyseerr-media-badge-series'
                    : 'jellyseerr-media-badge-movie'),
            mediaType === 'tv' ? 'СЕРИАЛ' : 'ФИЛЬМ'
        );
        image.appendChild(badge);
        scalable.appendChild(image);
        addFallbackOverview(scalable, item);
        box.appendChild(scalable);

        var title = createElement('div', 'cardText cardTextCentered cardText-first');
        var link = createElement('a', 'jellyseerr-more-info-link', name);
        link.href = 'https://www.kinopoisk.ru/film/' + kinopoiskId + '/';
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.referrerPolicy = 'strict-origin-when-cross-origin';
        title.appendChild(link);
        box.appendChild(title);
        var meta = createElement(
            'div',
            'cardText cardTextCentered cardText-secondary jellyseerr-meta'
        );
        appendKinopoiskMeta(meta, item);
        box.appendChild(meta);
        card.appendChild(box);
        return card;
    }

    function customizeKinopoiskCard(card, item) {
        if (!card) {
            return null;
        }
        var kinopoiskId = String(pick(item, 'kinopoiskId', 'KinopoiskId') || '');
        card.classList.add('kp-similar-card');
        card.dataset.kinopoiskId = kinopoiskId;

        var imageUrl = normalizeImageUrl(
            pick(item, 'posterUrl', 'PosterUrl')
            || pick(item, 'posterUrlPreview', 'PosterUrlPreview')
        );
        var image = card.querySelector('.cardImageContainer');
        if (image && imageUrl) {
            var lifecycle = window.KinopoiskDetailPageLifecycle;
            if (lifecycle) {
                lifecycle.applyProtectedImage(image, imageUrl, 'background');
            }
        }
        var meta = card.querySelector('.jellyseerr-meta');
        if (meta) {
            appendKinopoiskMeta(meta, item);
        }
        return card;
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
        var enhanced = window.JellyfinEnhanced;
        var scroller = createElement('div', 'kp-recommendations-scroller');
        var list = createElement('div', 'kp-recommendations-items');
        items.forEach(function (item) {
            var fallback = createKinopoiskFallbackCard(item);
            if (!fallback) {
                return;
            }
            list.appendChild(fallback);
            getKinopoiskCardData(item).then(function (resolved) {
                if (!resolved
                    || !fallback.isConnected
                    || !enhanced
                    || !enhanced.jellyseerrUI
                    || typeof enhanced.jellyseerrUI.createJellyseerrCard !== 'function') {
                    return;
                }
                var card = enhanced.jellyseerrUI.createJellyseerrCard(
                    resolved.item,
                    resolved.active,
                    resolved.userFound
                );
                card = customizeKinopoiskCard(card, item);
                if (card && fallback.isConnected) {
                    fallback.replaceWith(card);
                }
            });
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
                    : 'Подборка The Movie Database не найдена.'
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
        tabs.setAttribute('role', 'tablist');
        var content = createElement('div', 'kp-recommendations-content');
        var activeSource = 'kinopoisk';
        var loaded = Object.create(null);

        var sources = [
            { key: 'kinopoisk', label: 'КиноПоиск' },
            { key: 'extended', label: 'The Movie Database' }
        ];

        function setActive(source) {
            activeSource = source;
            Array.prototype.forEach.call(
                tabs.querySelectorAll('.kp-recommendations-tab'),
                function (button) {
                    var active = button.dataset.source === source;
                    button.classList.toggle('is-active', active);
                    button.setAttribute('aria-selected', active ? 'true' : 'false');
                    button.tabIndex = active ? 0 : -1;
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
            var button = createElement(
                'button',
                'emby-button kp-recommendations-tab',
                source.label
            );
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

    function sectionMatches(page, itemId) {
    var section = page && page.querySelector('#kinopoiskRecommendationsSection');
    return Boolean(section && section.isConnected && section.dataset.itemId === itemId);
}

function clearRetryTimers() {
    retryTimers.forEach(function (timer) {
        clearTimeout(timer);
    });
    retryTimers = [];
    if (renderTimer) {
        clearTimeout(renderTimer);
        renderTimer = null;
    }
}

function renderCurrentItem(itemId, expectedGeneration) {
    ensureStyles();
    if (!itemId
        || expectedGeneration !== renderGeneration
        || getCurrentItemId() !== itemId) {
        return;
    }

    fetchCurrentItem(itemId).then(function (item) {
        if (!item
            || expectedGeneration !== renderGeneration
            || getCurrentItemId() !== itemId) {
            return;
        }

        var page = getVisiblePage();
        if (!page || !page.isConnected) {
            return;
        }

        var existing = page.querySelector('#kinopoiskRecommendationsSection');
        if (existing && existing.dataset.itemId === itemId) {
            applyNativeCardWidth(page, existing);
            clearRetryTimers();
            return;
        }
        if (existing) {
            existing.remove();
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
        if (!anchor
            || !anchor.isConnected
            || !anchor.parentNode
            || expectedGeneration !== renderGeneration
            || getCurrentItemId() !== itemId) {
            return;
        }

        removeLegacySeerrSections(page);
        var section = createSection(itemId, item, kinopoiskId);
        applyNativeCardWidth(page, section);
        anchor.parentNode.insertBefore(section, anchor.nextSibling);
        clearRetryTimers();
    });
}

function queueRenderAttempt(itemId, expectedGeneration, delay) {
    retryTimers.push(setTimeout(function () {
        renderCurrentItem(itemId, expectedGeneration);
    }, delay));
}

function startRenderCycle() {
    var itemId = getCurrentItemId();
    renderGeneration += 1;
    activeItemId = itemId;
    clearRetryTimers();
    if (!itemId) {
        return;
    }
    var expectedGeneration = renderGeneration;
    retryDelays.forEach(function (delay) {
        queueRenderAttempt(itemId, expectedGeneration, delay);
    });
}

function handleLifecycleContext(context) {
    if (!context || !context.isCurrent()) {
        return;
    }
    if (activeItemId !== context.itemId
        || activeLifecycleGeneration !== context.generation) {
        renderGeneration += 1;
        activeItemId = context.itemId;
        activeLifecycleGeneration = context.generation;
        clearRetryTimers();
        var expectedGeneration = renderGeneration;
        retryDelays.forEach(function (delay) {
            queueRenderAttempt(context.itemId, expectedGeneration, delay);
        });
        return;
    }
    scheduleImmediateRender();
}

function scheduleImmediateRender() {
    var itemId = getCurrentItemId();
    if (itemId !== activeItemId) {
        startRenderCycle();
        return;
    }
    if (!itemId || renderTimer) {
        return;
    }
    var expectedGeneration = renderGeneration;
    renderTimer = setTimeout(function () {
        renderTimer = null;
        renderCurrentItem(itemId, expectedGeneration);
    }, 90);
}

ensureStyles();
var lifecycle = window.KinopoiskDetailPageLifecycle;
if (lifecycle) {
    lifecycle.subscribe(handleLifecycleContext);
} else {
    startRenderCycle();
}
console.info('[КиноПоиск] Объединённый блок рекомендаций зарегистрирован.');
}());
