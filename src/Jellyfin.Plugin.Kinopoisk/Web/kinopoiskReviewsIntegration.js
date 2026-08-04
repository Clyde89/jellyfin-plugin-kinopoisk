(function () {
    'use strict';

    if (window.__kinopoiskReviewsIntegrationInstalled) {
        return;
    }

    window.__kinopoiskReviewsIntegrationInstalled = true;

    var reviewCache = new Map();
    var reviewStates = new Map();
    var renderTimer = null;

    var reviewTypeLabels = {
        POSITIVE: 'Положительная',
        NEGATIVE: 'Отрицательная',
        NEUTRAL: 'Нейтральная',
        UNKNOWN: 'Без оценки'
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
        var apiClient = getApiClient();
        if (!apiClient || !itemId || typeof apiClient.getItem !== 'function') {
            return Promise.resolve(null);
        }
        return apiClient.getItem(apiClient.getCurrentUserId(), itemId)
            .catch(function (error) {
                console.warn('[КиноПоиск] Карточка для рецензий не загружена.', error);
                return null;
            });
    }

    function fetchReviewPage(kinopoiskId, page) {
        var key = String(kinopoiskId) + ':' + String(page);
        if (!reviewCache.has(key)) {
            reviewCache.set(
                key,
                fetchJson(
                    '/KinopoiskPresentation/'
                    + encodeURIComponent(String(kinopoiskId))
                    + '/reviews?page='
                    + encodeURIComponent(String(page))
                    + '&order=USER_POSITIVE_RATING_DESC'
                ).catch(function (error) {
                    reviewCache.delete(key);
                    throw error;
                })
            );
        }
        return reviewCache.get(key);
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskReviewsIntegrationStyles')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'kinopoiskReviewsIntegrationStyles';
        style.textContent = [
            '.kp-reviews-fallback{margin:2em 0 1em;display:flex!important;flex-direction:column}',
            '.kp-reviews-fallback>summary{cursor:pointer;display:flex;align-items:center;justify-content:space-between}',
            '.kp-reviews-fallback[open]>summary .expand-icon{transform:rotate(180deg)}',
            '.kp-review-source-bar{display:flex;gap:.45em;flex-wrap:wrap;padding:.65em .5em 0}',
            '.kp-review-source-button{border:1px solid rgba(255,255,255,.16);border-radius:999px;background:rgba(255,255,255,.06);color:inherit;padding:.38em .7em;cursor:pointer;font:inherit}',
            '.kp-review-source-button.is-active{background:rgba(255,255,255,.18);border-color:rgba(255,255,255,.34)}',
            '.kp-review-card{border-left-color:#ff9b38!important;background:rgba(45,25,5,.46)!important}',
            '.kp-review-card.kp-review-positive{border-left-color:#52c76b!important}',
            '.kp-review-card.kp-review-negative{border-left-color:#e45d5d!important}',
            '.kp-review-card.kp-review-neutral{border-left-color:#d0ad55!important}',
            '.kp-review-title{font-size:1.05em;font-weight:650;margin:0 0 .7em}',
            '.kp-review-type{font-size:.82em;padding:.24em .48em;border-radius:.35em;background:rgba(255,255,255,.09);white-space:nowrap}',
            '.kp-review-votes{display:flex;gap:.85em;margin-top:auto;padding-top:.9em;font-size:.86em;opacity:.78}',
            '.kp-review-toggle{border:0;background:transparent;color:#59b6e9;cursor:pointer;padding:.35em 0;font:inherit;text-align:left}',
            '.kp-review-pager{display:flex;align-items:center;gap:.6em;padding:.2em .5em 1em}',
            '.kp-review-pager-button{border:1px solid rgba(255,255,255,.16);border-radius:.55em;background:rgba(255,255,255,.08);color:inherit;padding:.5em .8em;cursor:pointer;font:inherit}',
            '.kp-review-pager-button:disabled{opacity:.55;cursor:wait}',
            '.kp-review-status{padding:.75em .5em;opacity:.72}',
            '.kp-review-card[hidden],.tmdb-review-card[hidden]{display:none!important}',
            '@media(min-width:768px){.kp-review-card{flex-basis:400px}}'
        ].join('');
        document.head.appendChild(style);
    }

    function getVisiblePage() {
        return document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage:not(.hide)')
            || document;
    }

    function findStandardReviewSection(page) {
        var sections = Array.prototype.slice.call(
            page.querySelectorAll('.tmdb-reviews-section')
        );
        var standard = sections.find(function (section) {
            return !section.classList.contains('kp-reviews-fallback');
        });
        if (standard) {
            sections.forEach(function (section) {
                if (section !== standard && section.classList.contains('kp-reviews-fallback')) {
                    section.remove();
                }
            });
            return standard;
        }
        return sections[0] || null;
    }

    function createFallbackReviewSection(page, itemId) {
        var section = createElement('details', 'detailSection tmdb-reviews-section kp-reviews-fallback');
        section.dataset.kpItemId = itemId;

        var summary = createElement('summary', 'sectionTitle');
        summary.append(
            document.createTextNode('Рецензии (0) '),
            createElement('i', 'material-icons expand-icon', 'expand_more')
        );
        section.appendChild(summary);
        section.appendChild(createElement('div', 'tmdb-review-swipe-container'));

        var anchor = page.querySelector('.streaming-lookup-container')
            || page.querySelector('.itemExternalLinks')
            || page.querySelector('.tagline')
            || page.querySelector('.detailPagePrimaryContent');
        if (!anchor || !anchor.parentNode) {
            return null;
        }
        anchor.parentNode.insertBefore(section, anchor.nextSibling);
        return section;
    }

    function getOrCreateReviewSection(page, itemId) {
        var section = findStandardReviewSection(page);
        if (section) {
            return section;
        }
        return createFallbackReviewSection(page, itemId);
    }

    function getSwipeContainer(section) {
        var container = section.querySelector('.tmdb-review-swipe-container');
        if (!container) {
            container = createElement('div', 'tmdb-review-swipe-container');
            section.appendChild(container);
        }
        return container;
    }

    function classifyReviewCards(section) {
        Array.prototype.forEach.call(
            section.querySelectorAll('.tmdb-review-card'),
            function (card) {
                if (card.classList.contains('kp-review-card')) {
                    card.dataset.reviewSource = 'kinopoisk';
                } else if (card.classList.contains('je-user-review-card')) {
                    card.dataset.reviewSource = 'users';
                } else {
                    card.dataset.reviewSource = 'tmdb';
                }
            }
        );
    }

    function getAvailableSources(section) {
        var available = new Set();
        Array.prototype.forEach.call(
            section.querySelectorAll('.tmdb-review-card'),
            function (card) {
                if (card.dataset.reviewSource) {
                    available.add(card.dataset.reviewSource);
                }
            }
        );
        return available;
    }

    function applyReviewFilter(section, source) {
        section.dataset.kpReviewFilter = source;
        Array.prototype.forEach.call(
            section.querySelectorAll('.tmdb-review-card'),
            function (card) {
                card.hidden = source !== 'all' && card.dataset.reviewSource !== source;
            }
        );
        Array.prototype.forEach.call(
            section.querySelectorAll('.kp-review-source-button'),
            function (button) {
                var active = button.dataset.source === source;
                button.classList.toggle('is-active', active);
                button.setAttribute('aria-pressed', active ? 'true' : 'false');
            }
        );
    }

    function ensureSourceBar(section) {
        classifyReviewCards(section);
        var available = getAvailableSources(section);
        var bar = section.querySelector('.kp-review-source-bar');
        if (!bar) {
            bar = createElement('div', 'kp-review-source-bar');
            var swipe = getSwipeContainer(section);
            section.insertBefore(bar, swipe);
        }

        var sources = [
            { key: 'all', label: 'Все' },
            { key: 'kinopoisk', label: 'КиноПоиск' },
            { key: 'tmdb', label: 'TMDB' },
            { key: 'users', label: 'Пользователи' }
        ];
        bar.textContent = '';
        sources.forEach(function (source) {
            if (source.key !== 'all' && !available.has(source.key)) {
                return;
            }
            var button = createElement('button', 'kp-review-source-button', source.label);
            button.type = 'button';
            button.dataset.source = source.key;
            button.addEventListener('click', function () {
                applyReviewFilter(section, source.key);
            });
            bar.appendChild(button);
        });

        var requested = section.dataset.kpReviewFilter || 'all';
        if (requested !== 'all' && !available.has(requested)) {
            requested = 'all';
        }
        applyReviewFilter(section, requested);
    }

    function updateReviewSummary(section, kinopoiskTotal) {
        var summary = section.querySelector('summary');
        if (!summary) {
            return;
        }
        var otherCount = section.querySelectorAll(
            '.tmdb-review-card:not(.kp-review-card)'
        ).length;
        summary.textContent = '';
        summary.append(
            document.createTextNode(
                'Рецензии (' + String(otherCount + Math.max(0, Number(kinopoiskTotal || 0))) + ') '
            ),
            createElement('i', 'material-icons expand-icon', 'expand_more')
        );
    }

    function reviewTypeClass(type) {
        if (type === 'POSITIVE') {
            return 'kp-review-positive';
        }
        if (type === 'NEGATIVE') {
            return 'kp-review-negative';
        }
        if (type === 'NEUTRAL') {
            return 'kp-review-neutral';
        }
        return '';
    }

    function createReviewCard(review) {
        var type = String(pick(review, 'type', 'Type') || 'UNKNOWN').toUpperCase();
        var description = String(pick(review, 'description', 'Description') || '');
        var previewLength = 350;
        var expanded = false;
        var card = createElement(
            'div',
            'tmdb-review-card kp-review-card ' + reviewTypeClass(type)
        );
        card.dataset.reviewSource = 'kinopoisk';

        var header = createElement('div', 'tmdb-review-header');
        var authorInfo = createElement('div', 'tmdb-review-author-info');
        authorInfo.append(
            createElement(
                'strong',
                'tmdb-review-author',
                String(pick(review, 'author', 'Author') || 'Автор КиноПоиска')
            ),
            createElement(
                'span',
                'tmdb-review-date',
                formatDate(pick(review, 'date', 'Date'))
            )
        );
        header.append(
            authorInfo,
            createElement(
                'span',
                'kp-review-type',
                reviewTypeLabels[type] || reviewTypeLabels.UNKNOWN
            )
        );
        card.appendChild(header);

        var title = String(pick(review, 'title', 'Title') || '').trim();
        if (title) {
            card.appendChild(createElement('div', 'kp-review-title', title));
        }

        var wrapper = createElement('div', 'tmdb-review-content-wrapper');
        var text = createElement('p', 'tmdb-review-text');
        var toggle = null;

        function renderText() {
            var visible = expanded || description.length <= previewLength
                ? description
                : description.substring(0, previewLength).trimEnd() + '…';
            text.textContent = visible;
            if (toggle) {
                toggle.textContent = expanded ? 'Свернуть' : 'Читать полностью';
                toggle.setAttribute('aria-expanded', expanded ? 'true' : 'false');
            }
        }

        wrapper.appendChild(text);
        if (description.length > previewLength) {
            toggle = createElement('button', 'kp-review-toggle', 'Читать полностью');
            toggle.type = 'button';
            toggle.setAttribute('aria-expanded', 'false');
            toggle.addEventListener('click', function () {
                expanded = !expanded;
                renderText();
            });
            wrapper.appendChild(toggle);
        }
        card.appendChild(wrapper);

        var votes = createElement('div', 'kp-review-votes');
        votes.append(
            createElement(
                'span',
                '',
                '👍 ' + String(Math.max(0, Number(pick(review, 'positiveRating', 'PositiveRating') || 0)))
            ),
            createElement(
                'span',
                '',
                '👎 ' + String(Math.max(0, Number(pick(review, 'negativeRating', 'NegativeRating') || 0)))
            )
        );
        card.appendChild(votes);
        renderText();
        return card;
    }

    function formatDate(value) {
        if (!value) {
            return '';
        }
        var date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return '';
        }
        return date.toLocaleDateString('ru-RU', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric'
        });
    }

    function getState(itemId, kinopoiskId) {
        var state = reviewStates.get(itemId);
        if (!state || state.kinopoiskId !== String(kinopoiskId)) {
            state = {
                kinopoiskId: String(kinopoiskId),
                items: [],
                loadedPages: new Set(),
                currentPage: 0,
                totalPages: 0,
                total: 0,
                visibleCount: 5,
                loading: false,
                loaded: false,
                error: false
            };
            reviewStates.set(itemId, state);
        }
        return state;
    }

    function removeKinopoiskReviewElements(section) {
        Array.prototype.forEach.call(
            section.querySelectorAll('.kp-review-card,.kp-review-pager,.kp-review-status'),
            function (element) { element.remove(); }
        );
    }

    function renderState(section, state) {
        removeKinopoiskReviewElements(section);
        var swipe = getSwipeContainer(section);
        var visible = state.items.slice(0, state.visibleCount);
        visible.forEach(function (review) {
            swipe.appendChild(createReviewCard(review));
        });

        if (state.loaded && !state.items.length) {
            var empty = createElement('div', 'kp-review-status', 'Рецензии КиноПоиска отсутствуют.');
            section.insertBefore(empty, swipe.nextSibling);
        }
        if (state.error) {
            var error = createElement('div', 'kp-review-status', 'Рецензии КиноПоиска временно недоступны.');
            section.insertBefore(error, swipe.nextSibling);
        }

        var canReveal = state.visibleCount < state.items.length;
        var canLoad = state.loaded && state.currentPage < state.totalPages;
        if (canReveal || canLoad) {
            var pager = createElement('div', 'kp-review-pager');
            var button = createElement(
                'button',
                'kp-review-pager-button',
                canReveal
                    ? 'Показать ещё ' + String(Math.min(5, state.items.length - state.visibleCount))
                    : 'Загрузить следующие рецензии'
            );
            button.type = 'button';
            button.disabled = state.loading;
            button.addEventListener('click', function () {
                if (state.visibleCount < state.items.length) {
                    state.visibleCount = Math.min(state.items.length, state.visibleCount + 5);
                    renderState(section, state);
                    return;
                }
                loadPage(section, state, state.currentPage + 1);
            });
            pager.appendChild(button);
            section.insertBefore(pager, swipe.nextSibling);
        }

        classifyReviewCards(section);
        ensureSourceBar(section);
        updateReviewSummary(section, state.total);
    }

    function loadPage(section, state, page) {
        if (state.loading || state.loadedPages.has(page)) {
            return Promise.resolve();
        }
        state.loading = true;
        state.error = false;
        renderState(section, state);

        return fetchReviewPage(state.kinopoiskId, page)
            .then(function (data) {
                var items = asArray(pick(data, 'items', 'Items'));
                state.loadedPages.add(page);
                state.currentPage = Math.max(state.currentPage, Number(pick(data, 'page', 'Page') || page));
                state.totalPages = Math.max(0, Number(pick(data, 'totalPages', 'TotalPages') || 0));
                state.total = Math.max(items.length, Number(pick(data, 'total', 'Total') || 0));
                items.forEach(function (review) {
                    var key = [
                        pick(review, 'kinopoiskId', 'KinopoiskId'),
                        pick(review, 'author', 'Author'),
                        pick(review, 'date', 'Date'),
                        pick(review, 'title', 'Title')
                    ].join('|');
                    var exists = state.items.some(function (existing) {
                        return existing.__kpKey === key;
                    });
                    if (!exists) {
                        review.__kpKey = key;
                        state.items.push(review);
                    }
                });
                state.loaded = true;
                state.loading = false;
                renderState(section, state);
            })
            .catch(function (error) {
                console.warn('[КиноПоиск] Рецензии не загружены.', error);
                state.loading = false;
                state.error = true;
                renderState(section, state);
            });
    }

    function shouldSuppressForSpoilerMode(item) {
        var enhanced = window.JellyfinEnhanced;
        if (!enhanced || !enhanced.pluginConfig || !enhanced.spoilerBlur) {
            return Promise.resolve(false);
        }
        if (!enhanced.pluginConfig.SpoilerBlurEnabled
            || enhanced.pluginConfig.SpoilerStripReviews === false) {
            return Promise.resolve(false);
        }

        var spoilerBlur = enhanced.spoilerBlur;
        var ready = typeof spoilerBlur.whenLoaded === 'function'
            ? spoilerBlur.whenLoaded()
            : Promise.resolve();
        return Promise.resolve(ready).then(function () {
            if (typeof spoilerBlur.isLoadOk === 'function' && !spoilerBlur.isLoadOk()) {
                return true;
            }
            if (typeof spoilerBlur.getUserPrefs === 'function') {
                var preferences = spoilerBlur.getUserPrefs() || {};
                if (preferences.HideReviews === false) {
                    return false;
                }
            }
            var itemType = String(pick(item, 'Type', 'type') || '');
            if (itemType === 'Movie') {
                return !!(spoilerBlur.isMovieEnabledFor
                    && spoilerBlur.isMovieEnabledFor(pick(item, 'Id', 'id') || ''));
            }
            if (itemType === 'Series') {
                return !!(spoilerBlur.isEnabledFor
                    && spoilerBlur.isEnabledFor(pick(item, 'Id', 'id') || ''));
            }
            return false;
        }).catch(function (error) {
            console.warn('[КиноПоиск] Проверка Spoiler Guard завершилась ошибкой.', error);
            return true;
        });
    }

    function bindSection(section, itemId, kinopoiskId) {
        var state = getState(itemId, kinopoiskId);
        var bindingKey = itemId + ':' + String(kinopoiskId);
        if (section.dataset.kpReviewsBinding !== bindingKey) {
            section.dataset.kpReviewsBinding = bindingKey;
            section.addEventListener('toggle', function () {
                if (section.open && !state.loaded && !state.loading) {
                    loadPage(section, state, 1);
                }
            });
        }

        if (state.loaded || state.loading || state.error) {
            renderState(section, state);
        } else {
            ensureSourceBar(section);
        }
        if (section.open && !state.loaded && !state.loading) {
            loadPage(section, state, 1);
        }
    }

    function removeSuppressedElements(page) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.kp-reviews-fallback'),
            function (section) { section.remove(); }
        );
        var standard = page.querySelector('.tmdb-reviews-section');
        if (standard) {
            Array.prototype.forEach.call(
                standard.querySelectorAll('.kp-review-card,.kp-review-source-bar,.kp-review-pager,.kp-review-status'),
                function (element) { element.remove(); }
            );
        }
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

            shouldSuppressForSpoilerMode(item).then(function (suppressed) {
                if (getCurrentItemId() !== itemId) {
                    return;
                }
                var page = getVisiblePage();
                if (suppressed) {
                    removeSuppressedElements(page);
                    return;
                }
                var section = getOrCreateReviewSection(page, itemId);
                if (section) {
                    bindSection(section, itemId, kinopoiskId);
                }
            });
        });
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentItem, 300);
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
    console.info('[КиноПоиск] Интеграция рецензий зарегистрирована.');
}());
