(function () {
    'use strict';

    if (window.__kinopoiskElsewhereBridgeInstalled) {
        return;
    }

    window.__kinopoiskElsewhereBridgeInstalled = true;

    var resolutionCache = new Map();
    var renderTimer = null;
    var activeItemId = null;

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
                console.warn('[КиноПоиск] Карточка для Jellyfin Elsewhere не загружена.', error);
                return null;
            });
    }

    function isElsewhereAvailable() {
        var enhanced = window.JellyfinEnhanced;
        return !!(
            enhanced
            && enhanced.pluginConfig
            && enhanced.pluginConfig.TmdbEnabled
            && enhanced.pluginConfig.ElsewhereEnabled
        );
    }

    function resolveTmdbId(imdbId, itemType) {
        var mediaType = itemType === 'Series' ? 'tv' : 'movie';
        var cacheKey = mediaType + ':' + imdbId;
        if (!resolutionCache.has(cacheKey)) {
            resolutionCache.set(
                cacheKey,
                fetchJson(
                    '/JellyfinEnhanced/tmdb/find/'
                    + encodeURIComponent(imdbId)
                    + '?external_source=imdb_id&language=ru-RU'
                ).then(function (data) {
                    var candidates = mediaType === 'tv'
                        ? asArray(data && data.tv_results)
                        : asArray(data && data.movie_results);
                    var candidate = candidates.find(function (item) {
                        return /^\d+$/.test(String(item && item.id || ''));
                    });
                    return candidate ? String(candidate.id) : null;
                }).catch(function (error) {
                    resolutionCache.delete(cacheKey);
                    throw error;
                })
            );
        }
        return resolutionCache.get(cacheKey);
    }

    function getVisiblePage() {
        return document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage:not(.hide)')
            || document;
    }

    function findDetailSection(page) {
        var sections = Array.prototype.slice.call(
            page.querySelectorAll('.detailSectionContent')
        );
        return sections.find(function (section) {
            return section.querySelector('.itemExternalLinks');
        }) || sections[0] || null;
    }

    function removeBridgeLinks(page, exceptItemId) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.kp-elsewhere-tmdb-bridge'),
            function (link) {
                if (!exceptItemId || link.dataset.kpItemId !== exceptItemId) {
                    link.remove();
                }
            }
        );
    }

    function hasTmdbLink(section) {
        return !!section.querySelector(
            'a[href*="themoviedb.org/movie/"],a[href*="themoviedb.org/tv/"]'
        );
    }

    function injectBridgeLink(section, itemId, itemType, tmdbId) {
        if (!section || hasTmdbLink(section)) {
            return;
        }

        var existing = section.querySelector(
            '.kp-elsewhere-tmdb-bridge[data-kp-item-id="' + itemId + '"]'
        );
        if (existing) {
            return;
        }

        var mediaType = itemType === 'Series' ? 'tv' : 'movie';
        var link = document.createElement('a');
        link.className = 'kp-elsewhere-tmdb-bridge';
        link.dataset.kpItemId = itemId;
        link.dataset.kpTemporaryTmdbId = tmdbId;
        link.href = 'https://www.themoviedb.org/' + mediaType + '/' + tmdbId;
        link.hidden = true;
        link.setAttribute('aria-hidden', 'true');
        link.tabIndex = -1;
        link.rel = 'noopener noreferrer';

        var externalLinks = section.querySelector('.itemExternalLinks');
        if (externalLinks) {
            externalLinks.appendChild(link);
        } else {
            section.appendChild(link);
        }
    }

    function renderCurrentItem() {
        if (!isElsewhereAvailable()) {
            return;
        }

        var itemId = getCurrentItemId();
        if (!itemId) {
            return;
        }
        var page = getVisiblePage();
        if (activeItemId !== itemId) {
            activeItemId = itemId;
            removeBridgeLinks(page, itemId);
        }

        fetchCurrentItem(itemId).then(function (item) {
            if (!item || getCurrentItemId() !== itemId || !isElsewhereAvailable()) {
                return;
            }

            var itemType = String(pick(item, 'Type', 'type') || '');
            if (itemType !== 'Movie' && itemType !== 'Series') {
                return;
            }

            var section = findDetailSection(page);
            if (!section || section.querySelector('.streaming-lookup-container')) {
                return;
            }
            if (hasTmdbLink(section)) {
                return;
            }

            var imdbId = String(getProviderId(item, 'imdb') || '').trim();
            if (!/^tt\d+$/.test(imdbId)) {
                return;
            }

            resolveTmdbId(imdbId, itemType)
                .then(function (tmdbId) {
                    if (!tmdbId || getCurrentItemId() !== itemId) {
                        return;
                    }
                    injectBridgeLink(section, itemId, itemType, tmdbId);
                })
                .catch(function (error) {
                    console.warn('[КиноПоиск] Временный TMDB ID не разрешён.', error);
                });
        });
    }

    window.KinopoiskTmdbResolver = Object.freeze({
        resolve: resolveTmdbId
    });

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentItem, 300);
    }

    var observer = new MutationObserver(scheduleRender);
    observer.observe(document.documentElement, {
        childList: true,
        subtree: true
    });
    window.addEventListener('hashchange', scheduleRender);
    window.addEventListener('popstate', scheduleRender);
    document.addEventListener('viewshow', scheduleRender, true);
    scheduleRender();
    console.info('[КиноПоиск] Совместимый мост Jellyfin Elsewhere зарегистрирован.');
}());
