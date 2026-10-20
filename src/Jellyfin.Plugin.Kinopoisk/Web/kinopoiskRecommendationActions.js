(function () {
    'use strict';
    if (window.KinopoiskRecommendationActions) { return; }
    var states = new Map();
    var pending = new Map();
    var userId = '';
    var generation = 0;
    var renderTimer = null;
    var refreshTimer = null;
    var refreshing = false;

    function user() {
        return window.ApiClient && window.ApiClient.getCurrentUserId ? String(window.ApiClient.getCurrentUserId() || '') : '';
    }
    function context() {
        var route = location.hash.replace(/^#\/?/, '').split('?')[0];
        var favorites = /^(home|home\.html)$/.test(route) && document.querySelector('#indexPage #favoritesTab.is-active');
        if (favorites && !favorites.closest('.hide,[hidden]')) {
            var visible = true;
            for (var parent = favorites; parent && parent !== document; parent = parent.parentElement) {
                var style = getComputedStyle(parent);
                if (style.display === 'none' || style.visibility === 'hidden') { visible = false; break; }
            }
            if (visible) { return { id: 'kinopoisk:favorites', page: favorites, kind: 'favorites' }; }
        }
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        var id = lifecycle ? lifecycle.getCurrentItemId() : new URLSearchParams((location.hash.split('?')[1] || location.search)).get('id');
        return { id: id, page: lifecycle ? lifecycle.getActivePage() : document, kind: 'details' };
    }
    function owner() { return context().id; }
    function attribute(element, name, value) {
        if (value === null) { if (element.hasAttribute(name)) { element.removeAttribute(name); } }
        else if (element.getAttribute(name) !== String(value)) { element.setAttribute(name, String(value)); }
    }
    function flag(element, name, enabled) {
        if (element.classList.contains(name) !== enabled) { element.classList.toggle(name, enabled); }
    }
    function key(item) {
        var kp = Number(item.kinopoiskId || item.KinopoiskId || 0);
        var tmdb = Number(item.tmdbId || item.TmdbId || 0);
        var type = (item.mediaType || item.MediaType) === 'tv' ? 'tv' : 'movie';
        return type + ':' + (kp > 0 ? 'kp:' + kp : 'tmdb:' + tmdb);
    }
    function request(path, method, body) {
        var client = window.ApiClient;
        if (!client || !user()) { return Promise.reject(new Error('Войдите в Jellyfin.')); }
        var controller = new AbortController();
        var timer = setTimeout(function () { controller.abort(); }, 20000);
        var headers = { Authorization: 'MediaBrowser Token="' + client.accessToken() + '"', 'X-Emby-Token': client.accessToken(), Accept: 'application/json' };
        if (body !== undefined) { headers['Content-Type'] = 'application/json'; }
        return fetch(client.getUrl(path), { method: method || 'GET', credentials: 'same-origin', headers: headers,
            signal: controller.signal, body: body === undefined ? undefined : JSON.stringify(body) }).then(function (response) {
            return response.json().catch(function () { return {}; }).then(function (data) {
                if (!response.ok) { throw new Error(typeof data.message === 'string' ? data.message : 'Не удалось сохранить действие в Jellyfin.'); }
                return data;
            });
        }).finally(function () { clearTimeout(timer); });
    }
    function node(tag, className, text) {
        var result = document.createElement(tag);
        result.className = className;
        if (text) { result.textContent = text; }
        return result;
    }
    function iconButton(className, icon) {
        var button = node('button', 'cardOverlayButton cardOverlayButton-hover paper-icon-button-light ' + className);
        button.type = 'button';
        button.setAttribute('is', 'paper-icon-button-light');
        var glyph = node('span', 'material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover ' + icon);
        glyph.setAttribute('aria-hidden', 'true');
        button.appendChild(glyph);
        return button;
    }
    function notice(card, message, error) {
        var status = card.querySelector('.kp-card-action-status');
        status.textContent = message;
        status.classList.toggle('is-error', error === true);
    }
    function current(card, expectedUser, expectedOwner, version) {
        var surface = context();
        return card.isConnected && surface.page && surface.page.contains(card) && user() === expectedUser && surface.id === expectedOwner && generation === version;
    }
    function reset(card) {
        card.__kpPersonalState = null;
        card.classList.remove('kp-library-ready');
        ['id', 'serverid', 'type', 'mediatype', 'isfolder', 'positionticks'].forEach(function (name) { card.removeAttribute('data-' + name); });
        var play = card.querySelector('.kp-similar-card__play');
        if (play) { play.hidden = true; play.disabled = true; }
        var heart = card.querySelector('.kp-similar-card__heart');
        if (heart) { heart.disabled = true; heart.setAttribute('aria-pressed', 'false'); heart.querySelector('span').classList.remove('favorite'); heart.querySelector('span').classList.add('favorite_border'); }
    }
    function apply(card, state) {
        var item = card.__kinopoiskRecommendationItem;
        var type = (item.mediaType || item.MediaType) === 'tv' ? 'Series' : 'Movie';
        var client = window.ApiClient;
        var server = client && typeof client.serverInfo === 'function' && client.serverInfo();
        var serverId = server && server.Id || '';
        card.__kpPersonalState = state;
        card.__kpPersonalUser = user();
        card.__kpPersonalOwner = owner();
        var play = card.querySelector('.kp-similar-card__play');
        var info = card.querySelector('.kp-similar-card__info');
        var actions = card.querySelector('.kp-similar-card__native-actions');
        var canPlay = !!state.itemId && state.canPlay === true && !!serverId;
        attribute(play, 'hidden', canPlay ? null : '');
        attribute(play, 'disabled', canPlay ? null : '');
        flag(card, 'kp-library-ready', canPlay);
        var playback = { id: state.itemId, serverid: serverId, type: type, mediatype: 'Video',
            isfolder: String(type === 'Series'), positionticks: String(Math.max(0, Number(state.playbackPositionTicks || 0))) };
        Object.keys(playback).forEach(function (name) { attribute(card, 'data-' + name, state.itemId ? playback[name] : null); });
        // Re-inserting an unchanged node restarts native hover transitions and
        // triggers another lifecycle render through its MutationObserver.
        if (canPlay) {
            flag(info, 'cardOverlayFab-primary', false);
            if (info.parentNode !== actions) { actions.insertBefore(info, actions.firstChild); }
        } else {
            flag(info, 'cardOverlayFab-primary', true);
            var overlay = card.querySelector('.cardOverlayContainer');
            if (info.parentNode !== overlay) { overlay.insertBefore(info, play); }
        }
        var playLabel = Number(state.playbackPositionTicks) > 0 ? 'Продолжить просмотр' : 'Воспроизвести';
        attribute(play, 'title', playLabel);
        attribute(play, 'aria-label', playLabel);
        var heart = card.querySelector('.kp-similar-card__heart');
        var selected = !!(state.favorite || state.wished);
        var label = state.wished ? 'Удалить из желаемого' : state.itemId
            ? selected ? 'Удалить из избранного Jellyfin' : 'Добавить в избранное Jellyfin'
            : 'Добавить в желаемое';
        attribute(heart, 'title', label);
        attribute(heart, 'aria-label', label);
        attribute(heart, 'aria-pressed', selected);
        flag(heart, 'ratingbutton-withrating', selected);
        var glyph = heart.querySelector('span');
        flag(glyph, 'favorite', selected);
        flag(glyph, 'favorite_border', !selected);
        flag(glyph, 'ratingbutton-icon-withrating', selected);
        attribute(heart, 'disabled', pending.has(user() + ':' + key(item)) ? '' : null);
    }
    function metadata(card) {
        var item = card.__kinopoiskRecommendationItem;
        return { kinopoiskId: Number(item.kinopoiskId || item.KinopoiskId || 0), tmdbId: Number(item.tmdbId || item.TmdbId || card.dataset.tmdbId || 0),
            mediaType: (item.mediaType || item.MediaType) === 'tv' ? 'tv' : 'movie', name: String(item.name || item.Name || '').slice(0, 300),
            originalName: String(item.originalName || item.OriginalName || '').slice(0, 300), overview: String(item.overview || item.Overview || '').slice(0, 4000),
            posterUrl: String(item.posterUrl || item.PosterUrl || item.posterUrlPreview || item.PosterUrlPreview || '').slice(0, 2048),
            year: Number(item.year || item.Year) || null, ratingKinopoisk: Number(item.ratingKinopoisk || item.RatingKinopoisk) || null };
    }
    function toggle(card) {
        var state = card.__kpPersonalState;
        var expectedUser = user();
        var expectedOwner = owner();
        var version = generation;
        var itemKey = key(card.__kinopoiskRecommendationItem);
        var lock = expectedUser + ':' + itemKey;
        if (!state || card.__kpPersonalUser !== expectedUser || card.__kpPersonalOwner !== expectedOwner || !current(card, expectedUser, expectedOwner, version) || pending.has(lock)) { return; }
        var saved = !(state.favorite || state.wished);
        notice(card, '');
        pending.set(lock, true);
        card.querySelector('.kp-similar-card__heart').disabled = true;
        var mutated = false;
        Promise.resolve().then(function () {
            if (!current(card, expectedUser, expectedOwner, version)) { throw new Error('Учётная запись или страница изменились.'); }
            if (state.itemId && (!state.wished || state.favorite)) {
                mutated = true;
                return request('/UserFavoriteItems/' + encodeURIComponent(state.itemId), saved ? 'POST' : 'DELETE');
            }
        }).then(function () {
            if (user() !== expectedUser || generation !== version) { throw new Error('Учётная запись изменилась.'); }
            if (!state.itemId && saved) {
                mutated = true;
                return request('/KinopoiskRecommendations/wishlist', 'PUT', metadata(card));
            }
            if (!saved && state.wished) {
                mutated = true;
                return request('/KinopoiskRecommendations/wishlist/' + itemKey.split(':').map(encodeURIComponent).join('/'), 'DELETE');
            }
        }).then(function () {
            if (!mutated || user() !== expectedUser || generation !== version) { return; }
            states.delete(itemKey);
            if (current(card, expectedUser, expectedOwner, version)) {
                state.favorite = !!state.itemId && saved;
                state.wished = !state.itemId && saved;
                apply(card, state);
                notice(card, saved ? state.itemId ? 'Добавлено в избранное Jellyfin.' : 'Сохранено в желаемом.' : 'Сохранённая отметка удалена.');
            }
            document.dispatchEvent(new CustomEvent('kinopoisk:wishlist-changed', { detail: { userId: expectedUser } }));
        }).catch(function (error) {
            states.delete(itemKey);
            if (current(card, expectedUser, expectedOwner, version)) { notice(card, String(error.message || 'Не удалось сохранить действие.'), true); }
        }).finally(function () {
            pending.delete(lock);
            if (current(card, expectedUser, expectedOwner, version)) { card.querySelector('.kp-similar-card__heart').disabled = false; schedule(); }
        });
    }
    function decorate(card) {
        if (card.querySelector('.kp-similar-card__heart')) { return; }
        var overlay = card.querySelector('.cardOverlayContainer');
        var info = overlay.querySelector('.cardOverlayFab-primary');
        info.classList.add('kp-similar-card__info');
        var play = iconButton('kp-similar-card__play cardOverlayFab-primary itemAction', 'play_arrow');
        play.dataset.action = 'resume';
        play.hidden = true;
        play.disabled = true;
        play.addEventListener('click', function (event) {
            if (!card.__kpPersonalState || user() !== card.__kpPersonalUser || owner() !== card.__kpPersonalOwner) { event.preventDefault(); event.stopPropagation(); }
        });
        var actions = node('div', 'cardOverlayButton-br flex kp-similar-card__native-actions');
        var heart = iconButton('kp-similar-card__heart', 'favorite_border');
        heart.disabled = true;
        heart.setAttribute('aria-label', 'Добавить в желаемое');
        heart.setAttribute('aria-pressed', 'false');
        heart.addEventListener('click', function (event) { event.preventDefault(); event.stopPropagation(); toggle(card); });
        actions.appendChild(heart);
        overlay.append(play, actions);
        var status = node('span', 'kp-card-action-status');
        status.setAttribute('role', 'status');
        status.setAttribute('aria-live', 'polite');
        card.appendChild(status);
    }
    function schedule() {
        if (!renderTimer) { renderTimer = setTimeout(function () { renderTimer = null; refresh(); }, 90); }
    }
    function refresh() {
        var selectedUser = user();
        if (selectedUser !== userId) {
            userId = selectedUser;
            generation += 1;
            states.clear();
            document.querySelectorAll('.kp-similar-card').forEach(reset);
            document.dispatchEvent(new CustomEvent('kinopoisk:personal-user-changed'));
        }
        if (document.hidden || refreshing) { return; }
        var page = context().page;
        if (!page || !selectedUser || !owner()) { return; }
        var expectedOwner = owner();
        var version = generation;
        var cards = Array.prototype.slice.call(page.querySelectorAll('.kp-similar-card'));
        cards.forEach(decorate);
        var keys = [];
        cards.forEach(function (card) {
            var itemKey = key(card.__kinopoiskRecommendationItem);
            var cached = states.get(itemKey);
            if (cached && cached.expires > Date.now()) { apply(card, cached.state); }
            else if (keys.indexOf(itemKey) < 0 && keys.length < 50) { keys.push(itemKey); }
        });
        if (!keys.length) { return; }
        refreshing = true;
        request('/KinopoiskRecommendations/state?keys=' + encodeURIComponent(keys.join(','))).then(function (data) {
            if (user() !== selectedUser || generation !== version || owner() !== expectedOwner) { return; }
            var entries = data.items || data.Items || [];
            entries.forEach(function (state) {
                states.set(state.key, { state: state, expires: Date.now() + 15000 });
                cards.forEach(function (card) {
                    if (current(card, selectedUser, expectedOwner, version) && key(card.__kinopoiskRecommendationItem) === state.key) { apply(card, state); }
                });
            });
        }).catch(function () {
            cards.forEach(function (card) {
                if (current(card, selectedUser, expectedOwner, version)) {
                    notice(card, 'Действия Jellyfin временно недоступны.', true);
                    var heart = card.querySelector('.kp-similar-card__heart');
                    heart.disabled = true;
                }
            });
        }).finally(function () { refreshing = false; if (user() !== selectedUser || owner() !== expectedOwner || cards.some(function (card) { return !card.isConnected; })) { schedule(); } });
    }
    function tick() {
        clearTimeout(refreshTimer);
        refreshTimer = null;
        if (!document.hidden && owner() && user()) {
            schedule();
            refreshTimer = setTimeout(tick, 30000);
        }
    }
    window.KinopoiskRecommendationActions = { decorate: decorate, key: key, context: context, list: function () { return request('/KinopoiskRecommendations/wishlist'); }, refresh: schedule };
    document.addEventListener('kinopoisk:recommendations-rendered', function () { schedule(); if (!refreshTimer) { tick(); } });
    document.addEventListener('kinopoisk:wishlist-changed', function () { states.clear(); schedule(); });
    document.addEventListener('kinopoisk:recommendation-surface-changed', schedule);
    document.addEventListener('focusin', function (event) { if (event.target.closest && event.target.closest('.kp-similar-card')) { schedule(); } });
    document.addEventListener('visibilitychange', tick);
    window.addEventListener('pagehide', function () { clearTimeout(refreshTimer); refreshTimer = null; });
    var lifecycle = window.KinopoiskDetailPageLifecycle;
    if (lifecycle) { lifecycle.subscribe(schedule); }
    schedule();
}());
