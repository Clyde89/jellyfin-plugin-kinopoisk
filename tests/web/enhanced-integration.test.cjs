const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');
const root = path.resolve(__dirname, '../../src/Jellyfin.Plugin.Kinopoisk/Web');
const source = name => fs.readFileSync(path.join(root, name), 'utf8');
const response = (data, status = 200) => new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json' } });

function runtime(options = {}) {
    const dom = new JSDOM('<!doctype html><body></body>', { runScripts: 'outside-only', url: 'https://jellyfin.test/jellyfin/web/' });
    const window = dom.window;
    let now = 1000000;
    let available = options.available === true;
    let user = 'ordinary-user';
    let token = 'fixture-token';
    let inFlight = 0;
    let maximum = 0;
    const calls = [];
    const logs = [];
    window.Date.now = () => now;
    const timeout = window.setTimeout.bind(window);
    window.setTimeout = (callback, delay, ...args) => delay < 1000
        ? timeout(() => { now += delay; callback(...args); }, 0) : timeout(callback, delay, ...args);
    window.console.info = message => { if (message.includes('Интеграция Enhanced:')) logs.push(message); };
    window.AbortController = AbortController;
    window.ApiClient = { getUrl: value => '/jellyfin' + value, getCurrentUserId: () => user, accessToken: () => token,
        getItem: () => Promise.resolve(null) };
    window.fetch = async (url, request) => {
        const target = String(url).replace(/^\/jellyfin/, '');
        calls.push({ path: target, at: now, request });
        if (target === '/KinopoiskRequests/integrations') {
            if (options.inventory) return options.inventory();
            return response({ enhanced: { available, state: available ? 'available' : 'missing' } });
        }
        if (target === '/JellyfinEnhanced/public-config') {
            return options.config ? options.config() : response({ TmdbEnabled: options.tmdb !== false });
        }
        inFlight += 1;
        maximum = Math.max(maximum, inFlight);
        try {
            return options.media ? await options.media(target, request) : response({ movie_results: [{ id: 501 }], tv_results: [{ id: 902 }] });
        } finally { inFlight -= 1; }
    };
    window.eval(source('kinopoiskEnhancedIntegration.js'));
    window.eval(source('kinopoiskElsewhereBridge.js'));
    return { dom, window, calls, logs, get maximum() { return maximum; },
        tick: amount => { now += amount; }, install: () => { available = true; },
        user: value => { user = value; }, logout: () => { user = ''; token = ''; },
        resolve: (id, type = 'Movie') => window.KinopoiskTmdbResolver.resolve(id, type),
        api: window.KinopoiskEnhancedIntegration };
}

test('отсутствующий Enhanced не получил ни одного обращения для 50 разных IMDb ID', async () => {
    const r = runtime();
    try {
        const ids = await Promise.all(Array.from({ length: 50 }, (_, i) => r.resolve('tt' + (1000000 + i))));
        assert.ok(ids.every(id => id === null));
        assert.equal(r.calls.filter(call => call.path.startsWith('/JellyfinEnhanced/')).length, 0);
        assert.equal(r.calls.filter(call => call.path === '/KinopoiskRequests/integrations').length, 1);
        await r.resolve('tt9999999');
        assert.equal(r.calls.length, 1);
        assert.equal(r.logs.length, 1);
    } finally { r.dom.window.close(); }
});

test('наличие глобального объекта Enhanced не подменило серверную проверку', async () => {
    const r = runtime();
    try {
        r.window.JellyfinEnhanced = { pluginConfig: { TmdbEnabled: true, ElsewhereEnabled: true } };
        assert.equal(await r.resolve('tt1000000'), null);
        assert.equal(r.calls.some(call => call.path.startsWith('/JellyfinEnhanced/')), false);
    } finally { r.dom.window.close(); }
});

test('загруженный Enhanced выполнил 30 параллельных сопоставлений через общую очередь', async () => {
    const r = runtime({ available: true, media: async () => {
        await new Promise(resolve => setTimeout(resolve, 2));
        return response({ movie_results: [{ id: 501 }], tv_results: [{ id: 902 }] });
    } });
    try {
        const ids = await Promise.all(Array.from({ length: 30 }, (_, i) => r.resolve('tt' + (2000000 + i), i % 2 ? 'Series' : 'Movie')));
        assert.deepEqual([...ids], Array.from({ length: 30 }, (_, i) => i % 2 ? '902' : '501'));
        assert.equal(r.maximum, 1);
        assert.equal(r.calls.filter(call => call.path === '/KinopoiskRequests/integrations').length, 1);
        assert.equal(r.calls.filter(call => call.path === '/JellyfinEnhanced/public-config').length, 1);
        for (const call of r.calls) {
            assert.equal(call.request.headers.Authorization, 'MediaBrowser Token="fixture-token"');
            assert.equal(call.path.includes('fixture-token'), false);
        }
    } finally { r.dom.window.close(); }
});

test('повторное сопоставление одного IMDb ID объединилось в один запрос', async () => {
    const r = runtime({ available: true });
    try {
        assert.ok((await Promise.all(Array.from({ length: 30 }, () => r.resolve('tt2000000')))).every(id => id === '501'));
        assert.equal(r.calls.filter(call => call.path.includes('/tmdb/find/')).length, 1);
    } finally { r.dom.window.close(); }
});

test('отключённый TMDB не помешал обнаружению Enhanced и не запустил поиск', async () => {
    const r = runtime({ available: true, tmdb: false });
    try {
        assert.equal(await r.resolve('tt1000000'), null);
        assert.equal((await r.api.status()).available, true);
        assert.equal(r.calls.some(call => call.path.includes('/tmdb/find/')), false);
    } finally { r.dom.window.close(); }
});

test('отсутствующий IMDb ID не отключил исправную интеграцию и отрицательно кэшировался', async () => {
    const r = runtime({ available: true, media: target => target.includes('tt1000000')
        ? response({}, 404) : response({ movie_results: [{ id: 501 }] }) });
    try {
        assert.equal(await r.resolve('tt1000000'), null);
        assert.equal(await r.resolve('tt1000001'), '501');
        assert.equal(await r.resolve('tt1000000'), null);
        assert.equal((await r.api.status()).available, true);
        assert.equal(r.calls.filter(call => call.path.includes('tt1000000')).length, 1);
    } finally { r.dom.window.close(); }
});

test('различные 404 ограничились тремя URL за минуту и не повторили HTTP probing', async () => {
    const r = runtime({ available: true, media: () => response({}, 404) });
    try {
        for (let minute = 0; minute < 3; minute++) {
            await Promise.all(Array.from({ length: 40 }, (_, i) => r.resolve('tt' + (3000000 + minute * 100 + i))));
            assert.equal(r.calls.filter(call => call.path.includes('/tmdb/find/')).length, (minute + 1) * 3);
            r.tick(60001);
        }
        let bucket = 0;
        let previous = 0;
        for (const call of r.calls.filter(call => call.path.includes('/tmdb/find/'))) {
            bucket = Math.max(0, bucket - (call.at - previous) / 10000) + 1;
            assert.ok(bucket <= 10, 'Превышена ёмкость http-probing');
            previous = call.at;
        }
        assert.equal((await r.api.status()).available, true);
    } finally { r.dom.window.close(); }
});

for (const failure of [502, 503, 429, 'network', 'timeout']) {
    test('временный сбой остановил очередь и восстановился после паузы: ' + failure, async () => {
        let broken = true;
        const r = runtime({ available: true, media: () => {
            if (!broken) return response({ movie_results: [{ id: 501 }] });
            if (typeof failure === 'number') return response({}, failure);
            throw failure === 'timeout' ? new DOMException('fixture', 'AbortError') : new TypeError('fixture');
        } });
        try {
            await Promise.all(Array.from({ length: 40 }, (_, i) => r.resolve('tt' + (4000000 + i))));
            assert.equal(r.calls.filter(call => call.path.includes('/tmdb/find/')).length, 1);
            const count = r.calls.length;
            assert.equal(await r.resolve('tt4999999'), null);
            assert.equal(r.calls.length, count);
            broken = false;
            r.tick(30001);
            assert.equal(await r.resolve('tt5000000'), '501');
            assert.equal(r.calls.filter(call => call.path === '/JellyfinEnhanced/public-config').length, 2);
            assert.equal(r.logs.length, 3);
        } finally { r.dom.window.close(); }
    });
}

test('Enhanced обнаружился после появления в реестре без пересоздания собственного клиента', async () => {
    const r = runtime();
    try {
        assert.equal(await r.resolve('tt1000000'), null);
        r.install();
        assert.equal(await r.resolve('tt1000001'), null);
        r.tick(60001);
        assert.equal(await r.resolve('tt1000000'), '501');
        assert.equal(r.calls.filter(call => call.path === '/KinopoiskRequests/integrations').length, 2);
    } finally { r.dom.window.close(); }
});

for (const status of [404, 503]) {
    test('сбой постоянного endpoint конфигурации остановил все обращения к объектам: ' + status, async () => {
        const r = runtime({ available: true, config: () => response({}, status) });
        try {
            await Promise.all(Array.from({ length: 40 }, (_, i) => r.resolve('tt' + (6000000 + i))));
            assert.equal(r.calls.filter(call => call.path === '/JellyfinEnhanced/public-config').length, 1);
            assert.equal(r.calls.some(call => call.path.includes('/tmdb/find/')), false);
        } finally { r.dom.window.close(); }
    });
}

test('неоднозначное сопоставление не выбрало произвольный фильм', async () => {
    const r = runtime({ available: true, media: () => response({ movie_results: [{ id: 501 }, { id: 502 }] }) });
    try { assert.equal(await r.resolve('tt1000000'), null); }
    finally { r.dom.window.close(); }
});

test('поздняя проверка прежнего пользователя не вызвала Enhanced после выхода', async () => {
    let complete;
    const r = runtime({ inventory: () => new Promise(resolve => { complete = resolve; }) });
    try {
        const old = r.resolve('tt1000000');
        await new Promise(resolve => setTimeout(resolve, 0));
        r.logout();
        complete(response({ enhanced: { available: true, state: 'available' } }));
        assert.equal(await old, null);
        assert.equal(r.calls.some(call => call.path.startsWith('/JellyfinEnhanced/')), false);
    } finally { r.dom.window.close(); }
});

test('явно подтверждённые POST не объединялись и не повторялись автоматически', async () => {
    const r = runtime({ available: true });
    let count = 0;
    try {
        await Promise.all([1, 2].map(id => r.api.run('/JellyfinEnhanced/jellyseerr/request', () => {
            count += 1;
            return Promise.resolve(id);
        }, false)));
        assert.equal(count, 2);
        await assert.rejects(r.api.run('/JellyfinEnhanced/jellyseerr/request', () => {
            count += 1; throw new TypeError('fixture');
        }, false));
        assert.equal(count, 3);
    } finally { r.dom.window.close(); }
});

test('некорректный ответ реестра не разрешил запрос к необязательному расширению', async () => {
    const r = runtime({ inventory: () => response({ items: [] }) });
    try {
        await Promise.all(Array.from({ length: 25 }, (_, i) => r.resolve('tt' + (7000000 + i))));
        assert.equal(r.calls.length, 1);
    } finally { r.dom.window.close(); }
});


test('повторные сбои увеличили паузу с 30 до 60 секунд', async () => {
    const r = runtime({ available: true, media: () => response({}, 503) });
    try {
        await r.resolve('tt8100000');
        r.tick(30001);
        await r.resolve('tt8100001');
        assert.equal(r.calls.filter(call => call.path.includes('/tmdb/find/')).length, 2);
        r.tick(30001);
        await r.resolve('tt8100002');
        assert.equal(r.calls.filter(call => call.path.includes('/tmdb/find/')).length, 2);
        r.tick(30001);
        await r.resolve('tt8100003');
        assert.equal(r.calls.filter(call => call.path.includes('/tmdb/find/')).length, 3);
    } finally { r.dom.window.close(); }
});
