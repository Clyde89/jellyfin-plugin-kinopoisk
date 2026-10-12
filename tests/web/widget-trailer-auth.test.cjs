const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const script = fs.readFileSync(path.resolve(
    __dirname, '../../src/Jellyfin.Plugin.Kinopoisk/Web/kinopoiskWidgetTrailerPlayer.js'), 'utf8');
const token = 'test-jellyfin-token';

function createRuntime(baseUrl, localTrailerCount, statusCode = 200) {
    const dom = new JSDOM('<!doctype html><html><body><button class="btnPlayTrailer">Трейлер</button></body></html>', {
        runScripts: 'outside-only',
        url: 'https://jellyfin.test' + baseUrl + '/web/#/details?id=movie-1'
    });
    const { window } = dom;
    const requests = [];
    let nativeClicks = 0;
    window.console = { info() {}, warn() {}, error() {} };
    window.ApiClient = {
        accessToken: () => token,
        getCurrentUserId: () => 'user-1',
        getUrl: route => 'https://jellyfin.test' + baseUrl + route,
        getItem: () => Promise.resolve({
            Id: 'movie-1',
            LocalTrailerCount: localTrailerCount,
            ProviderIds: { kinopoisk: '301' },
            RemoteTrailers: [{ Url: 'https://widgets.kinopoisk.ru/discovery/film/301/trailer/1', Name: 'Трейлер' }]
        })
    };
    window.fetch = async (url, options) => {
        requests.push({ url, options });
        const authorized = options.headers.Authorization === 'MediaBrowser Token="' + token + '"';
        return new Response(JSON.stringify({ State: 'preparing', Message: 'Трейлер подготавливается на сервере' }), {
            status: authorized ? statusCode : 401,
            headers: { 'content-type': 'application/json' }
        });
    };
    window.eval(script);
    const button = window.document.querySelector('.btnPlayTrailer');
    button.addEventListener('click', () => nativeClicks++);
    return { dom, window, requests, button, nativeClicks: () => nativeClicks };
}

async function waitFor(predicate) {
    for (let attempt = 0; attempt < 100; attempt++) {
        if (predicate()) return;
        await new Promise(resolve => setTimeout(resolve, 10));
    }
    assert.fail('Ожидаемое действие кнопки трейлера не выполнено.');
}

for (const baseUrl of ['', '/jellyfin']) {
    test('состояние трейлера авторизовано без legacy-заголовка: ' + (baseUrl || 'корень'), async t => {
        const runtime = createRuntime(baseUrl, 1);
        t.after(() => runtime.dom.window.close());

        runtime.button.click();
        await waitFor(() => runtime.nativeClicks() === 1);

        assert.equal(runtime.requests.length, 1);
        const { url, options } = runtime.requests[0];
        assert.equal(url, 'https://jellyfin.test' + baseUrl + '/KinopoiskPlayback/cache/status/301');
        assert.equal(options.headers.Authorization, 'MediaBrowser Token="' + token + '"');
        assert.equal(options.headers['X-Emby-Token'], undefined);
        assert.equal(options.credentials, 'same-origin');
        assert.equal(options.cache, 'no-store');
        assert.equal(url.includes(token), false);
        assert.ok(runtime.window.document.querySelector('#kinopoiskTrailerPreparationNotice'));
        assert.equal(runtime.window.document.querySelector('iframe'), null);
    });
}

test('токен Jellyfin не передан внешнему виджету трейлера', async t => {
    const runtime = createRuntime('/jellyfin', 0);
    t.after(() => runtime.dom.window.close());

    runtime.button.click();
    await waitFor(() => runtime.window.document.querySelector('iframe'));

    const frame = runtime.window.document.querySelector('iframe');
    assert.equal(new URL(frame.src).origin, 'https://widgets.kinopoisk.ru');
    assert.equal(frame.outerHTML.includes(token), false);
    assert.equal(frame.outerHTML.includes('Authorization'), false);
    assert.equal(frame.src.includes('api_key'), false);
    assert.equal(frame.src.includes('ApiKey'), false);
    assert.equal(runtime.requests.length, 0);
    assert.equal(runtime.nativeClicks(), 0);
});

test('ошибка авторизации состояния не заблокировала штатный запуск трейлера', async t => {
    const runtime = createRuntime('', 1, 401);
    t.after(() => runtime.dom.window.close());

    runtime.button.click();
    await waitFor(() => runtime.nativeClicks() === 1);

    assert.equal(runtime.requests.length, 1);
    assert.equal(runtime.window.document.querySelector('#kinopoiskTrailerPreparationNotice'), null);
    assert.equal(runtime.window.document.querySelector('iframe'), null);
});
