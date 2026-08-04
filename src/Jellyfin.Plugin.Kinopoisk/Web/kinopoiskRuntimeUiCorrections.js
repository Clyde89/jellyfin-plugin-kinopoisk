(function () {
    'use strict';

    if (window.__kinopoiskRuntimeUiCorrectionsInstalled) {
        return;
    }

    window.__kinopoiskRuntimeUiCorrectionsInstalled = true;

    var itemCache = new Map();
    var releaseCache = new Map();
    var renderTimer = null;
    var scrollerSequence = 0;

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
                        console.warn('[КиноПоиск] Карточка для корректировки интерфейса не загружена.', error);
                        return null;
                    })
            );
        }
        return itemCache.get(itemId);
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskRuntimeUiCorrectionsStyles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'kinopoiskRuntimeUiCorrectionsStyles';
        style.textContent = [
            '.kp-runtime-navigation[hidden],.kp-carousel-navigation[hidden]{display:none!important}',
            '.kp-runtime-navigation-button,.kp-carousel-button{font-family:inherit!important;font-size:1.8em!important;font-weight:400!important;line-height:1!important;padding:0!important}',
            '.kp-runtime-release-marker{display:none!important}',
            '.kp-runtime-release-row{display:flex;align-items:center;gap:.65em;flex-wrap:wrap;margin-top:.45em;width:100%}',
            '.kp-runtime-release-row .kp-runtime-release-date{display:flex;align-items:center;gap:.85em;flex-wrap:wrap;width:max-content;max-width:100%;padding:.38em .68em;border-radius:.55em;background:rgba(0,0,0,.34)}',
            '.kp-runtime-release-row .kp-runtime-release-date-entry{display:inline-flex;align-items:center;gap:.3em;white-space:nowrap}',
            '.kp-runtime-release-row .kp-runtime-release-date-label{font-weight:650}',
            '.kp-runtime-release-row .kp-runtime-release-date-value.is-missing{opacity:.58}',
            '.kp-runtime-release-row .kp-runtime-release-date-more{display:inline-flex;align-items:center;justify-content:center;border:0;background:transparent;color:inherit;cursor:pointer;padding:.08em .22em}',
            '@media(hover:hover) and (pointer:fine){.kp-recommendations-section .kp-runtime-navigation,.tmdb-reviews-section .kp-carousel-navigation,.tmdb-reviews-section .kp-runtime-navigation{opacity:0;pointer-events:none;transition:opacity .16s ease}.kp-recommendations-section:hover .kp-runtime-navigation,.kp-recommendations-section:focus-within .kp-runtime-navigation,.tmdb-reviews-section:hover .kp-carousel-navigation,.tmdb-reviews-section:focus-within .kp-carousel-navigation,.tmdb-reviews-section:hover .kp-runtime-navigation,.tmdb-reviews-section:focus-within .kp-runtime-navigation{opacity:1;pointer-events:auto}}',
            '@media(max-width:600px){.kp-runtime-release-row .kp-runtime-release-date-label{display:none}.kp-runtime-release-row .kp-runtime-release-date{gap:.55em}}'
        ].join('');
        document.head.appendChild(style);
    }

    function getScrollerId(scroller) {
        if (!scroller.dataset.kpUiCorrectionsScrollerId) {
            scrollerSequence += 1;
            scroller.dataset.kpUiCorrectionsScrollerId = 'kp-ui-scroller-' + String(scrollerSequence);
        }
        return scroller.dataset.kpUiCorrectionsScrollerId;
    }

    function normalizeNavigation(navigation, scroller, labelPrefix) {
        if (!navigation || !scroller) {
            return;
        }

        var buttons = navigation.querySelectorAll('button');
        if (buttons.length < 2) {
            return;
        }

        var previous = buttons[0];
        var next = buttons[1];
        previous.classList.remove('material-icons');
        next.classList.remove('material-icons');
        previous.textContent = '‹';
        next.textContent = '›';
        previous.title = 'Предыдущие ' + labelPrefix;
        next.title = 'Следующие ' + labelPrefix;
        previous.setAttribute('aria-label', previous.title);
        next.setAttribute('aria-label', next.title);

        var bindingId = getScrollerId(scroller);
        if (navigation.dataset.kpUiCorrectionsBinding === bindingId) {
            if (typeof navigation.__kpUiCorrectionsUpdate === 'function') {
                navigation.__kpUiCorrectionsUpdate();
            }
            return;
        }
        navigation.dataset.kpUiCorrectionsBinding = bindingId;

        var pending = false;
        function update() {
            pending = false;
            var maximum = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
            var hasOverflow = maximum > 4;
            navigation.hidden = !hasOverflow;
            previous.disabled = !hasOverflow || scroller.scrollLeft <= 4;
            next.disabled = !hasOverflow || scroller.scrollLeft >= maximum - 4;
        }
        function scheduleUpdate() {
            if (!pending) {
                pending = true;
                window.requestAnimationFrame(update);
            }
        }

        scroller.addEventListener('scroll', scheduleUpdate, { passive: true });
        window.addEventListener('resize', scheduleUpdate);

        if (typeof ResizeObserver === 'function') {
            var resizeObserver = new ResizeObserver(scheduleUpdate);
            resizeObserver.observe(scroller);
            navigation.__kpUiCorrectionsResizeObserver = resizeObserver;
        }

        var mutationObserver = new MutationObserver(scheduleUpdate);
        mutationObserver.observe(scroller, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['hidden', 'style', 'class']
        });
        navigation.__kpUiCorrectionsMutationObserver = mutationObserver;
        navigation.__kpUiCorrectionsUpdate = scheduleUpdate;
        scheduleUpdate();
    }

    function patchCarousels(page) {
        Array.prototype.forEach.call(page.querySelectorAll('.kp-recommendations-section'), function (section) {
            normalizeNavigation(
                section.querySelector('.kp-runtime-navigation'),
                section.querySelector('.kp-recommendations-scroller'),
                'рекомендации'
            );
        });
        Array.prototype.forEach.call(page.querySelectorAll('.tmdb-reviews-section'), function (section) {
            var toolbar = section.querySelector('.kp-review-toolbar');
            normalizeNavigation(
                toolbar && (
                    toolbar.querySelector('.kp-carousel-navigation')
                    || toolbar.querySelector('.kp-runtime-navigation')
                ),
                section.querySelector('.tmdb-review-swipe-container'),
                'рецензии'
            );
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
        var definitions = [
            { label: 'Кино', icon: 'local_movies', types: [1, 2, 3] },
            { label: 'Цифра', icon: 'ondemand_video', types: [4] },
            { label: 'Носитель', icon: 'album', types: [5] }
        ];

        return definitions.map(function (definition) {
            var selected = null;
            for (var preferredIndex = 0; preferredIndex < preferred.length && !selected; preferredIndex++) {
                var preferredEntry = results.find(function (entry) {
                    return entry.iso_3166_1 === preferred[preferredIndex];
                });
                selected = preferredEntry
                    && earliestOfBucket(preferredEntry.release_dates, definition.types);
            }
            if (!selected) {
                for (var resultIndex = 0; resultIndex < results.length && !selected; resultIndex++) {
                    selected = earliestOfBucket(results[resultIndex].release_dates, definition.types);
                }
            }
            return {
                label: definition.label,
                icon: definition.icon,
                date: selected ? selected.release_date : null
            };
        });
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
            return fetchJson(
                '/JellyfinEnhanced/tmdb/movie/'
                + encodeURIComponent(String(tmdbId))
                + '/release_dates'
            ).then(function (data) {
                return resolveReleaseBuckets(asArray(data && data.results));
            });
        }).catch(function (error) {
            releaseCache.delete(itemId);
            console.warn('[КиноПоиск] Отдельный бар дат не сформирован.', error);
            return [];
        });
        releaseCache.set(itemId, promise);
        return promise;
    }

    function formatDate(value) {
        if (!value) {
            return '—';
        }
        var normalized = /^\d{4}-\d{2}-\d{2}$/.test(String(value))
            ? String(value) + 'T12:00:00'
            : String(value);
        var date = new Date(normalized);
        if (Number.isNaN(date.getTime())) {
            return String(value);
        }
        return date.toLocaleDateString('ru-RU', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric'
        });
    }

    function hasReadableReleaseDates(element) {
        var text = String(element && element.textContent || '');
        return /(Кино|Цифра|Носитель)/.test(text)
            && /\d{2}\.\d{2}\.\d{4}/.test(text);
    }

    function ensureRuntimeMarker(primary, itemId) {
        var marker = primary.querySelector('.kp-runtime-release-marker');
        if (!marker) {
            marker = createElement(
                'span',
                'mediaInfoItem-releaseDate kp-runtime-release-marker'
            );
            primary.appendChild(marker);
        }
        marker.dataset.itemId = itemId;
        marker.dataset.kpRuntimeItemId = itemId;
    }

    function removeIncompleteInlineDates(primary, itemId) {
        Array.prototype.forEach.call(
            primary.querySelectorAll('.mediaInfoItem-releaseDate:not(.kp-runtime-release-marker)'),
            function (element) {
                var belongsToItem = !element.dataset.itemId || element.dataset.itemId === itemId;
                if (belongsToItem && !hasReadableReleaseDates(element)) {
                    element.remove();
                }
            }
        );
    }

    function renderReleaseRow(page, primary, itemId, buckets) {
        var existing = page.querySelector('.kp-runtime-release-row');
        if (existing && existing.dataset.kpRuntimeItemId === itemId) {
            return;
        }
        if (existing) {
            existing.remove();
        }

        var hasAnyDate = buckets.some(function (bucket) {
            return !!bucket.date;
        });
        if (!hasAnyDate || !primary.parentNode) {
            return;
        }

        removeIncompleteInlineDates(primary, itemId);
        ensureRuntimeMarker(primary, itemId);

        var row = createElement('div', 'itemMiscInfo kp-runtime-release-row');
        row.dataset.kpRuntimeItemId = itemId;
        var chip = createElement('div', 'mediaInfoItem-releaseDate kp-runtime-release-date');
        chip.dataset.itemId = itemId;

        buckets.forEach(function (bucket) {
            var entry = createElement('span', 'kp-runtime-release-date-entry');
            var value = createElement(
                'span',
                'kp-runtime-release-date-value' + (bucket.date ? '' : ' is-missing'),
                formatDate(bucket.date)
            );
            entry.title = bucket.label;
            entry.append(
                createElement('span', 'material-icons', bucket.icon),
                createElement('span', 'kp-runtime-release-date-label', bucket.label),
                value
            );
            chip.appendChild(entry);
        });

        var more = createElement(
            'button',
            'kp-runtime-release-date-more material-icons',
            'calendar_month'
        );
        more.type = 'button';
        more.title = 'Показать все даты релиза';
        more.setAttribute('aria-label', more.title);
        more.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            var button = document.querySelector(
                '#kinopoiskEnhancedPresentationPanel .kp-date-list .kp-action'
            );
            if (button) {
                button.click();
            }
        });
        chip.appendChild(more);
        row.appendChild(chip);
        primary.parentNode.insertBefore(row, primary.nextSibling);
    }

    function patchReleaseRow(page, item) {
        if (String(pick(item, 'Type', 'type') || '') !== 'Movie') {
            return;
        }

        var primary = page.querySelector('.itemMiscInfo.itemMiscInfo-primary');
        if (!primary) {
            return;
        }

        var itemId = String(pick(item, 'Id', 'id') || '');
        var completeStandard = Array.prototype.some.call(
            primary.querySelectorAll('.mediaInfoItem-releaseDate:not(.kp-runtime-release-marker)'),
            hasReadableReleaseDates
        );
        if (completeStandard) {
            var runtimeRow = page.querySelector('.kp-runtime-release-row');
            if (runtimeRow) {
                runtimeRow.remove();
            }
            return;
        }

        fetchReleaseBuckets(item).then(function (buckets) {
            if (getCurrentItemId() === itemId && primary.isConnected) {
                renderReleaseRow(page, primary, itemId, buckets);
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
        patchCarousels(page);

        fetchCurrentItem(itemId).then(function (item) {
            if (item && getCurrentItemId() === itemId) {
                patchReleaseRow(page, item);
            }
        });
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentItem, 180);
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
    console.info('[КиноПоиск] Корректировки runtime-интерфейса зарегистрированы.');
}());
