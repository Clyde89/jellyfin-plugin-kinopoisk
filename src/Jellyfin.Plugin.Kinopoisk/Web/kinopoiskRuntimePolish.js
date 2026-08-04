(function () {
    'use strict';

    if (window.__kinopoiskRuntimePolishInstalled) {
        return;
    }

    window.__kinopoiskRuntimePolishInstalled = true;

    var itemCache = new Map();
    var releaseCache = new Map();
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

    function getApiClient() {
        return window.ApiClient || null;
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
        if (!itemCache.has(itemId)) {
            var apiClient = getApiClient();
            if (!apiClient || typeof apiClient.getItem !== 'function') {
                return Promise.resolve(null);
            }
            itemCache.set(
                itemId,
                apiClient.getItem(apiClient.getCurrentUserId(), itemId)
                    .catch(function (error) {
                        itemCache.delete(itemId);
                        console.warn('[КиноПоиск] Карточка для полировки интерфейса не загружена.', error);
                        return null;
                    })
            );
        }
        return itemCache.get(itemId);
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskRuntimePolishStyles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'kinopoiskRuntimePolishStyles';
        style.textContent = [
            '.kp-runtime-navigation{display:flex;align-items:center;gap:.28em;margin-left:auto}',
            '.kp-runtime-navigation-button{display:inline-flex;align-items:center;justify-content:center;width:2.45em;height:2.45em;border:0;border-radius:50%;background:rgba(0,0,0,.52);color:inherit;cursor:pointer;font:inherit}',
            '.kp-runtime-navigation-button:hover,.kp-runtime-navigation-button:focus-visible{background:rgba(0,0,0,.78);outline:2px solid rgba(255,255,255,.65);outline-offset:2px}',
            '.kp-runtime-navigation-button:disabled{opacity:.28;cursor:default;outline:0}',
            '.kp-recommendations-scroller,.tmdb-review-swipe-container{scrollbar-width:none;scroll-snap-type:x proximity;overscroll-behavior-inline:contain}',
            '.kp-recommendations-scroller::-webkit-scrollbar,.tmdb-review-swipe-container::-webkit-scrollbar{display:none}',
            '.kp-recommendations-items>* ,.tmdb-review-swipe-container>*{scroll-snap-align:start}',
            '.kp-runtime-release-date{display:flex;align-items:center;gap:.6em;flex-wrap:wrap}',
            '.kp-runtime-release-date-entry{display:inline-flex;align-items:center;gap:.28em;white-space:nowrap}',
            '.kp-runtime-release-date-label{font-weight:650}',
            '.kp-runtime-release-date-more{border:0;background:transparent;color:inherit;cursor:pointer;padding:.08em .22em}',
            '@media(max-width:600px){.kp-runtime-release-date-label{display:none}}'
        ].join('');
        document.head.appendChild(style);
    }

    function scrollCarousel(scroller, direction) {
        var distance = Math.max(320, Math.floor(scroller.clientWidth * 0.86));
        scroller.scrollBy({ left: direction * distance, behavior: 'smooth' });
    }

    function attachKeyboardNavigation(scroller) {
        if (scroller.dataset.kpKeyboardNavigation === 'true') {
            return;
        }
        scroller.dataset.kpKeyboardNavigation = 'true';
        if (!scroller.hasAttribute('tabindex')) {
            scroller.tabIndex = 0;
        }
        scroller.addEventListener('keydown', function (event) {
            if (event.key === 'ArrowLeft') {
                event.preventDefault();
                scrollCarousel(scroller, -1);
            } else if (event.key === 'ArrowRight') {
                event.preventDefault();
                scrollCarousel(scroller, 1);
            }
        });
    }

    function ensureNavigation(header, scroller, labelPrefix) {
        if (!header || !scroller) {
            return;
        }
        attachKeyboardNavigation(scroller);
        var navigation = header.querySelector('.kp-runtime-navigation');
        if (!navigation) {
            navigation = createElement('div', 'kp-runtime-navigation');
            var previous = createElement('button', 'kp-runtime-navigation-button material-icons', 'chevron_left');
            var next = createElement('button', 'kp-runtime-navigation-button material-icons', 'chevron_right');
            previous.type = 'button';
            next.type = 'button';
            previous.title = 'Предыдущие ' + labelPrefix;
            next.title = 'Следующие ' + labelPrefix;
            previous.setAttribute('aria-label', previous.title);
            next.setAttribute('aria-label', next.title);
            previous.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                scrollCarousel(scroller, -1);
            });
            next.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                scrollCarousel(scroller, 1);
            });
            navigation.append(previous, next);
            header.appendChild(navigation);

            var pending = false;
            function update() {
                pending = false;
                var maximum = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
                previous.disabled = scroller.scrollLeft <= 4;
                next.disabled = maximum <= 4 || scroller.scrollLeft >= maximum - 4;
            }
            function scheduleUpdate() {
                if (!pending) {
                    pending = true;
                    window.requestAnimationFrame(update);
                }
            }
            scroller.addEventListener('scroll', scheduleUpdate, { passive: true });
            window.addEventListener('resize', scheduleUpdate);
            navigation.__kpUpdate = scheduleUpdate;
        }
        if (typeof navigation.__kpUpdate === 'function') {
            navigation.__kpUpdate();
        }
    }

    function enhanceCarousels(page) {
        Array.prototype.forEach.call(page.querySelectorAll('.kp-recommendations-section'), function (section) {
            var header = section.querySelector('.kp-recommendations-header');
            var scroller = section.querySelector('.kp-recommendations-scroller');
            if (header && scroller) {
                ensureNavigation(header, scroller, 'рекомендации');
            }
        });

        Array.prototype.forEach.call(page.querySelectorAll('.tmdb-reviews-section'), function (section) {
            var toolbar = section.querySelector('.kp-review-toolbar');
            var scroller = section.querySelector('.tmdb-review-swipe-container');
            if (toolbar && scroller && !toolbar.querySelector('.kp-carousel-navigation')) {
                ensureNavigation(toolbar, scroller, 'рецензии');
            }
        });
    }

    function earliestOfBucket(releaseDates, types) {
        var matches = asArray(releaseDates).filter(function (entry) {
            return types.indexOf(Number(entry.type)) >= 0 && entry.release_date;
        });
        if (!matches.length) {
            return null;
        }
        matches.sort(function (left, right) {
            return String(left.release_date).localeCompare(String(right.release_date));
        });
        return matches[0];
    }

    function resolveReleaseBuckets(results) {
        var enhanced = window.JellyfinEnhanced;
        var region = String(
            enhanced && enhanced.pluginConfig && enhanced.pluginConfig.DEFAULT_REGION
                ? enhanced.pluginConfig.DEFAULT_REGION
                : 'RU'
        ).toUpperCase();
        var preferred = [region, 'US'].filter(function (value, index, array) {
            return value && array.indexOf(value) === index;
        });
        var buckets = [
            { label: 'Кино', icon: 'local_movies', types: [1, 2, 3] },
            { label: 'Цифра', icon: 'ondemand_video', types: [4] },
            { label: 'Носитель', icon: 'album', types: [5] }
        ];

        return buckets.map(function (bucket) {
            var selected = null;
            for (var preferredIndex = 0; preferredIndex < preferred.length && !selected; preferredIndex++) {
                var preferredEntry = results.find(function (entry) {
                    return entry.iso_3166_1 === preferred[preferredIndex];
                });
                selected = preferredEntry && earliestOfBucket(preferredEntry.release_dates, bucket.types);
            }
            if (!selected) {
                for (var resultIndex = 0; resultIndex < results.length && !selected; resultIndex++) {
                    selected = earliestOfBucket(results[resultIndex].release_dates, bucket.types);
                }
            }
            return selected ? {
                label: bucket.label,
                icon: bucket.icon,
                date: selected.release_date
            } : null;
        }).filter(Boolean);
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

    function fetchReleaseBuckets(item) {
        var itemId = String(pick(item, 'Id', 'id') || '');
        if (releaseCache.has(itemId)) {
            return releaseCache.get(itemId);
        }
        var promise = resolveTmdbId(item).then(function (tmdbId) {
            if (!tmdbId) {
                releaseCache.delete(itemId);
                return [];
            }
            return fetchJson('/JellyfinEnhanced/tmdb/movie/' + encodeURIComponent(String(tmdbId)) + '/release_dates')
                .then(function (data) {
                    return resolveReleaseBuckets(asArray(data && data.results));
                });
        }).catch(function (error) {
            releaseCache.delete(itemId);
            console.warn('[КиноПоиск] Резервный бар дат не сформирован.', error);
            return [];
        });
        releaseCache.set(itemId, promise);
        return promise;
    }

    function formatDate(value) {
        var normalized = /^\d{4}-\d{2}-\d{2}$/.test(String(value || ''))
            ? String(value) + 'T12:00:00'
            : String(value || '');
        var date = new Date(normalized);
        if (Number.isNaN(date.getTime())) {
            return String(value || '');
        }
        return date.toLocaleDateString('ru-RU', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric'
        });
    }

    function renderReleaseBar(container, itemId, buckets) {
        var existing = container.querySelector('.mediaInfoItem-releaseDate');
        if (existing && existing.dataset.kpRuntimeItemId === itemId) {
            return;
        }
        if (existing && existing.dataset.itemId === itemId && !existing.classList.contains('kp-runtime-release-date')) {
            return;
        }
        if (existing) {
            existing.remove();
        }
        if (!buckets.length) {
            return;
        }

        var chip = createElement('div', 'mediaInfoItem mediaInfoItem-releaseDate kp-runtime-release-date');
        chip.dataset.itemId = itemId;
        chip.dataset.kpRuntimeItemId = itemId;
        buckets.forEach(function (bucket) {
            var entry = createElement('span', 'kp-runtime-release-date-entry');
            entry.title = bucket.label;
            entry.append(
                createElement('span', 'material-icons', bucket.icon),
                createElement('span', 'kp-runtime-release-date-label', bucket.label),
                createElement('span', '', formatDate(bucket.date))
            );
            chip.appendChild(entry);
        });
        var more = createElement('button', 'kp-runtime-release-date-more material-icons', 'calendar_month');
        more.type = 'button';
        more.title = 'Показать все даты релиза';
        more.setAttribute('aria-label', more.title);
        more.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            var button = document.querySelector('#kinopoiskEnhancedPresentationPanel .kp-date-list .kp-action');
            if (button) {
                button.click();
            }
        });
        chip.appendChild(more);
        container.appendChild(chip);
    }

    function enhanceReleaseBar(page, item) {
        var itemType = String(pick(item, 'Type', 'type') || '');
        if (itemType !== 'Movie') {
            return;
        }
        var container = page.querySelector('.itemMiscInfo.itemMiscInfo-primary');
        if (!container) {
            return;
        }
        var itemId = String(pick(item, 'Id', 'id') || '');
        var existing = container.querySelector('.mediaInfoItem-releaseDate');
        if (existing && (existing.dataset.itemId === itemId || existing.dataset.kpRuntimeItemId === itemId)) {
            return;
        }
        fetchReleaseBuckets(item).then(function (buckets) {
            if (getCurrentItemId() === itemId && container.isConnected) {
                renderReleaseBar(container, itemId, buckets);
            }
        });
    }

    function renderCurrentItem() {
        var itemId = getCurrentItemId();
        if (!itemId) {
            return;
        }
        var page = document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage:not(.hide)')
            || document;
        enhanceCarousels(page);
        fetchCurrentItem(itemId).then(function (item) {
            if (!item || getCurrentItemId() !== itemId) {
                return;
            }
            enhanceReleaseBar(page, item);
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
    scheduleRender();
    console.info('[КиноПоиск] Полировка runtime-интерфейса зарегистрирована.');
}());
