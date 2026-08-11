(function () {
    'use strict';

    if (window.KinopoiskDetailPageLifecycle) {
        return;
    }

    var subscribers = new Set();
    var itemCache = new Map();
    var imageCache = new Map();
    var retryTimers = [];
    var renderTimer = null;
    var generation = 0;
    var activeContext = null;
    var maximumImageCacheEntries = 120;
    var retryDelays = [0, 120, 350, 800, 1600, 3200];

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

    function isVisiblePage(page) {
        if (!page || !page.isConnected || page.hidden || page.classList.contains('hide')) {
            return false;
        }
        var style = window.getComputedStyle(page);
        return style.display !== 'none'
            && style.visibility !== 'hidden'
            && style.opacity !== '0';
    }

    function getActivePage() {
        var pages = document.querySelectorAll('#itemDetailPage, .itemDetailPage');
        for (var index = pages.length - 1; index >= 0; index--) {
            if (isVisiblePage(pages[index])) {
                return pages[index];
            }
        }
        return null;
    }

    function getApiClient() {
        return window.ApiClient || null;
    }

    function getAuthHeaders(accept) {
        var apiClient = getApiClient();
        var token = apiClient && typeof apiClient.accessToken === 'function'
            ? apiClient.accessToken()
            : '';
        return {
            'Authorization': 'MediaBrowser Token="' + token + '"',
            'X-Emby-Token': token,
            'Accept': accept || 'application/json'
        };
    }

    function fetchJson(path) {
        var apiClient = getApiClient();
        if (!apiClient) {
            return Promise.reject(new Error('ApiClient недоступен.'));
        }
        return fetch(apiClient.getUrl(path), {
            credentials: 'same-origin',
            headers: getAuthHeaders('application/json')
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        });
    }

    function fetchItem(itemId) {
        var apiClient = getApiClient();
        if (!apiClient || !itemId || typeof apiClient.getItem !== 'function') {
            return Promise.resolve(null);
        }
        if (!itemCache.has(itemId)) {
            itemCache.set(
                itemId,
                apiClient.getItem(apiClient.getCurrentUserId(), itemId)
                    .catch(function (error) {
                        itemCache.delete(itemId);
                        console.warn('[КиноПоиск] Активная карточка Jellyfin не загружена.', error);
                        return null;
                    })
            );
            if (itemCache.size > 50) {
                itemCache.delete(itemCache.keys().next().value);
            }
        }
        return itemCache.get(itemId);
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
            parsed.port = '';
            parsed.hash = '';
            return parsed.href;
        } catch (_) {
            return null;
        }
    }

    function wait(delay) {
        return new Promise(function (resolve) {
            setTimeout(resolve, delay);
        });
    }

    function fetchProtectedImage(sourceUrl) {
        var normalized = normalizeImageUrl(sourceUrl);
        if (!normalized) {
            return Promise.reject(new Error('Недопустимый URL изображения.'));
        }
        if (imageCache.has(normalized)) {
            return imageCache.get(normalized);
        }

        var request = (function requestWithRetry(attempt) {
            var apiClient = getApiClient();
            if (!apiClient) {
                return Promise.reject(new Error('ApiClient недоступен.'));
            }
            var path = '/KinopoiskPresentation/image?url=' + encodeURIComponent(normalized);
            return fetch(apiClient.getUrl(path), {
                credentials: 'same-origin',
                headers: getAuthHeaders('image/*')
            }).then(function (response) {
                if (!response.ok) {
                    var error = new Error('HTTP ' + response.status);
                    error.status = response.status;
                    throw error;
                }
                var contentType = String(response.headers.get('content-type') || '');
                if (contentType.indexOf('image/') !== 0) {
                    throw new Error('Сервер вернул не изображение.');
                }
                return response.blob();
            }).then(function (blob) {
                return URL.createObjectURL(blob);
            }).catch(function (error) {
                var retryable = !error.status || error.status === 429 || error.status >= 500;
                if (retryable && attempt < 2) {
                    return wait(attempt === 0 ? 350 : 1100)
                        .then(function () { return requestWithRetry(attempt + 1); });
                }
                throw error;
            });
        }(0)).catch(function (error) {
            imageCache.delete(normalized);
            throw error;
        });

        imageCache.set(normalized, request);
        if (imageCache.size > maximumImageCacheEntries) {
            var oldest = imageCache.keys().next().value;
            var oldRequest = imageCache.get(oldest);
            imageCache.delete(oldest);
            Promise.resolve(oldRequest).then(function (objectUrl) {
                URL.revokeObjectURL(objectUrl);
            }).catch(function () { });
        }
        return request;
    }

    function applyProtectedImage(element, sourceUrl, mode) {
        if (!element) {
            return Promise.resolve(false);
        }
        var normalized = normalizeImageUrl(sourceUrl);
        if (!normalized) {
            element.classList.add('kp-image-unavailable');
            return Promise.resolve(false);
        }
        element.dataset.kpImageSource = normalized;
        element.classList.add('kp-image-loading');
        return fetchProtectedImage(normalized).then(function (objectUrl) {
            if (!element.isConnected || element.dataset.kpImageSource !== normalized) {
                return false;
            }
            if (mode === 'background') {
                element.style.backgroundImage = 'url("' + objectUrl + '")';
            } else {
                element.src = objectUrl;
            }
            element.classList.remove('kp-image-loading', 'kp-image-unavailable');
            element.classList.add('kp-image-loaded');
            return true;
        }).catch(function (error) {
            if (element.dataset.kpImageSource === normalized) {
                element.classList.remove('kp-image-loading');
                element.classList.add('kp-image-unavailable');
            }
            console.warn('[КиноПоиск] Изображение не загружено через локальный кэш.', error);
            return false;
        });
    }

    function removeInactiveRuntimeElements(page) {
        var selectors = [
            '#kinopoiskEnhancedPresentationPanel',
            '#kinopoiskRecommendationsSection',
            '.kp-standalone-release-dates',
            '.kp-reviews-fallback',
            '.kp-review-card',
            '.kp-review-toolbar',
            '.kp-review-pager',
            '.kp-review-status',
            '.kp-elsewhere-tmdb-bridge',
            '.kp-tags-details'
        ].join(',');
        Array.prototype.forEach.call(document.querySelectorAll(selectors), function (element) {
            if (!page || !page.contains(element)) {
                element.remove();
            }
        });
    }

    function notify(context) {
        subscribers.forEach(function (subscriber) {
            try {
                subscriber(context);
            } catch (error) {
                console.warn('[КиноПоиск] Ошибка обработчика активной карточки.', error);
            }
        });
        document.dispatchEvent(new CustomEvent('kinopoisk:detail-context', {
            detail: context
        }));
    }

    function refresh() {
        renderTimer = null;
        var itemId = getCurrentItemId();
        var page = getActivePage();
        if (!itemId || !page) {
            if (activeContext && activeContext.abortController) {
                activeContext.abortController.abort();
            }
            activeContext = null;
            removeInactiveRuntimeElements(null);
            return;
        }

        var changed = !activeContext
            || activeContext.itemId !== itemId
            || activeContext.page !== page;
        if (changed) {
            if (activeContext && activeContext.abortController) {
                activeContext.abortController.abort();
            }
            generation += 1;
            activeContext = {
                itemId: itemId,
                page: page,
                generation: generation,
                abortController: new AbortController()
            };
            activeContext.signal = activeContext.abortController.signal;
            activeContext.isCurrent = function () {
                return activeContext
                    && activeContext.itemId === itemId
                    && activeContext.page === page
                    && activeContext.generation === generation
                    && !activeContext.signal.aborted;
            };
            removeInactiveRuntimeElements(page);
        }
        notify(activeContext);
    }

    function clearRetryTimers() {
        retryTimers.forEach(function (timer) { clearTimeout(timer); });
        retryTimers = [];
    }

    function requestRender(options) {
        var navigation = options && options.navigation;
        if (navigation) {
            clearRetryTimers();
            retryDelays.forEach(function (delay) {
                retryTimers.push(setTimeout(refresh, delay));
            });
        }
        if (renderTimer) {
            return;
        }
        renderTimer = setTimeout(refresh, navigation ? 0 : 80);
    }

    function subscribe(subscriber) {
        subscribers.add(subscriber);
        if (activeContext) {
            subscriber(activeContext);
        }
        requestRender();
        return function () { subscribers.delete(subscriber); };
    }

    function patchHistory(methodName) {
        var original = history[methodName];
        if (typeof original !== 'function' || original.__kinopoiskPatched) {
            return;
        }
        var patched = function () {
            var result = original.apply(this, arguments);
            requestRender({ navigation: true });
            return result;
        };
        patched.__kinopoiskPatched = true;
        patched.__kinopoiskOriginal = original;
        history[methodName] = patched;
    }

    window.KinopoiskDetailPageLifecycle = Object.freeze({
        getCurrentItemId: getCurrentItemId,
        getActivePage: getActivePage,
        getActiveContext: function () { return activeContext; },
        getAuthHeaders: getAuthHeaders,
        fetchJson: fetchJson,
        fetchItem: fetchItem,
        normalizeImageUrl: normalizeImageUrl,
        applyProtectedImage: applyProtectedImage,
        subscribe: subscribe,
        requestRender: requestRender
    });

    patchHistory('pushState');
    patchHistory('replaceState');
    var observer = new MutationObserver(function () { requestRender(); });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('hashchange', function () { requestRender({ navigation: true }); });
    window.addEventListener('popstate', function () { requestRender({ navigation: true }); });
    document.addEventListener('viewshow', function () { requestRender({ navigation: true }); }, true);
    requestRender({ navigation: true });
    console.info('[КиноПоиск] Единый жизненный цикл карточки зарегистрирован.');
}());
