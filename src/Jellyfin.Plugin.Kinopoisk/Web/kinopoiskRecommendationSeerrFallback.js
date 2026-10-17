(function () {
    'use strict';

    if (window.__kinopoiskRecommendationSeerrFallbackInstalled) {
        return;
    }

    window.__kinopoiskRecommendationSeerrFallbackInstalled = true;

    var cache = new Map();
    var renderTimer = null;
    var retryTimer = null;
    var retryAttempt = 0;
    var retryDelays = [250, 750, 1500, 3000, 6000, 12000, 30000, 60000];
    var activeItemId = null;
    var activeApi = null;
    var activeUserId = null;
    var bindingGeneration = 0;
    var pendingCards = [];
    var activeRequests = 0;

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

    function getAuthHeaders(apiClient) {
        var token = apiClient && typeof apiClient.accessToken === 'function'
            ? apiClient.accessToken()
            : '';
        return {
            'Authorization': 'MediaBrowser Token="' + token + '"',
            'X-Emby-Token': token,
            'Accept': 'application/json'
        };
    }

    function fetchJson(apiClient, path) {
        return fetch(apiClient.getUrl(path), {
            credentials: 'same-origin',
            headers: getAuthHeaders(apiClient)
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        });
    }

    function normalizeTitle(value) {
        return String(value || '')
            .trim()
            .toLocaleLowerCase('ru-RU')
            .normalize('NFKD')
            .replace(/[\u0300-\u036f]/g, '')
            .replace(/[^a-zа-яё0-9]+/gi, '');
    }

    function getYear(value) {
        var match = String(value || '').match(/^(\d{4})/);
        return match ? Number(match[1]) : 0;
    }

    function getCandidateYear(candidate, mediaType) {
        return getYear(mediaType === 'tv'
            ? pick(candidate, 'firstAirDate', 'first_air_date')
            : pick(candidate, 'releaseDate', 'release_date'));
    }

    function scoreCandidate(candidate, source) {
        var candidateType = String(
            pick(candidate, 'mediaType', 'media_type') || ''
        ).toLowerCase();
        if (candidateType !== source.mediaType) {
            return -1;
        }

        var candidateId = String(pick(candidate, 'id', 'mediaId') || '');
        if (!/^\d+$/.test(candidateId)) {
            return -1;
        }

        var candidateYear = getCandidateYear(candidate, source.mediaType);
        if (source.year > 0 && candidateYear !== source.year) {
            return -1;
        }

        var titles = [
            pick(candidate, 'originalTitle', 'original_title'),
            pick(candidate, 'originalName', 'original_name'),
            pick(candidate, 'title'),
            pick(candidate, 'name')
        ].map(normalizeTitle).filter(Boolean);

        var originalMatch = source.originalTitle
            && titles.indexOf(source.originalTitle) >= 0;
        var localMatch = source.localTitle
            && titles.indexOf(source.localTitle) >= 0;
        if (!originalMatch && !localMatch) {
            return -1;
        }

        return (originalMatch ? 40 : 30) + (source.year > 0 ? 20 : 0);
    }

    function selectUniqueMatch(results, item) {
        var source = {
            mediaType: String(pick(item, 'mediaType', 'MediaType') || 'movie') === 'tv'
                ? 'tv'
                : 'movie',
            year: Number(pick(item, 'year', 'Year') || 0),
            originalTitle: normalizeTitle(pick(item, 'originalName', 'OriginalName')),
            localTitle: normalizeTitle(pick(item, 'name', 'Name'))
        };
        var byId = Object.create(null);

        asArray(results).forEach(function (candidate) {
            var score = scoreCandidate(candidate, source);
            var id = String(pick(candidate, 'id', 'mediaId') || '');
            if (score < 0 || !/^\d+$/.test(id)) {
                return;
            }
            if (!byId[id] || score > byId[id].score) {
                byId[id] = { id: id, score: score };
            }
        });

        var matches = Object.keys(byId)
            .map(function (id) { return byId[id]; })
            .sort(function (left, right) { return right.score - left.score; });
        if (!matches.length) {
            return null;
        }
        if (matches.length > 1 && matches[0].score === matches[1].score) {
            return null;
        }
        return matches[0].id;
    }

    function searchTmdbId(enhanced, item) {
        var api = enhanced.jellyseerrAPI;
        if (!api || typeof api.search !== 'function') {
            return Promise.resolve(null);
        }

        var queries = [
            String(pick(item, 'originalName', 'OriginalName') || '').trim(),
            String(pick(item, 'name', 'Name') || '').trim()
        ].filter(function (value, index, values) {
            return value && values.indexOf(value) === index;
        });
        if (!queries.length) {
            return Promise.resolve(null);
        }

        var results = [];
        var failed = false;
        return queries.reduce(function (promise, query) {
            return promise.then(function () {
                return Promise.resolve().then(function () {
                    return api.search(query, 1, { skipCache: false, throwOnError: true });
                })
                    .then(function (response) {
                        results = results.concat(asArray(response && response.results));
                    }).catch(function () { failed = true; });
            });
        }, Promise.resolve()).then(function () {
            var match = selectUniqueMatch(results, item);
            if (!match && failed) {
                throw new Error('Поиск Seerr временно недоступен.');
            }
            return match;
        });
    }

    function resolveTmdbId(enhanced, item) {
        var imdbId = String(pick(item, 'imdbId', 'ImdbId') || '').trim();
        var mediaType = String(pick(item, 'mediaType', 'MediaType') || 'movie') === 'tv'
            ? 'tv'
            : 'movie';
        var resolver = window.KinopoiskTmdbResolver;
        var direct = /^tt\d+$/.test(imdbId)
            && resolver
            && typeof resolver.resolve === 'function'
            ? resolver.resolve(imdbId, mediaType === 'tv' ? 'Series' : 'Movie')
            : Promise.resolve(null);

        return Promise.resolve(direct).catch(function () {
            return null;
        }).then(function (tmdbId) {
            return /^\d+$/.test(String(tmdbId || ''))
                ? String(tmdbId)
                : searchTmdbId(enhanced, item);
        });
    }

    function getSeerrStatus(enhanced) {
        var key = 'seerr-status';
        if (!cache.has(key)) {
            var request = enhanced.jellyseerrAPI
                && typeof enhanced.jellyseerrAPI.checkUserStatus === 'function'
                ? enhanced.jellyseerrAPI.checkUserStatus()
                : Promise.resolve({ active: false, userFound: false });
            var pending = Promise.resolve(request).catch(function () {
                return { active: false, userFound: false };
            }).then(function (status) {
                if ((!status || !status.active || !status.userFound) && cache.get(key) === pending) {
                    cache.delete(key);
                }
                return status;
            });
            cache.set(key, pending);
        }
        return cache.get(key);
    }

    function fetchDetails(enhanced, tmdbId, mediaType) {
        var api = enhanced.jellyseerrAPI;
        if (mediaType === 'movie' && typeof api.fetchMovieDetails === 'function') {
            return api.fetchMovieDetails(Number(tmdbId));
        }
        if (mediaType === 'tv' && typeof api.fetchTvShowDetails === 'function') {
            return api.fetchTvShowDetails(Number(tmdbId));
        }
        return Promise.resolve(null);
    }

    function buildCardData(item, details, tmdbId) {
        var mediaType = String(pick(item, 'mediaType', 'MediaType') || 'movie') === 'tv'
            ? 'tv'
            : 'movie';
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
        return result;
    }

    function resolveCardData(enhanced, item) {
        var kinopoiskId = String(pick(item, 'kinopoiskId', 'KinopoiskId') || '');
        var key = 'match:' + kinopoiskId;
        if (!cache.has(key)) {
            var request = Promise.all([
                getSeerrStatus(enhanced),
                resolveTmdbId(enhanced, item)
            ]).then(function (values) {
                var status = values[0] || { active: false, userFound: false };
                var tmdbId = String(values[1] || '');
                if (!/^\d+$/.test(tmdbId)) {
                    return null;
                }
                var mediaType = String(
                    pick(item, 'mediaType', 'MediaType') || 'movie'
                ) === 'tv' ? 'tv' : 'movie';
                return Promise.resolve(fetchDetails(enhanced, tmdbId, mediaType))
                    .then(function (details) {
                        if (!details) {
                            throw new Error('Состояние фильма в Seerr не получено.');
                        }
                        return {
                            item: buildCardData(item, details, tmdbId),
                            active: status.active === true,
                            userFound: status.userFound === true
                        };
                    });
            });
            var pending = request.then(function (result) {
                if ((!result || !result.active || !result.userFound) && cache.get(key) === pending) {
                    cache.delete(key);
                }
                return result;
            }, function (error) {
                if (cache.get(key) === pending) {
                    cache.delete(key);
                }
                throw error;
            });
            cache.set(key, pending);
        }
        return cache.get(key);
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

    function appendKinopoiskMeta(container, item) {
        container.textContent = '';
        var source = document.createElement('span');
        source.className = 'kp-similar-card__source';
        source.textContent = 'КиноПоиск';
        container.appendChild(source);

        var year = Number(pick(item, 'year', 'Year') || 0);
        if (year > 0) {
            var yearNode = document.createElement('bdi');
            yearNode.textContent = String(year);
            container.appendChild(yearNode);
        }

        var rating = Number(pick(item, 'ratingKinopoisk', 'RatingKinopoisk') || 0);
        if (rating > 0) {
            var ratingNode = document.createElement('span');
            ratingNode.className = 'kp-similar-card__rating';
            var star = document.createElement('span');
            star.className = 'kp-similar-card__star';
            star.textContent = '★';
            var value = document.createElement('span');
            value.textContent = rating.toFixed(1);
            ratingNode.append(star, value);
            container.appendChild(ratingNode);
        }
    }

    function customizeCard(card, item) {
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

    function fetchRecommendationItems() {
        var itemId = getCurrentItemId();
        var apiClient = window.ApiClient;
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        var page = lifecycle ? lifecycle.getActivePage() : document;
        var container = page && page.querySelector('#kinopoiskRecommendationsSection .kp-recommendations-items');
        if (container && Array.isArray(container.__kinopoiskRecommendationItems)) {
            return Promise.resolve(container.__kinopoiskRecommendationItems);
        }
        if (!itemId || !apiClient || typeof apiClient.getItem !== 'function') {
            return Promise.resolve([]);
        }

        var key = 'items:' + itemId;
        if (!cache.has(key)) {
            var itemRequest = lifecycle
                ? lifecycle.fetchItem(itemId)
                : apiClient.getItem(apiClient.getCurrentUserId(), itemId);
            var request = itemRequest
                .then(function (currentItem) {
                    var kinopoiskId = String(getProviderId(currentItem, 'kinopoisk') || '');
                    if (!/^\d+$/.test(kinopoiskId)) {
                        return [];
                    }
                    var path = '/KinopoiskPresentation/'
                        + encodeURIComponent(kinopoiskId)
                        + '/similars';
                    var dataRequest = lifecycle
                        ? lifecycle.fetchJson(path)
                        : fetchJson(apiClient, path);
                    return dataRequest.then(function (response) {
                        return asArray(pick(response, 'items', 'Items'));
                    });
                }).catch(function () {
                    cache.delete(key);
                    return [];
                });
            cache.set(key, request);
        }
        return cache.get(key);
    }

    function getUserId() {
        var apiClient = window.ApiClient;
        return apiClient && typeof apiClient.getCurrentUserId === 'function'
            ? String(apiClient.getCurrentUserId() || '')
            : '';
    }

    function scheduleRetry() {
        if (retryTimer || retryAttempt >= retryDelays.length) {
            return;
        }
        retryTimer = setTimeout(function () {
            retryTimer = null;
            processFallbackCards();
        }, retryDelays[retryAttempt++]);
    }

    function drainCards() {
        while (activeRequests < 3 && pendingCards.length) {
            activeRequests += 1;
            Promise.resolve().then(pendingCards.shift()).catch(function (error) {
                console.debug('[КиноПоиск] Карточка Seerr пока не готова.', error);
            }).then(function () {
                activeRequests -= 1;
                drainCards();
            });
        }
    }

    function setCardStatus(card, status) {
        var label = card.querySelector('.kp-similar-card__fallback-button');
        if (label && label.textContent !== status) {
            label.textContent = status;
        }
    }

    function processFallbackCards() {
        var itemId = getCurrentItemId();
        if (itemId !== activeItemId) {
            activeItemId = itemId;
            retryAttempt = 0;
            clearTimeout(retryTimer);
            retryTimer = null;
        }
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        var page = lifecycle ? lifecycle.getActivePage() : document;
        var cards = page && page.querySelectorAll('.kp-similar-card--fallback[data-kinopoisk-id]');
        if (!itemId || !cards || !cards.length) {
            return;
        }
        var enhanced = window.JellyfinEnhanced;
        var api = enhanced && enhanced.jellyseerrAPI;
        var userId = getUserId();
        if (api !== activeApi || userId !== activeUserId) {
            activeApi = api;
            activeUserId = userId;
            bindingGeneration += 1;
            cache.clear();
            Array.prototype.forEach.call(cards, function (card) {
                delete card.dataset.kpSeerrFallbackState;
                delete card.dataset.kpSeerrRetryAfter;
            });
        }
        if (!api || !enhanced.jellyseerrUI
            || typeof enhanced.jellyseerrUI.createJellyseerrCard !== 'function') {
            scheduleRetry();
            return;
        }
        var expectedGeneration = bindingGeneration;
        function isCurrent(card) {
            return bindingGeneration === expectedGeneration
                && getCurrentItemId() === itemId
                && getUserId() === userId
                && (!lifecycle || lifecycle.getActivePage() === page)
                && card.isConnected && page.contains(card);
        }
        fetchRecommendationItems().then(function (items) {
            var byId = Object.create(null);
            items.forEach(function (item) {
                byId[String(pick(item, 'kinopoiskId', 'KinopoiskId') || '')] = item;
            });
            Array.prototype.forEach.call(cards, function (fallback) {
                if (!isCurrent(fallback) || fallback.dataset.kpSeerrFallbackState === 'pending') {
                    return;
                }
                if (Number(fallback.dataset.kpSeerrRetryAfter || 0) > Date.now()) {
                    scheduleRetry();
                    return;
                }
                var item = byId[String(fallback.dataset.kinopoiskId || '')];
                if (!item) {
                    scheduleRetry();
                    return;
                }
                fallback.dataset.kpSeerrFallbackState = 'pending';
                setCardStatus(fallback, 'Подключение к Seerr…');
                pendingCards.push(function () {
                    if (!isCurrent(fallback)) {
                        return;
                    }
                    return Promise.resolve().then(function () {
                        return resolveCardData(enhanced, item);
                    }).then(function (resolved) {
                        if (!isCurrent(fallback)) {
                            return;
                        }
                        if (!resolved) {
                            fallback.dataset.kpSeerrFallbackState = 'unmatched';
                            fallback.dataset.kpSeerrRetryAfter = String(Date.now() + 30000);
                            setCardStatus(fallback, 'Не найдено в Seerr · КиноПоиск');
                            scheduleRetry();
                            return;
                        }
                        if (!resolved.active || !resolved.userFound) {
                            throw new Error('Seerr или учётная запись пока недоступны.');
                        }
                        var card = enhanced.jellyseerrUI.createJellyseerrCard(
                            resolved.item, resolved.active, resolved.userFound
                        );
                        card = customizeCard(card, item);
                        if (!card) {
                            throw new Error('Интерфейс Seerr пока не готов.');
                        }
                        if (isCurrent(fallback)) {
                            fallback.replaceWith(card);
                        }
                    }).catch(function (error) {
                        if (isCurrent(fallback)) {
                            fallback.dataset.kpSeerrFallbackState = 'retry';
                            fallback.dataset.kpSeerrRetryAfter = String(Date.now() + 1500);
                            setCardStatus(fallback, 'Seerr временно недоступен · КиноПоиск');
                            scheduleRetry();
                            console.debug('[КиноПоиск] Карточка Seerr будет обработана повторно.', error);
                        }
                    });
                });
            });
            drainCards();
        }).catch(function () { scheduleRetry(); });
    }

    function scheduleProcessing() {
        if (!renderTimer) {
            renderTimer = setTimeout(function () {
                renderTimer = null;
                processFallbackCards();
            }, 100);
        }
    }

    document.addEventListener('kinopoisk:recommendations-rendered', scheduleProcessing);
    document.addEventListener('jellyseerr-media-requested', function () { cache.clear(); });
    document.addEventListener('focusin', function (event) {
        if (event.target.closest && event.target.closest('.kp-similar-card--fallback')) {
            retryAttempt = 0;
            scheduleProcessing();
        }
    });
    var lifecycle = window.KinopoiskDetailPageLifecycle;
    if (lifecycle) {
        lifecycle.subscribe(scheduleProcessing);
    } else {
        scheduleProcessing();
    }
    console.info('[КиноПоиск] Резервное сопоставление карточек через Seerr зарегистрировано.');
}());
