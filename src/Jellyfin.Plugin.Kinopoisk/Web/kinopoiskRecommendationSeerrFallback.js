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
    var openDialog = null;
    var requestProvider = 'seerr';
    var statusExpiresAt = 0;
    var liveTimer = null;
    var liveRefreshRunning = false;
    var liveFailures = 0;

    // Core cards do not require an external plugin. Both optional connectors
    // use authenticated server endpoints; service credentials stay on the server.
    function proxyFetch(base, path, body) {
        var client = window.ApiClient;
        if (!client || typeof client.getUrl !== 'function') {
            return Promise.reject(new Error('Сеанс Jellyfin ещё не готов.'));
        }
        var controller = new AbortController();
        var timer = body === undefined ? setTimeout(function () { controller.abort(); }, 20000) : null;
        var headers = getAuthHeaders(client);
        if (base === '/JellyfinEnhanced/jellyseerr') {
            // Compatibility with older Enhanced versions; current versions use
            // the authenticated principal. Never accept an arbitrary user id.
            headers['X-Jellyfin-User-Id'] = getUserId();
        }
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
        }
        return fetch(client.getUrl(base + path), {
            method: body === undefined ? 'GET' : 'POST',
            credentials: 'same-origin',
            headers: headers,
            signal: body === undefined ? controller.signal : undefined,
            body: body === undefined ? undefined : JSON.stringify(body)
        }).then(function (response) {
            return response.json().catch(function () { return {}; }).then(function (data) {
                if (!response.ok) {
                    var error = new Error(response.status === 404
                        ? 'Серверная интеграция Seerr недоступна.'
                        : String(data.message || data.errorMessage || 'Ошибка Seerr: HTTP ' + response.status));
                    error.status = response.status;
                    throw error;
                }
                return data;
            });
        }).finally(function () { clearTimeout(timer); });
    }

    function proxy(path, body) {
        if (path !== '/user-status') {
            return proxyFetch(requestProvider === 'enhanced' ? '/JellyfinEnhanced/jellyseerr' : '/KinopoiskRequests', path, body);
        }
        var userId = getUserId();
        return proxyFetch('/KinopoiskRequests', path).then(function (status) {
            if (getUserId() !== userId) { throw new Error('Учётная запись изменилась.'); }
            requestProvider = status.provider === 'enhanced' ? 'enhanced' : 'seerr';
            if (requestProvider !== 'enhanced') { return status; }
            return proxyFetch('/JellyfinEnhanced/jellyseerr', path).then(function (legacyStatus) {
                return Object.assign({}, legacyStatus, { provider: 'enhanced', enhancedDetected: true,
                    reason: legacyStatus.reason || (!legacyStatus.active ? 'disabled' : !legacyStatus.userFound ? 'unlinked' : 'linked') });
            }).catch(function (error) {
                if (error.status !== 404) { throw error; }
                return { provider: 'enhanced', enhancedDetected: false, active: false, userFound: false, reason: 'enhanced_missing' };
            });
        });
    }

    var proxyApi = {
        checkUserStatus: function () {
            return proxy('/user-status');
        },
        search: function (query) {
            return proxy('/search?query=' + encodeURIComponent(query) + '&page=1');
        },
        fetchMovieDetails: function (id) {
            return proxy('/movie/' + id);
        },
        fetchTvShowDetails: function (id) {
            return proxy('/tv/' + id);
        },
        fetchSimilarMovies: function (id) { return proxy('/movie/' + id + '/similar'); },
        fetchRecommendedMovies: function (id) { return proxy('/movie/' + id + '/recommendations'); },
        fetchSimilarTvShows: function (id) { return proxy('/tv/' + id + '/similar'); },
        fetchRecommendedTvShows: function (id) { return proxy('/tv/' + id + '/recommendations'); }
    };

    window.KinopoiskRecommendationRequests = proxyApi;

    function getIntegration() {
        return { jellyseerrAPI: proxyApi };
    }

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
        var saved = String(item.tmdbId || '');
        if (/^\d+$/.test(saved)) { return Promise.resolve(saved); }
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
        if (Date.now() >= statusExpiresAt) { cache.delete(key); }
        if (!cache.has(key)) {
            var request = enhanced.jellyseerrAPI
                && typeof enhanced.jellyseerrAPI.checkUserStatus === 'function'
                ? enhanced.jellyseerrAPI.checkUserStatus()
                : Promise.resolve({ active: false, userFound: false });
            var pending = Promise.resolve(request).then(function (status) {
                statusExpiresAt = Date.now() + 30000;
                if ((!status || ((!status.active || !status.userFound) && !isStandalone(status))) && cache.get(key) === pending) {
                    cache.delete(key);
                }
                return status;
            }, function (error) {
                if (cache.get(key) === pending) { cache.delete(key); }
                throw error;
            });
            statusExpiresAt = Date.now() + 30000;
            cache.set(key, pending);
        }
        return cache.get(key);
    }

    function isStandalone(status) {
        return status && !status.active && (status.reason === 'disabled' || status.reason === 'enhanced_missing');
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
        var key = 'match:' + (kinopoiskId || item.mediaType + ':' + item.tmdbId);
        if (!cache.has(key)) {
            var status;
            var request = getSeerrStatus(enhanced).then(function (value) {
                status = value || {};
                if (isStandalone(status)) {
                    return null;
                }
                if (!status.active || !status.userFound) {
                    var reasons = {
                        disabled: 'Сервис запросов не подключён.',
                        unlinked: 'Учётная запись Jellyfin не связана с Seerr.',
                        blocked: 'Для этой учётной записи доступ к Seerr запрещён.',
                        no_user: 'Войдите в Jellyfin для запроса.',
                        unreachable: 'Seerr сейчас недоступен.'
                    };
                    var error = new Error(reasons[status.reason] || 'Seerr или учётная запись сейчас недоступны.');
                    error.reason = status.reason;
                    throw error;
                }
                return resolveTmdbId(enhanced, item);
            }).then(function (resolvedId) {
                var tmdbId = String(resolvedId || '');
                if (isStandalone(status)) {
                    return { standalone: true, active: false, userFound: false, connection: status };
                }
                if (!/^\d+$/.test(tmdbId)) {
                    return null;
                }
                var mediaType = String(
                    pick(item, 'mediaType', 'MediaType') || 'movie'
                ) === 'tv' ? 'tv' : 'movie';
                return Promise.resolve(fetchDetails(enhanced, tmdbId, mediaType))
                    .then(function (details) {
                        if (!details || Number(details.id) !== Number(tmdbId)) {
                            throw new Error('Состояние фильма в Seerr не получено.');
                        }
                        return {
                            item: buildCardData(item, details, tmdbId),
                            status: status,
                            active: status.active === true,
                            userFound: status.userFound === true
                        };
                    });
            });
            var pending = request.then(function (result) {
                if ((!result || result.standalone || !result.active || !result.userFound) && cache.get(key) === pending) {
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

    function element(tag, className, text) {
        var node = document.createElement(tag);
        node.className = className || '';
        if (text !== undefined) { node.textContent = text; }
        return node;
    }

    function mediaRequests(item) {
        return asArray(item.mediaInfo && item.mediaInfo.requests).concat(asArray(item.requests))
            .filter(function (request) { return request && !request.is4k; })
            .sort(function (left, right) { return Number(right.id || 0) - Number(left.id || 0); });
    }

    function seasonBlocked(item, number) {
        var known = asArray(item.mediaInfo && item.mediaInfo.seasons);
        var state = known.find(function (value) { return Number(value.seasonNumber) === number; });
        return (!!state && [2, 3, 4, 5, 6].indexOf(Number(state.status)) >= 0)
            || mediaRequests(item).some(function (request) {
                return [1, 2, 4].indexOf(Number(request.status)) >= 0
                    && asArray(request.seasons).some(function (season) { return Number(season.seasonNumber) === number; });
            });
    }

    function availability(item) {
        var info = item.mediaInfo || {};
        var status = Number(info.status || 1);
        var requests = mediaRequests(item);
        var active = requests.find(function (request) { return [1, 2, 4].indexOf(Number(request.status)) >= 0; });
        var requestStatus = Number((active || requests[0] || {}).status || 0);
        var label = status === 5 ? 'Доступно' : status === 6 ? 'Заблокировано в Seerr'
            : status === 4 ? 'Частично доступно' : requestStatus === 4 ? 'Ошибка заявки'
            : status === 3 ? 'Загружается' : requestStatus === 1 ? 'Ожидает одобрения'
            : requestStatus === 2 ? 'Одобрено' : status === 2 ? 'Запрошено'
            : requestStatus === 3 ? 'Отклонено' : requestStatus === 5 ? 'Заявка завершена' : 'Запросить';
        var canRequest = status !== 5 && status !== 6 && !active && status !== 2 && status !== 3;
        if (item.mediaType === 'tv' && status !== 5 && status !== 6) {
            canRequest = asArray(item.seasons).some(function (season) {
                return Number.isInteger(season.seasonNumber) && season.seasonNumber >= 0
                    && Number(season.episodeCount) > 0 && !seasonBlocked(item, season.seasonNumber);
            });
        }
        var libraryId = String(info.jellyfinMediaId || (item.mediaType === 'tv' && info.jellyfinSeriesId) || '');
        return { label: label, canRequest: canRequest,
            libraryId: /^[a-z0-9-]+$/i.test(libraryId) ? libraryId : '' };
    }

    function bindCard(card, resolved) {
        card.__kpSeerrResolved = resolved;
        card.dataset.kpSeerrFallbackState = 'ready';
        delete card.dataset.kpSeerrReason;
        card.dataset.tmdbId = String(resolved.item.id);
        var available = availability(resolved.item);
        var permitted = resolved.item.mediaType === 'tv' ? resolved.status.canRequestTv : resolved.status.canRequestMovie;
        setCardStatus(card, available.canRequest && permitted === false ? 'Подробнее' : available.label);
        scheduleLiveRefresh();
    }

    function safeWebUrl(value) {
        try {
            if (!value || String(value).length > 2048 || /[\x00-\x1f\x7f]/.test(value)) { return ''; }
            var url = new URL(value);
            if (!/^https?:$/.test(url.protocol) || url.username || url.password || url.search || url.hash) { return ''; }
            return url.href.replace(/\/+$/, '');
        } catch (_) { return ''; }
    }

    function getSeerrLink(item) {
        var key = 'seerr-link';
        if (!cache.has(key)) {
            var userId = getUserId();
            var request = proxyFetch('/KinopoiskRequests', '/link').then(function (settings) {
                if (getUserId() !== userId) { return ''; }
                var url = safeWebUrl(settings.url);
                if (url || settings.mode === 'custom' || requestProvider !== 'enhanced') { return url; }
                return proxyFetch('/JellyfinEnhanced', '/public-config').then(function (config) {
                    if (settings.mode !== 'local') {
                        var client = window.ApiClient;
                        var address = client && typeof client.serverAddress === 'function' ? client.serverAddress() : window.location.origin;
                        var current = String(address).replace(/\/+$/, '').toLowerCase();
                        var mappings = String(config.JellyseerrUrlMappings || '').split('\n');
                        for (var index = 0; index < mappings.length; index++) {
                            var pair = mappings[index].split('|').map(function (value) { return value.trim(); });
                            if (pair.length === 2 && pair[0].replace(/\/+$/, '').toLowerCase() === current && safeWebUrl(pair[1])) {
                                return safeWebUrl(pair[1]);
                            }
                        }
                    }
                    return safeWebUrl(config.JellyseerrBaseUrl);
                });
            }).catch(function () { cache.delete(key); return ''; });
            cache.set(key, request);
        }
        return cache.get(key).then(function (url) {
            return url ? url + '/' + (item.mediaType === 'tv' ? 'tv' : 'movie') + '/' + Number(item.id) : '';
        });
    }

    function readQuota() {
        return proxy('/quota').then(function (data) {
            if (!data || typeof data !== 'object') { throw new Error('Квота не получена.'); }
            return data;
        });
    }

    function quotaState(data, type, count) {
        var value = data && data[type];
        if (!value || !Number.isFinite(Number(value.limit)) || Number(value.limit) < 0) {
            return { text: 'Не удалось получить квоту. Лимит проверит Seerr при отправке.', blocked: false };
        }
        var limit = Number(value.limit);
        if (limit === 0) { return { text: 'Квота: без ограничений.', blocked: false }; }
        var remaining = Number(value.remaining);
        if (!Number.isFinite(remaining)) { remaining = Math.max(0, limit - Number(value.used || 0)); }
        var unit = type === 'tv' ? 'сезонов' : 'фильмов';
        return { text: 'Квота: осталось ' + Math.max(0, remaining) + ' из ' + limit + ' ' + unit
            + (Number(value.days) > 0 ? ' за ' + Number(value.days) + ' дн' : ' за всё время') + '.'
            + (type === 'tv' ? ' Выбрано: ' + count + '.' : '')
            + (value.restricted || count > remaining ? ' Лимит запроса исчерпан или выбранных сезонов слишком много.' : ''),
            blocked: !!value.restricted || count > remaining };
    }

    function requestDefaults(item, userStatus) {
        // Older Enhanced versions may not expose their JS API. Preserve Seerr's
        // routing rules when using its server proxy directly as well.
        return proxy('/overrideRule').catch(function (error) {
            if (error.status === 403 || error.status === 404) { return []; }
            throw error;
        }).then(function (rules) {
            var service = item.mediaType === 'tv' ? 'sonarrServiceId' : 'radarrServiceId';
            var genres = asArray(item.genres).map(function (genre) { return String(genre.id).toLowerCase(); })
                .concat(asArray(item.genres).map(function (genre) { return String(genre.name).toLowerCase(); }))
                .concat(asArray(item.genreIds).map(String));
            var keywords = asArray(item.keywords).map(function (keyword) { return String(keyword.name).toLowerCase(); });
            function includesAny(condition, values) {
                return String(condition).toLowerCase().split('|').some(function (value) {
                    return values.indexOf(value.trim()) >= 0;
                });
            }
            var rule = asArray(rules).find(function (candidate) {
                return candidate[service] != null
                    && (!candidate.language || includesAny(candidate.language, [String(item.originalLanguage || '').toLowerCase()]))
                    && (!candidate.genre || includesAny(candidate.genre, genres))
                    && (!candidate.keywords || includesAny(candidate.keywords, keywords))
                    && (!candidate.users || String(candidate.users).split(',').map(function (id) { return id.trim(); })
                        .indexOf(String(userStatus.jellyseerrUserId || '')) >= 0);
            });
            if (!rule) { return {}; }
            var settings = { serverId: rule[service] };
            if (rule.profileId != null) { settings.profileId = rule.profileId; }
            if (rule.rootFolder) { settings.rootFolder = rule.rootFolder; }
            if (rule.tags != null) {
                settings.tags = (Array.isArray(rule.tags) ? rule.tags : String(rule.tags).split('|'))
                    .map(Number).filter(function (id) { return Number.isInteger(id) && id >= 0; });
            }
            return settings;
        });
    }

    function submitRequest(item, seasons, userStatus, isCurrent) {
        if (!isCurrent()) { return Promise.reject(new Error('Страница или учётная запись изменились.')); }
        return requestDefaults(item, userStatus).then(function (settings) {
            if (!isCurrent()) { throw new Error('Страница или учётная запись изменились.'); }
            return proxy('/request', Object.assign({}, settings, {
                mediaType: item.mediaType,
                mediaId: Number(item.id)
            }, item.mediaType === 'tv' ? { seasons: seasons } : {})).then(function (response) {
                if (!response || !Number.isInteger(response.id) || response.id < 1) {
                    throw new Error('Seerr не подтвердил создание заявки. Обновите состояние фильма или сезонов.');
                }
                return response;
            });
        });
    }

    function findLibraryItem(card, source) {
        if (!/^\d+$/.test(String(card.dataset.kinopoiskId || ''))) { return Promise.resolve(null); }
        var type = String(pick(source, 'mediaType', 'MediaType') || 'movie') === 'tv' ? 'tv' : 'movie';
        return fetchJson(window.ApiClient, '/KinopoiskRecommendations/library/' + card.dataset.kinopoiskId
            + '?mediaType=' + type).then(function (data) {
            var id = String(data.itemId || '');
            return /^[a-z0-9-]+$/i.test(id) ? id : null;
        });
    }

    function ensureDialogStyles() {
        if (document.getElementById('kinopoiskRecommendationDialogStyles')) { return; }
        var style = element('style');
        style.id = 'kinopoiskRecommendationDialogStyles';
        style.textContent = '.kp-recommendation-dialog{box-sizing:border-box;margin:auto;width:min(38em,calc(100vw - 2em));max-height:calc(100dvh - 2em);padding:1.5em;border:0;border-radius:.3em;overflow:auto;color:inherit}'
            + '.kp-recommendation-dialog::backdrop{background:rgba(0,0,0,.65)}'
            + '.kp-recommendation-dialog h2{margin-top:0}.kp-recommendation-dialog p{line-height:1.5;white-space:pre-wrap}'
            + '.kp-recommendation-dialog__actions{display:flex;align-items:center;justify-content:flex-end;gap:.6em;flex-wrap:wrap;margin-top:1.4em}'
            + '.kp-recommendation-dialog [hidden]{display:none}'
            + '.kp-recommendation-dialog__seasons{display:grid;gap:.7em;margin:1em 0}.kp-recommendation-dialog__seasons label{display:flex;gap:.7em;align-items:center}'
            + '.kp-recommendation-dialog__status{line-height:1.5;margin-top:1em}';
        document.head.appendChild(style);
    }

    function showDetails(card, keyboard) {
        if (!card || !card.isConnected) { return; }
        if (openDialog) { openDialog.close(); }
        var source = card.__kinopoiskRecommendationItem;
        if (!source) { return; }
        ensureDialogStyles();
        var ownerId = getCurrentItemId();
        var userId = getUserId();
        var returnFocus = document.activeElement;
        var dialog = element('dialog', 'dialog kp-recommendation-dialog');
        var title = String(pick(source, 'name', 'Name') || pick(source, 'originalName', 'OriginalName') || 'Рекомендация');
        var heading = element('h2', '', title);
        heading.id = 'kinopoiskRecommendationDialogTitle';
        dialog.setAttribute('aria-labelledby', heading.id);
        dialog.appendChild(heading);
        var year = Number(pick(source, 'year', 'Year') || 0);
        if (year > 0) { dialog.appendChild(element('div', 'secondaryText', String(year))); }
        dialog.appendChild(element('p', '', String(pick(source, 'overview', 'Overview') || 'Краткое описание отсутствует.')));
        var status = element('div', 'kp-recommendation-dialog__status', 'Загрузка действий…');
        status.setAttribute('role', 'status');
        dialog.appendChild(status);
        var seasons = element('div', 'kp-recommendation-dialog__seasons');
        dialog.appendChild(seasons);
        var actions = element('div', 'kp-recommendation-dialog__actions');
        var kpLink = element('a', 'emby-button', 'КиноПоиск');
        var tmdbSource = source.tmdbId && !Number(source.kinopoiskId || source.KinopoiskId);
        kpLink.href = tmdbSource ? 'https://www.themoviedb.org/' + (source.mediaType === 'tv' ? 'tv/' : 'movie/') + encodeURIComponent(source.tmdbId)
            : 'https://www.kinopoisk.ru/film/' + encodeURIComponent(card.dataset.kinopoiskId) + '/';
        if (tmdbSource) { kpLink.textContent = 'TMDB'; }
        kpLink.target = '_blank';
        kpLink.rel = 'noopener noreferrer';
        var cancel = element('button', 'emby-button button-flat', 'Закрыть');
        cancel.type = 'button';
        var submit = element('button', 'emby-button raised button-submit kp-recommendation-dialog__submit', 'Запросить');
        submit.type = 'button';
        submit.disabled = true;
        submit.hidden = true;
        actions.append(kpLink, cancel, submit);
        dialog.appendChild(actions);
        document.body.appendChild(dialog);
        var closed = false;
        function isCurrent() {
            return !closed && getCurrentItemId() === ownerId && getUserId() === userId
                && card.isConnected && dialog.isConnected;
        }
        function close() {
            if (closed) { return; }
            closed = true;
            if (typeof dialog.close === 'function' && dialog.open) { dialog.close(); }
            dialog.remove();
            if (openDialog && openDialog.element === dialog) { openDialog = null; }
            if (keyboard && returnFocus && returnFocus.isConnected) {
                returnFocus.focus({ preventScroll: true });
            } else if (card.contains(document.activeElement)) {
                // Native dialog.close() restores focus too. Pointer activation
                // must not leave Jellyfin's :focus-within hover on the card.
                document.activeElement.blur();
            }
        }
        openDialog = { element: dialog, close: close, ownerId: ownerId, userId: userId, card: card };
        findLibraryItem(card, source).then(function (libraryId) {
            if (!isCurrent() || !libraryId || actions.querySelector('.kp-recommendation-library')) { return; }
            var library = element('a', 'emby-button kp-recommendation-library', 'Открыть в Jellyfin');
            library.href = '#/details?id=' + encodeURIComponent(libraryId);
            actions.insertBefore(library, cancel);
            library.addEventListener('click', close);
        }).catch(function () { /* The synopsis and optional request remain available. */ });
        cancel.addEventListener('click', close);
        dialog.addEventListener('cancel', function (event) { event.preventDefault(); close(); });
        if (typeof dialog.showModal === 'function') { dialog.showModal(); }
        else { dialog.setAttribute('open', ''); }
        cancel.focus();
        var quota = element('div', 'secondaryText kp-recommendation-dialog__quota', 'Проверка квоты…');
        status.after(quota);
        var seerrLink = element('a', 'emby-button kp-recommendation-seerr-link', 'Открыть в Seerr');
        seerrLink.target = '_blank';
        seerrLink.rel = 'noopener noreferrer';
        seerrLink.hidden = true;
        actions.insertBefore(seerrLink, cancel);
        var resolved;
        var inFlight = false;
        var uncertain = false;
        var submissionNotice = false;
        var quotaData = null;
        var quotaLoading = true;
        var quotaSequence = 0;
        function selectedSeasons() {
            return Array.prototype.filter.call(seasons.querySelectorAll('input:checked'), function (input) { return !input.disabled; })
                .map(function (input) { return Number(input.value); });
        }
        function renderActions() {
            if (!resolved || !isCurrent() || inFlight || uncertain) { return; }
            var item = resolved.item;
            var available = availability(item);
            var permitted = item.mediaType === 'tv' ? resolved.status.canRequestTv : resolved.status.canRequestMovie;
            var canRequest = available.canRequest && permitted !== false;
            var choices = item.mediaType === 'tv' && canRequest ? asArray(item.seasons).filter(function (season) {
                return Number.isInteger(season.seasonNumber) && season.seasonNumber >= 0 && Number(season.episodeCount) > 0;
            }) : [];
            var config = requestProvider === 'enhanced' && window.JellyfinEnhanced && window.JellyfinEnhanced.pluginConfig;
            var wanted = choices.map(function (choice) { return String(choice.seasonNumber); });
            Array.prototype.forEach.call(seasons.querySelectorAll('label'), function (label) {
                if (wanted.indexOf(label.querySelector('input').value) < 0) { label.remove(); }
            });
            choices.forEach(function (season) {
                var input = seasons.querySelector('input[value="' + season.seasonNumber + '"]');
                if (!input) {
                    var label = element('label');
                    input = element('input');
                    input.type = config && config.JellyseerrSingleSeasonRequestOnly ? 'radio' : 'checkbox';
                    input.name = 'kinopoisk-request-season';
                    input.value = String(season.seasonNumber);
                    label.append(input, element('span'));
                    seasons.appendChild(label);
                }
                input.disabled = seasonBlocked(item, season.seasonNumber);
                if (input.disabled) { input.checked = false; }
                input.nextElementSibling.textContent = (season.seasonNumber === 0 ? 'Спецвыпуски' : 'Сезон ' + season.seasonNumber)
                    + (input.disabled ? ' — уже запрошен или доступен' : '');
            });
            var count = item.mediaType === 'tv' ? selectedSeasons().length : 1;
            var limit = quotaState(quotaData, item.mediaType, count);
            quota.textContent = quotaLoading ? 'Проверка квоты…' : limit.text;
            submit.hidden = false;
            status.textContent = permitted === false ? 'Нет права запроса в Seerr'
                : canRequest ? (available.label === 'Запросить' ? '' : available.label + '. ')
                    + (item.mediaType === 'tv' ? 'Выберите сезоны и подтвердите запрос в Seerr.' : 'Подтвердите запрос в Seerr.') : available.label;
            if (submissionNotice) { status.textContent = 'Запрос отправлен в Seerr. ' + status.textContent; }
            submit.textContent = canRequest ? 'Запросить' : available.libraryId ? 'Открыть в Jellyfin' : available.label;
            submit.disabled = canRequest ? quotaLoading || limit.blocked : !available.libraryId;
            if (available.libraryId && !actions.querySelector('.kp-recommendation-library')) {
                var library = element('a', 'emby-button kp-recommendation-library', 'Открыть в Jellyfin');
                library.href = '#/details?id=' + encodeURIComponent(available.libraryId);
                actions.insertBefore(library, cancel);
                library.addEventListener('click', close);
            }
        }
        function refreshQuota() {
            var sequence = ++quotaSequence;
            return readQuota().catch(function () { return null; }).then(function (data) {
                if (isCurrent() && sequence === quotaSequence) {
                    quotaData = data;
                    quotaLoading = false;
                    renderActions();
                }
            });
        }
        seasons.addEventListener('change', renderActions);
        openDialog.refresh = function (result) {
            if (!isCurrent() || inFlight || uncertain) { return; }
            resolved = result;
            submissionNotice = false;
            renderActions();
            refreshQuota();
        };
        submit.addEventListener('click', function () {
            if (!resolved || inFlight || uncertain || !isCurrent()) { return; }
            var item = resolved.item;
            var available = availability(item);
            if (available.libraryId && !available.canRequest) {
                close();
                window.location.hash = (window.location.hash.indexOf('#!') === 0 ? '#!/details?id=' : '#/details?id=')
                    + encodeURIComponent(available.libraryId);
                return;
            }
            if (!available.canRequest || quotaLoading || quotaState(quotaData, item.mediaType,
                item.mediaType === 'tv' ? selectedSeasons().length : 1).blocked) { return; }
            if ((item.mediaType === 'tv' ? resolved.status.canRequestTv : resolved.status.canRequestMovie) === false) { return; }
            var enhanced = window.JellyfinEnhanced;
            var nativeUi = enhanced && enhanced.jellyseerrUI;
            var nativeApi = enhanced && enhanced.jellyseerrAPI;
            var useAdvanced = false;
            try {
                useAdvanced = requestProvider === 'enhanced' && !card.__kpAdvancedUnavailable && nativeApi
                    && typeof nativeApi.shouldShowAdvanced === 'function' && nativeApi.shouldShowAdvanced()
                    && nativeUi && (item.mediaType === 'tv' ? typeof nativeUi.showSeasonSelectionModal === 'function'
                        : typeof nativeUi.showMovieRequestModal === 'function');
            } catch (_) { /* The authenticated server path remains available. */ }
            if (useAdvanced) {
                close();
                Promise.resolve().then(function () {
                    return item.mediaType === 'tv'
                        ? nativeUi.showSeasonSelectionModal(item.id, 'tv', item.name, item)
                        : nativeUi.showMovieRequestModal(item.id, item.title, item);
                }).catch(function () {
                    card.__kpAdvancedUnavailable = true;
                    if (card.isConnected && getCurrentItemId() === ownerId && getUserId() === userId) { showDetails(card, keyboard); }
                });
                return;
            }
            if (item.mediaType === 'tv' && !selectedSeasons().length) {
                status.textContent = 'Выберите хотя бы один сезон.';
                return;
            }
            inFlight = true;
            submit.disabled = true;
            submit.textContent = 'Отправка…';
            status.textContent = 'Проверка квоты перед отправкой…';
            var posted = false;
            refreshQuota().then(function () {
                if (!isCurrent()) { throw new Error('Страница или учётная запись изменились.'); }
                var selected = selectedSeasons();
                var limit = quotaState(quotaData, item.mediaType, item.mediaType === 'tv' ? selected.length : 1);
                if (limit.blocked || (item.mediaType === 'tv' && !selected.length)) {
                    inFlight = false;
                    renderActions();
                    status.textContent = limit.blocked ? limit.text : 'Выберите хотя бы один сезон.';
                    return null;
                }
                status.textContent = 'Отправка запроса в Seerr…';
                posted = true;
                return submitRequest(item, selected, resolved.status, isCurrent);
            }).then(function (response) {
                if (!posted) { return; }
                cache.clear();
                if (!isCurrent()) { return; }
                var info = Object.assign({}, item.mediaInfo, response && response.media || {});
                // A successful response without media details confirms submission,
                // but does not tell us whether approval was automatic.
                if (!response || !response.media) { info.status = info.status === 4 ? 4 : 2; }
                if (response && Number(response.status) > 0) {
                    info.requests = [response].concat(mediaRequests(item));
                }
                if (item.mediaType === 'tv') {
                    var submitted = selectedSeasons();
                    info.seasons = asArray(info.seasons).filter(function (season) { return submitted.indexOf(Number(season.seasonNumber)) < 0; })
                        .concat(submitted.map(function (number) { return { seasonNumber: number, status: 2 }; }));
                }
                item.mediaInfo = info;
                submissionNotice = true;
                bindCard(card, resolved);
                inFlight = false;
                renderActions();
                status.textContent = 'Запрос отправлен в Seerr. ' + availability(item).label;
                refreshQuota();
                document.dispatchEvent(new CustomEvent('jellyseerr-media-requested', {
                    detail: { tmdbId: item.id, mediaType: item.mediaType }
                }));
            }).catch(function (error) {
                if (isCurrent()) {
                    inFlight = false;
                    uncertain = posted;
                    submit.textContent = 'Проверьте состояние';
                    status.textContent = String(error.message || 'Не удалось подтвердить запрос.')
                        + ' Закройте окно и откройте карточку снова для проверки состояния.';
                    cache.clear();
                }
            });
        });
        // Recheck on opening: a request could have been made in another tab.
        cache.delete('match:' + (card.dataset.kinopoiskId || source.mediaType + ':' + source.tmdbId));
        cache.delete('seerr-status');
        resolveCardData(getIntegration(), source).then(function (result) {
            if (!isCurrent()) { return; }
            if (result && result.standalone) {
                bindStandaloneCard(card);
                quota.textContent = '';
                status.textContent = result.connection.reason === 'enhanced_missing'
                    ? 'Для запросов подключите Seerr в настройках КиноПоиска. Jellyfin Enhanced не установлен.'
                    : result.connection.enhancedDetected
                        ? 'Jellyfin Enhanced обнаружен, но Seerr в нём выключен или не настроен. Можно подключить Seerr напрямую в настройках КиноПоиска.' : '';
                return;
            }
            if (!result) { throw new Error('Однозначное совпадение в Seerr не найдено. Запрос недоступен.'); }
            resolved = result;
            bindCard(card, result);
            renderActions();
            refreshQuota();
            cache.delete('seerr-link');
            getSeerrLink(result.item).then(function (url) {
                if (isCurrent() && url) { seerrLink.href = url; seerrLink.hidden = false; }
            });
        }).catch(function (error) {
            if (isCurrent()) {
                status.textContent = String(error.message || 'Seerr сейчас недоступен.');
                quota.textContent = '';
                submit.textContent = 'Запрос недоступен';
                submit.hidden = false;
                submit.disabled = true;
            }
        });
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

    function bindStandaloneCard(card) {
        card.__kpSeerrResolved = null;
        card.dataset.kpSeerrFallbackState = 'standalone';
        delete card.dataset.kpSeerrReason;
        setCardStatus(card, 'Подробнее');
        card.dataset.kpSeerrRetryAfter = String(Date.now() + 30000);
        scheduleRetry();
    }

    function processFallbackCards() {
        var itemId = getCurrentItemId();
        if (document.hidden) { return; }
        if (openDialog && (openDialog.ownerId !== itemId || openDialog.userId !== getUserId()
            || !openDialog.card.isConnected)) {
            openDialog.close();
        }
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
        var enhanced = getIntegration();
        var api = proxyApi;
        var userId = getUserId();
        if (api !== activeApi || userId !== activeUserId) {
            activeApi = api;
            activeUserId = userId;
            bindingGeneration += 1;
            cache.clear();
            Array.prototype.forEach.call(cards, function (card) {
                card.__kpSeerrResolved = null;
                delete card.dataset.kpSeerrFallbackState;
                delete card.dataset.kpSeerrRetryAfter;
            });
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
                if (!isCurrent(fallback) || fallback.dataset.kpSeerrFallbackState === 'pending'
                    || fallback.dataset.kpSeerrFallbackState === 'ready') {
                    return;
                }
                if (Number(fallback.dataset.kpSeerrRetryAfter || 0) > Date.now()) {
                    scheduleRetry();
                    return;
                }
                var item = fallback.__kinopoiskRecommendationItem || byId[String(fallback.dataset.kinopoiskId || '')];
                if (!item) {
                    scheduleRetry();
                    return;
                }
                fallback.dataset.kpSeerrFallbackState = 'pending';
                setCardStatus(fallback, 'Подробнее');
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
                            setCardStatus(fallback, 'Не найдено в Seerr');
                            scheduleRetry();
                            return;
                        }
                        if (resolved.standalone) { bindStandaloneCard(fallback); return; }
                        if (!resolved.active || !resolved.userFound) {
                            throw new Error('Seerr или учётная запись пока недоступны.');
                        }
                        if (isCurrent(fallback)) {
                            bindCard(fallback, resolved);
                        }
                    }).catch(function (error) {
                        if (isCurrent(fallback)) {
                            fallback.dataset.kpSeerrFallbackState = 'retry';
                            fallback.dataset.kpSeerrRetryAfter = String(Date.now() + 1500);
                            setCardStatus(fallback, 'Seerr недоступен');
                            fallback.dataset.kpSeerrReason = String(error.message || 'Seerr недоступен');
                            scheduleRetry();
                            console.debug('[КиноПоиск] Карточка Seerr будет обработана повторно.', error);
                        }
                    });
                });
            });
            drainCards();
        }).catch(function () { scheduleRetry(); });
    }

    function scheduleLiveRefresh(delay) {
        if (liveTimer || liveRefreshRunning || document.hidden || !getUserId() || !getCurrentItemId()) { return; }
        liveTimer = setTimeout(function () {
            liveTimer = null;
            refreshVisibleCards();
        }, delay === undefined ? Math.min(120000, 30000 * Math.pow(2, liveFailures)) : delay);
    }

    function refreshVisibleCards() {
        if (liveRefreshRunning || document.hidden) { return; }
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        var page = lifecycle ? lifecycle.getActivePage() : document;
        var userId = getUserId();
        var itemId = getCurrentItemId();
        var generation = bindingGeneration;
        if (!page || !userId || !itemId) { return; }
        var cards = Array.prototype.filter.call(page.querySelectorAll('.kp-similar-card--fallback'), function (card) {
            if (!card.__kpSeerrResolved || card.dataset.kpSeerrFallbackState !== 'ready') { return false; }
            if (openDialog && openDialog.card === card) { return true; }
            var rect = card.getBoundingClientRect();
            return rect.width > 0 && (rect.bottom > 0 && rect.top < window.innerHeight && rect.right > 0 && rect.left < window.innerWidth);
        }).slice(0, 24);
        if (!cards.length) { return; }
        function current(card) {
            return !document.hidden && userId === getUserId() && itemId === getCurrentItemId()
                && generation === bindingGeneration && card.isConnected && page.contains(card)
                && (!lifecycle || lifecycle.getActivePage() === page);
        }
        liveRefreshRunning = true;
        cache.delete('seerr-status');
        getSeerrStatus(getIntegration()).then(function (status) {
            if (!cards.some(current)) { return; }
            if (!status.active || !status.userFound) {
                cards.forEach(function (card) {
                    if (!current(card)) { return; }
                    card.__kpSeerrResolved = null;
                    delete card.dataset.kpSeerrFallbackState;
                    setCardStatus(card, 'Подробнее');
                });
                if (openDialog && openDialog.userId === userId) { openDialog.close(); }
                cache.clear();
                scheduleProcessing();
                return;
            }
            var tasks = cards.map(function (card) {
                return new Promise(function (done) {
                    pendingCards.push(function () {
                        if (!current(card)) { done(); return; }
                        var previous = card.__kpSeerrResolved;
                        return fetchDetails(getIntegration(), previous.item.id, previous.item.mediaType).then(function (details) {
                            if (!current(card)) { return; }
                            if (!details || Number(details.id) !== Number(previous.item.id)) { throw new Error('Состояние Seerr не получено.'); }
                            var resolved = { item: buildCardData(card.__kinopoiskRecommendationItem, details, previous.item.id),
                                status: status, active: true, userFound: true };
                            bindCard(card, resolved);
                            var source = card.__kinopoiskRecommendationItem;
                            cache.set('match:' + (card.dataset.kinopoiskId || source.mediaType + ':' + source.tmdbId), Promise.resolve(resolved));
                            if (openDialog && openDialog.card === card && openDialog.refresh) { openDialog.refresh(resolved); }
                        }).catch(function () { liveFailures = Math.min(2, liveFailures + 1); }).finally(done);
                    });
                });
            });
            liveFailures = 0;
            drainCards();
            return Promise.all(tasks);
        }).catch(function () { liveFailures = Math.min(2, liveFailures + 1); }).finally(function () {
            liveRefreshRunning = false;
            if (cards.some(current)) { scheduleLiveRefresh(); }
        });
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
    document.addEventListener('kinopoisk:recommendation-open', function (event) {
        showDetails(event.detail && event.detail.card, event.detail && event.detail.keyboard);
    });
    document.addEventListener('jellyseerr-media-requested', function () {
        cache.clear();
        clearTimeout(liveTimer);
        liveTimer = null;
        scheduleLiveRefresh(1500);
    });
    document.addEventListener('visibilitychange', function () {
        clearTimeout(liveTimer);
        liveTimer = null;
        if (!document.hidden) { scheduleProcessing(); scheduleLiveRefresh(100); }
    });
    window.addEventListener('pagehide', function () { clearTimeout(liveTimer); liveTimer = null; });
    document.addEventListener('scroll', function () { scheduleLiveRefresh(1000); }, true);
    window.addEventListener('resize', function () { scheduleLiveRefresh(1000); });
    document.addEventListener('focusin', function (event) {
        if (event.target.closest && event.target.closest('.kp-similar-card--fallback')) {
            retryAttempt = 0;
            scheduleProcessing();
            scheduleLiveRefresh(100);
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
