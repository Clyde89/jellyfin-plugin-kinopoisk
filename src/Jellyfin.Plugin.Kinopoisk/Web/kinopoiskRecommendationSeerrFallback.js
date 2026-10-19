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
    var cardStates = new WeakMap();
    var openDialog = null;

    // Use the authenticated server proxy even when Enhanced's UI modules are
    // absent or failed to initialize. No Seerr API key is exposed to the browser.
    function proxy(path, body) {
        var client = window.ApiClient;
        if (!client || typeof client.getUrl !== 'function') {
            return Promise.reject(new Error('Сеанс Jellyfin ещё не готов.'));
        }
        var controller = new AbortController();
        var timer = body === undefined ? setTimeout(function () { controller.abort(); }, 20000) : null;
        var headers = getAuthHeaders(client);
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
        }
        return fetch(client.getUrl('/JellyfinEnhanced/jellyseerr' + path), {
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

    function callApi(name, args, fallback) {
        var api = window.JellyfinEnhanced && window.JellyfinEnhanced.jellyseerrAPI;
        return api && typeof api[name] === 'function'
            ? Promise.resolve().then(function () { return api[name].apply(api, args); })
            : fallback();
    }

    var proxyApi = {
        checkUserStatus: function () {
            return callApi('checkUserStatus', [], function () { return proxy('/user-status'); });
        },
        search: function (query, page, options) {
            return callApi('search', [query, page, options], function () {
                return proxy('/search?query=' + encodeURIComponent(query) + '&page=1');
            });
        },
        fetchMovieDetails: function (id) {
            return callApi('fetchMovieDetails', [id], function () { return proxy('/movie/' + id); });
        },
        fetchTvShowDetails: function (id) {
            return callApi('fetchTvShowDetails', [id], function () { return proxy('/tv/' + id); });
        }
    };

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
            var pending = Promise.resolve(request).then(function (status) {
                if ((!status || !status.active || !status.userFound) && cache.get(key) === pending) {
                    cache.delete(key);
                }
                return status;
            }, function (error) {
                if (cache.get(key) === pending) { cache.delete(key); }
                throw error;
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
            var status;
            var request = getSeerrStatus(enhanced).then(function (value) {
                status = value || {};
                if (!status.active || !status.userFound) {
                    var reasons = {
                        disabled: 'Интеграция Seerr отключена в Jellyfin Enhanced.',
                        unlinked: 'Учётная запись Jellyfin не связана с Seerr.',
                        blocked: 'Для этой учётной записи доступ к Seerr запрещён.',
                        no_user: 'Войдите в Jellyfin для запроса.',
                        unreachable: 'Seerr сейчас недоступен.'
                    };
                    throw new Error(reasons[status.reason] || 'Seerr или учётная запись сейчас недоступны.');
                }
                return resolveTmdbId(enhanced, item);
            }).then(function (resolvedId) {
                var tmdbId = String(resolvedId || '');
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

    function element(tag, className, text) {
        var node = document.createElement(tag);
        node.className = className || '';
        if (text !== undefined) { node.textContent = text; }
        return node;
    }

    function availability(item) {
        var info = item.mediaInfo || {};
        var status = Number(info.status || 1);
        var libraryId = String(info.jellyfinMediaId || (item.mediaType === 'tv' && info.jellyfinSeriesId) || '');
        return {
            label: status === 5 ? 'Доступно' : status === 2 ? 'Запрошено'
                : status === 3 ? 'Загружается' : status === 4 ? 'Частично доступно' : 'Запрос',
            canRequest: status !== 2 && status !== 3 && status !== 5,
            libraryId: /^[a-z0-9-]+$/i.test(libraryId) ? libraryId : ''
        };
    }

    function bindCard(card, resolved) {
        cardStates.set(card, resolved);
        card.dataset.kpSeerrFallbackState = 'ready';
        card.dataset.tmdbId = String(resolved.item.id);
        setCardStatus(card, availability(resolved.item).label);
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
        var api = window.JellyfinEnhanced && window.JellyfinEnhanced.jellyseerrAPI;
        if (!isCurrent()) { return Promise.reject(new Error('Страница или учётная запись изменились.')); }
        if (item.mediaType === 'tv' && api && typeof api.requestTvSeasons === 'function') {
            return api.requestTvSeasons(item.id, seasons, {}, item);
        }
        if (item.mediaType === 'movie' && api && typeof api.requestMedia === 'function') {
            return api.requestMedia(item.id, 'movie', {}, false, item);
        }
        return requestDefaults(item, userStatus).then(function (settings) {
            if (!isCurrent()) { throw new Error('Страница или учётная запись изменились.'); }
            return proxy('/request', Object.assign({}, settings, {
                mediaType: item.mediaType,
                mediaId: Number(item.id)
            }, item.mediaType === 'tv' ? { seasons: seasons } : {}));
        });
    }

    function ensureDialogStyles() {
        if (document.getElementById('kinopoiskRecommendationDialogStyles')) { return; }
        var style = element('style');
        style.id = 'kinopoiskRecommendationDialogStyles';
        style.textContent = '.kp-recommendation-dialog{box-sizing:border-box;width:min(38em,calc(100vw - 2em));max-height:calc(100dvh - 2em);padding:1.5em;border:0;border-radius:.3em;overflow:auto;color:inherit;background:#202020}'
            + '.kp-recommendation-dialog::backdrop{background:rgba(0,0,0,.65)}'
            + '.kp-recommendation-dialog h2{margin-top:0}.kp-recommendation-dialog p{line-height:1.5;white-space:pre-wrap}'
            + '.kp-recommendation-dialog__actions{display:flex;align-items:center;justify-content:flex-end;gap:.6em;flex-wrap:wrap;margin-top:1.4em}'
            + '.kp-recommendation-dialog__seasons{display:grid;gap:.7em;margin:1em 0}.kp-recommendation-dialog__seasons label{display:flex;gap:.7em;align-items:center}'
            + '.kp-recommendation-dialog__status{line-height:1.5;margin-top:1em}';
        document.head.appendChild(style);
    }

    function showDetails(card) {
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
        var status = element('div', 'kp-recommendation-dialog__status', 'Проверка Seerr…');
        status.setAttribute('role', 'status');
        dialog.appendChild(status);
        var seasons = element('div', 'kp-recommendation-dialog__seasons');
        dialog.appendChild(seasons);
        var actions = element('div', 'kp-recommendation-dialog__actions');
        var kpLink = element('a', 'emby-button', 'КиноПоиск');
        kpLink.href = 'https://www.kinopoisk.ru/film/' + encodeURIComponent(card.dataset.kinopoiskId) + '/';
        kpLink.target = '_blank';
        kpLink.rel = 'noopener noreferrer';
        var cancel = element('button', 'emby-button', 'Закрыть');
        cancel.type = 'button';
        var submit = element('button', 'emby-button raised button-submit kp-recommendation-dialog__submit', 'Запрос');
        submit.type = 'button';
        submit.disabled = true;
        actions.append(kpLink, cancel, submit);
        dialog.appendChild(actions);
        document.body.appendChild(dialog);
        var closed = false;
        function isCurrent() {
            return !closed && getCurrentItemId() === ownerId && getUserId() === userId
                && card.isConnected && dialog.isConnected;
        }
        function close() {
            closed = true;
            if (typeof dialog.close === 'function' && dialog.open) { dialog.close(); }
            dialog.remove();
            if (openDialog && openDialog.element === dialog) { openDialog = null; }
            if (returnFocus && returnFocus.isConnected) { returnFocus.focus(); }
        }
        openDialog = { element: dialog, close: close, ownerId: ownerId, userId: userId, card: card };
        cancel.addEventListener('click', close);
        dialog.addEventListener('cancel', function (event) { event.preventDefault(); close(); });
        if (typeof dialog.showModal === 'function') { dialog.showModal(); }
        else { dialog.setAttribute('open', ''); }
        cancel.focus();
        var resolved;
        var inFlight = false;
        submit.addEventListener('click', function () {
            if (!resolved || inFlight || !isCurrent()) { return; }
            var item = resolved.item;
            var available = availability(item);
            if (available.libraryId && !available.canRequest) {
                close();
                window.location.hash = (window.location.hash.indexOf('#!') === 0 ? '#!/details?id=' : '#/details?id=')
                    + encodeURIComponent(available.libraryId);
                return;
            }
            if (!available.canRequest) { return; }
            var enhanced = window.JellyfinEnhanced;
            var nativeUi = enhanced && enhanced.jellyseerrUI;
            var nativeApi = enhanced && enhanced.jellyseerrAPI;
            if (nativeApi && typeof nativeApi.shouldShowAdvanced === 'function' && nativeApi.shouldShowAdvanced()
                && nativeUi && (item.mediaType === 'tv' ? typeof nativeUi.showSeasonSelectionModal === 'function'
                    : typeof nativeUi.showMovieRequestModal === 'function')) {
                close();
                if (item.mediaType === 'tv') { nativeUi.showSeasonSelectionModal(item.id, 'tv', item.name, item); }
                else { nativeUi.showMovieRequestModal(item.id, item.title, item); }
                return;
            }
            var selected = Array.prototype.map.call(seasons.querySelectorAll('input:checked'), function (input) { return Number(input.value); });
            if (item.mediaType === 'tv' && !selected.length) {
                status.textContent = 'Выберите хотя бы один сезон.';
                return;
            }
            inFlight = true;
            submit.disabled = true;
            submit.textContent = 'Отправка…';
            status.textContent = 'Отправка запроса в Seerr…';
            Promise.resolve().then(function () {
                return submitRequest(item, selected, resolved.status, isCurrent);
            }).then(function () {
                cache.clear();
                item.mediaInfo = Object.assign({}, item.mediaInfo, { status: 2 });
                bindCard(card, resolved);
                document.dispatchEvent(new CustomEvent('jellyseerr-media-requested', {
                    detail: { tmdbId: item.id, mediaType: item.mediaType }
                }));
                if (isCurrent()) {
                    submit.textContent = 'Запрошено';
                    status.textContent = 'Запрос отправлен в Seerr.';
                    Array.prototype.forEach.call(seasons.querySelectorAll('input'), function (input) { input.disabled = true; });
                }
            }).catch(function (error) {
                if (isCurrent()) {
                    // Never repeat a POST automatically: even a network failure
                    // can mean the request reached Seerr. Reopen to check status.
                    submit.textContent = 'Проверьте состояние';
                    status.textContent = String(error.message || 'Не удалось подтвердить запрос.')
                        + ' Закройте окно и откройте карточку снова для проверки состояния.';
                    cardStates.delete(card);
                    cache.clear();
                }
            });
        });
        // Recheck on opening: a request could have been made in another tab.
        cache.delete('match:' + card.dataset.kinopoiskId);
        cache.delete('seerr-status');
        resolveCardData(getIntegration(), source).then(function (result) {
            if (!isCurrent()) { return; }
            if (!result) { throw new Error('Однозначное совпадение в Seerr не найдено. Запрос недоступен.'); }
            resolved = result;
            bindCard(card, result);
            var item = result.item;
            var available = availability(item);
            status.textContent = available.canRequest ? 'Подтвердите запрос в Seerr.' : available.label;
            submit.textContent = available.canRequest ? 'Запрос' : available.libraryId ? 'Открыть в Jellyfin' : available.label;
            submit.disabled = !available.libraryId && !available.canRequest;
            if (available.libraryId && available.canRequest) {
                var library = element('a', 'emby-button', 'Открыть в Jellyfin');
                library.href = '#/details?id=' + encodeURIComponent(available.libraryId);
                actions.insertBefore(library, cancel);
                library.addEventListener('click', close);
            }
            if (item.mediaType === 'tv' && available.canRequest) {
                var known = asArray(item.mediaInfo && item.mediaInfo.seasons);
                var config = window.JellyfinEnhanced && window.JellyfinEnhanced.pluginConfig;
                var choices = asArray(item.seasons).filter(function (season) {
                    return Number.isInteger(season.seasonNumber) && season.seasonNumber >= 0 && Number(season.episodeCount) > 0;
                });
                choices.forEach(function (season) {
                    var state = known.find(function (value) { return value.seasonNumber === season.seasonNumber; });
                    var label = element('label');
                    var input = element('input');
                    input.type = config && config.JellyseerrSingleSeasonRequestOnly ? 'radio' : 'checkbox';
                    input.name = 'kinopoisk-request-season';
                    input.value = String(season.seasonNumber);
                    input.disabled = !!state && [2, 3, 5].indexOf(Number(state.status)) >= 0;
                    label.append(input, element('span', '', (season.seasonNumber === 0 ? 'Спецвыпуски' : 'Сезон ' + season.seasonNumber)
                        + (input.disabled ? ' — уже запрошен или доступен' : '')));
                    seasons.appendChild(label);
                });
                if (!seasons.querySelector('input:not(:disabled)')) {
                    submit.disabled = true;
                    status.textContent = 'Нет доступных для запроса сезонов.';
                } else {
                    status.textContent = 'Выберите сезоны и подтвердите запрос в Seerr.';
                }
            }
        }).catch(function (error) {
            if (isCurrent()) {
                status.textContent = String(error.message || 'Seerr сейчас недоступен.');
                submit.textContent = 'Запрос недоступен';
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

    function processFallbackCards() {
        var itemId = getCurrentItemId();
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
        var api = (window.JellyfinEnhanced && window.JellyfinEnhanced.jellyseerrAPI) || proxyApi;
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
                            setCardStatus(fallback, 'Не найдено в Seerr');
                            scheduleRetry();
                            return;
                        }
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
        showDetails(event.detail && event.detail.card);
    });
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
