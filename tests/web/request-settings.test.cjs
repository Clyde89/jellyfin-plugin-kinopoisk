const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const html = fs.readFileSync(path.resolve(__dirname,
    '../../src/Jellyfin.Plugin.Kinopoisk/Configuration/configPage.html'), 'utf8');

// A small DOM adapter for the methods used by the Jellyfin configuration page.
// The production page script and its real click handlers run unchanged.
function configRuntime(provider, enhanced, failure) {
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'https://jellyfin.test/web/' });
    const { window } = dom;
    const requests = [];
    let writes = 0;
    window.$ = selector => {
        const nodes = [...window.document.querySelectorAll(selector)];
        const adapter = {
            prop(name, value) { if (value === undefined) return nodes[0]?.[name]; nodes.forEach(n => { n[name] = value; }); return adapter; },
            attr(name, value) { nodes.forEach(n => n.setAttribute(name, value)); return adapter; },
            val(value) { if (value === undefined) return nodes[0]?.value; nodes.forEach(n => { n.value = value; }); return adapter; },
            text(value) { nodes.forEach(n => { n.textContent = value; }); return adapter; },
            change() { return adapter; },
            on(name, handler) { nodes.forEach(n => n.addEventListener(name, handler)); return adapter; }
        };
        return adapter;
    };
    window.AbortController = AbortController;
    window.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} };
    window.ApiClient = {
        getUrl: value => value,
        accessToken: () => 'session-token',
        getCurrentUserId: () => 'linked-user',
        getPluginConfiguration: async () => ({ RecommendationRequestProvider: provider,
            SeerrUrl: 'http://configured-seerr:5055', SeerrApiKey: 'service-secret' }),
        updatePluginConfiguration: async () => { writes += 1; }
    };
    window.fetch = async (url, options) => {
        requests.push({ url, options });
        if (url === '/KinopoiskRequests/integrations') return Response.json({ enhanced: { available: enhanced, state: enhanced ? 'available' : 'missing' } });
        if (url === '/KinopoiskRequests/user-status') return Response.json({ provider,
            active: provider === 'seerr', userFound: provider === 'seerr', reason: provider === 'disabled' ? 'disabled' : undefined });
        if (url === '/KinopoiskRequests/connection-test') {
            assert.equal(options.method, 'POST');
            if (failure === 'timeout') throw new window.DOMException('Timed out', 'AbortError');
            if (failure) return Response.json({ message: failure }, { status: 502 });
            const draft = JSON.parse(options.body);
            const selected = draft.provider === 'auto' ? (draft.url || draft.apiKey ? 'seerr' : 'enhanced') : draft.provider;
            return Response.json({ provider: selected, active: selected === 'seerr', userFound: selected === 'seerr',
                reason: selected === 'disabled' ? 'disabled' : undefined });
        }
        if (url === '/JellyfinEnhanced/jellyseerr/user-status') return Response.json(enhanced
            ? { active: true, userFound: true } : {}, { status: enhanced ? 200 : 404 });
        throw new Error('Unexpected request: ' + url);
    };
    for (const script of window.document.querySelectorAll('script:not([src])')) window.eval(script.textContent);
    return { dom, window, requests, get writes() { return writes; } };
}

for (const [provider, enhanced, message] of [
    ['seerr', false, 'Seerr подключён напрямую. Учётная запись связана.'],
    ['auto', false, 'Seerr подключён напрямую. Учётная запись связана.'],
    ['enhanced', true, 'Seerr подключён через Enhanced. Учётная запись связана.'],
    ['enhanced', false, 'Подключите Seerr напрямую: укажите адрес и API-ключ, затем сохраните настройки.'],
    ['disabled', false, 'Запросы отключены. Рекомендации и подробности доступны.']
]) {
    test('настройки проверяют введённые параметры без сохранения: ' + provider + '/' + enhanced, async () => {
        const runtime = configRuntime(provider, enhanced);
        try {
            await runtime.window.loadConfiguration();
            while (runtime.window.document.querySelector('#CheckSeerrConnection').disabled) {
                await new Promise(resolve => setTimeout(resolve, 20));
            }
            // Draft credentials go only to the authenticated same-origin check.
            runtime.window.document.querySelector('#SeerrApiKey').value = 'unsaved-secret';
            runtime.window.document.querySelector('#SeerrUrl').value = 'http://draft-seerr:5055';
            runtime.window.document.querySelector('#CheckSeerrConnection').click();
            const started = Date.now();
            while (runtime.window.document.querySelector('#CheckSeerrConnection').disabled && Date.now() - started < 3000) {
                await new Promise(resolve => setTimeout(resolve, 20));
            }
            assert.equal(runtime.window.document.querySelector('#SeerrConnectionStatus').textContent,
                message + ' Проверены введённые параметры. Для применения сохраните настройки.');
            assert.equal(runtime.window.document.querySelector('#EnhancedDetectionStatus').textContent, enhanced ? 'обнаружен' : 'не установлен');
            assert.equal(runtime.window.document.querySelector('#SeerrApiKey').type, 'password');
            assert.equal(runtime.writes, 0);
            if (!enhanced) { assert.equal(runtime.requests.some(request => request.url.startsWith('/JellyfinEnhanced/')), false); }
            assert.equal(runtime.window.JellyfinEnhanced, undefined);
            const preview = runtime.requests.filter(request => request.options.method === 'POST');
            assert.equal(preview.length, 1);
            assert.equal(preview[0].url, '/KinopoiskRequests/connection-test');
            assert.deepEqual(JSON.parse(preview[0].options.body), { provider, url: 'http://draft-seerr:5055', apiKey: 'unsaved-secret' });
            for (const request of runtime.requests) {
                assert.equal(request.options.headers.Authorization, 'MediaBrowser Token="session-token"');
                assert.equal(JSON.stringify(request.options.headers).includes('unsaved-secret'), false);
                assert.equal(request.url.includes('unsaved-secret'), false);
                if (request.url !== '/KinopoiskRequests/connection-test') assert.equal(request.options.body, undefined);
            }
        } finally { runtime.dom.window.close(); }
    });
}

test('проверяется выбранный в форме режим вместо сохранённого подключения', async () => {
    const runtime = configRuntime('enhanced', false);
    try {
        await runtime.window.loadConfiguration();
        runtime.window.document.querySelector('#RecommendationRequestProvider').value = 'seerr';
        await runtime.window.loadRequestConnection(true);
        assert.match(runtime.window.document.querySelector('#SeerrConnectionStatus').textContent, /^Seerr подключён напрямую/);
        assert.equal(runtime.writes, 0);
    } finally { runtime.dom.window.close(); }
});

for (const failure of ['Seerr отклонил API-ключ.', 'Не удалось подключиться к Seerr.', 'Получен ответ не в формате Seerr API.', 'timeout']) {
    test('проверка введённых параметров объясняет ошибку: ' + failure, async () => {
        const runtime = configRuntime('seerr', false, failure);
        try {
            await runtime.window.loadConfiguration();
            await runtime.window.loadRequestConnection(true);
            assert.equal(runtime.window.document.querySelector('#SeerrConnectionStatus').textContent,
                failure === 'timeout' ? 'Seerr не ответил вовремя.' : failure);
            assert.equal(runtime.window.document.querySelector('#CheckSeerrConnection').disabled, false);
            assert.equal(runtime.writes, 0);
        } finally { runtime.dom.window.close(); }
    });
}

test('смена полей во время проверки не позволяет старому результату заменить сообщение', async () => {
    const runtime = configRuntime('seerr', false);
    let complete;
    try {
        await runtime.window.loadConfiguration();
        const originalFetch = runtime.window.fetch;
        runtime.window.fetch = (url, options) => url === '/KinopoiskRequests/connection-test'
            ? new Promise(resolve => { complete = resolve; }) : originalFetch(url, options);
        const check = runtime.window.loadRequestConnection(true);
        const input = runtime.window.document.querySelector('#SeerrUrl');
        input.value = 'http://another-seerr:5055';
        input.dispatchEvent(new runtime.window.Event('input', { bubbles: true }));
        complete(Response.json({ provider: 'seerr', active: true, userFound: true }));
        await check;
        assert.match(runtime.window.document.querySelector('#SeerrConnectionStatus').textContent, /Параметры изменены/);
        assert.equal(runtime.window.document.querySelector('#CheckSeerrConnection').disabled, false);
        assert.equal(runtime.writes, 0);
    } finally { runtime.dom.window.close(); }
});


test('адрес ссылки выбран независимо от адреса серверного подключения', async () => {
    const runtime = configRuntime('seerr', false);
    try {
        await runtime.window.loadConfiguration();
        await runtime.window.loadRequestConnection();
        const mode = runtime.window.document.querySelector('#SeerrLinkMode');
        const url = runtime.window.document.querySelector('#SeerrWebUrl');
        assert.equal(mode.value, 'auto');
        assert.equal(url.disabled, true);
        mode.value = 'custom';
        mode.dispatchEvent(new runtime.window.Event('change'));
        assert.equal(url.disabled, false);
        assert.equal(url.required, true);
        url.value = 'https://public.example/seerr';
        url.dispatchEvent(new runtime.window.Event('input'));
        assert.equal(url.checkValidity(), true);
        const config = runtime.window.copyFormToConfiguration({});
        assert.equal(config.SeerrLinkMode, 'custom');
        assert.equal(config.SeerrWebUrl, 'https://public.example/seerr');
        assert.equal(config.SeerrUrl, 'http://configured-seerr:5055');
        url.value = 'https://u:password@public.example/seerr';
        url.dispatchEvent(new runtime.window.Event('input'));
        assert.equal(url.checkValidity(), false);
        assert.equal(runtime.writes, 0);
    } finally { runtime.dom.window.close(); }
});
