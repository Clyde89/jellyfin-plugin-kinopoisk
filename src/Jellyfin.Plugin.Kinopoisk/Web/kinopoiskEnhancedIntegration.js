(function () {
    'use strict';
    if (window.KinopoiskEnhancedIntegration) { return; }

    var scope = '';
    var generation = 0;
    var snapshot = null;
    var probe = null;
    var expiresAt = 0;
    var blockedUntil = 0;
    var failures = 0;
    var queue = Promise.resolve();
    var nextStart = 0;
    var pending = new Map();
    var missingPaths = new Map();
    var notFoundTimes = [];
    var lastState = '';

    function session() {
        var client = window.ApiClient;
        return client && typeof client.getUrl === 'function' && typeof client.getCurrentUserId === 'function'
            && typeof client.accessToken === 'function'
            ? [client.getUrl('/'), client.getCurrentUserId(), client.accessToken()].join('|') : '';
    }
    function context() {
        var current = session();
        if (current !== scope) {
            scope = current;
            generation += 1;
            snapshot = null;
            probe = null;
            expiresAt = 0;
            blockedUntil = 0;
            failures = 0;
            pending.clear();
            missingPaths.clear();
            notFoundTimes = [];
            lastState = '';
        }
        return generation;
    }
    function changed(state) {
        if (state === lastState) { return; }
        lastState = state;
        console.info('[КиноПоиск] Интеграция Enhanced: ' + state + '.');
        document.dispatchEvent(new CustomEvent('kinopoisk:enhanced-state', { detail: { state: state } }));
    }
    function unavailable(reason) {
        var error = new Error('Интеграция Enhanced сейчас недоступна.');
        error.reason = reason || 'enhanced_unavailable';
        return error;
    }
    function raw(path) {
        var client = window.ApiClient;
        var controller = new AbortController();
        var timer = setTimeout(function () { controller.abort(); }, 15000);
        return fetch(client.getUrl(path), { credentials: 'same-origin', cache: 'no-store',
            headers: { 'Authorization': 'MediaBrowser Token="' + client.accessToken() + '"',
                'X-Emby-Token': client.accessToken(), 'Accept': 'application/json' },
            signal: controller.signal
        }).then(function (response) {
            if (!response.ok) {
                var error = unavailable();
                error.status = response.status;
                throw error;
            }
            return response.json();
        }).finally(function () { clearTimeout(timer); });
    }
    function suspend(expected) {
        if (context() !== expected) { return; }
        failures = Math.min(5, failures + 1);
        blockedUntil = Date.now() + Math.min(300000, 30000 * Math.pow(2, failures - 1));
        expiresAt = blockedUntil;
        snapshot = { available: false, tmdb: false, state: 'unreachable' };
        changed('временно недоступна; включён автономный режим');
    }
    function status() {
        var expected = context();
        if (!scope || !window.ApiClient.getCurrentUserId() || !window.ApiClient.accessToken()) {
            return Promise.resolve({ available: false, tmdb: false, state: 'no_session' });
        }
        if (snapshot && Date.now() < expiresAt) { return Promise.resolve(snapshot); }
        if (probe) { return probe; }
        var work = raw('/KinopoiskRequests/integrations').then(function (result) {
            if (context() !== expected) { throw unavailable('session_changed'); }
            if (!result || !result.enhanced || result.enhanced.available !== true) {
                var state = result && result.enhanced && result.enhanced.state || 'unknown';
                snapshot = { available: false, tmdb: false, state: state };
                expiresAt = Date.now() + 60000;
                changed('недоступна; включён автономный режим');
                return snapshot;
            }
            // Реестр подтвердил загруженный экземпляр. Проверялся один постоянный URL.
            return raw('/JellyfinEnhanced/public-config').then(function (config) {
                if (context() !== expected) { throw unavailable('session_changed'); }
                if (!config || typeof config !== 'object' || Array.isArray(config)
                    || typeof config.TmdbEnabled !== 'boolean') { throw unavailable('unsupported_config'); }
                snapshot = { available: true, tmdb: config.TmdbEnabled, state: 'available', config: config };
                expiresAt = Date.now() + 60000;
                blockedUntil = 0;
                failures = 0;
                changed('доступна');
                return snapshot;
            });
        }).catch(function (error) {
            if (context() !== expected) { throw unavailable('session_changed'); }
            suspend(expected);
            return snapshot;
        });
        probe = work;
        var finished = function () { if (probe === work) { probe = null; } };
        work.then(finished, finished);
        return work;
    }

    function run(path, action, deduplicate) {
        var expected = context();
        var key = expected + ':' + path;
        if (!/^\/JellyfinEnhanced\//.test(path)) { return Promise.reject(unavailable('invalid_path')); }
        if (deduplicate !== false && pending.has(key)) { return pending.get(key); }
        var work = queue.catch(function () {}).then(function () {
            if (context() !== expected) { throw unavailable('session_changed'); }
            return status();
        }).then(function (state) {
            if (context() !== expected) { throw unavailable('session_changed'); }
            if (!state.available || Date.now() < blockedUntil) { throw unavailable(state.state); }
            if (path.indexOf('/JellyfinEnhanced/tmdb/') === 0 && !state.tmdb) { throw unavailable('tmdb_disabled'); }
            if (path === '/JellyfinEnhanced/public-config') { return { cached: true, value: state.config }; }
            var now = Date.now();
            notFoundTimes = notFoundTimes.filter(function (time) { return now - time < 60000; });
            if (notFoundTimes.length >= 3 || (missingPaths.get(path) || 0) > now) {
                throw unavailable('endpoint_backoff');
            }
            return new Promise(function (resolve) { setTimeout(resolve, Math.max(0, nextStart - now)); });
        }).then(function (cached) {
            if (cached && cached.cached) { return cached.value; }
            if (context() !== expected || Date.now() < blockedUntil) { throw unavailable(); }
            nextStart = Date.now() + 350;
            return Promise.resolve().then(action).then(function (value) {
                if (context() !== expected) { throw unavailable('session_changed'); }
                return value;
            }).catch(function (error) {
                if (context() !== expected) { throw unavailable('session_changed'); }
                if (error.status === 404) {
                    // Ошибка объекта не отключала расширение. Общий бюджет ограничил
                    // различные ошибочные URL даже при параллельной загрузке карточек.
                    notFoundTimes.push(Date.now());
                    missingPaths.set(path, Date.now() + 300000);
                    if (missingPaths.size > 256) { missingPaths.delete(missingPaths.keys().next().value); }
                } else if (!error.status || error.status === 401 || error.status === 403
                    || error.status === 429 || error.status >= 500) { suspend(expected); }
                throw error;
            });
        });
        queue = work.then(function () {}, function () {});
        if (deduplicate !== false) { pending.set(key, work); }
        work.then(function () { if (pending.get(key) === work) { pending.delete(key); } },
            function () { if (pending.get(key) === work) { pending.delete(key); } });
        return work;
    }

    window.KinopoiskEnhancedIntegration = Object.freeze({
        status: status,
        run: run,
        json: function (path) { return run(path, function () { return raw(path); }); }
    });
}());
