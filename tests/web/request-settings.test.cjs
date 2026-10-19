const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const html = fs.readFileSync(path.resolve(__dirname,
    '../../src/Jellyfin.Plugin.Kinopoisk/Configuration/configPage.html'), 'utf8');

// A small DOM adapter for the methods used by the Jellyfin configuration page.
// The production page script and its real click handlers run unchanged.
function configRuntime(provider, enhanced) {
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
        if (url === '/JellyfinEnhanced/version') return new Response(enhanced ? '12.6.0.0' : '', { status: enhanced ? 200 : 404 });
        if (url === '/KinopoiskRequests/user-status') return Response.json({ provider,
            active: provider === 'seerr', userFound: provider === 'seerr', reason: provider === 'disabled' ? 'disabled' : undefined });
        if (url === '/JellyfinEnhanced/jellyseerr/user-status') return Response.json(enhanced
            ? { active: true, userFound: true } : {}, { status: enhanced ? 200 : 404 });
        throw new Error('Unexpected request: ' + url);
    };
    for (const script of window.document.querySelectorAll('script:not([src])')) window.eval(script.textContent);
    return { dom, window, requests, get writes() { return writes; } };
}

for (const [provider, enhanced, message] of [
    ['seerr', false, 'Seerr подключён напрямую. Учётная запись связана.'],
    ['enhanced', true, 'Seerr подключён через Enhanced. Учётная запись связана.'],
    ['enhanced', false, 'Подключите Seerr напрямую: укажите адрес и API-ключ, затем сохраните настройки.'],
    ['disabled', false, 'Запросы отключены. Рекомендации и подробности доступны.']
]) {
    test('настройки проверяют сохранённое подключение и наличие Enhanced: ' + provider + '/' + enhanced, async () => {
        const runtime = configRuntime(provider, enhanced);
        try {
            await runtime.window.loadConfiguration();
            while (runtime.window.document.querySelector('#CheckSeerrConnection').disabled) {
                await new Promise(resolve => setTimeout(resolve, 20));
            }
            // Checking a connection never saves an edited API key or creates a request.
            runtime.window.document.querySelector('#SeerrApiKey').value = 'unsaved-secret';
            runtime.window.document.querySelector('#CheckSeerrConnection').click();
            const started = Date.now();
            while (runtime.window.document.querySelector('#CheckSeerrConnection').disabled && Date.now() - started < 3000) {
                await new Promise(resolve => setTimeout(resolve, 20));
            }
            assert.equal(runtime.window.document.querySelector('#SeerrConnectionStatus').textContent, message);
            assert.equal(runtime.window.document.querySelector('#EnhancedDetectionStatus').textContent, enhanced ? 'обнаружен' : 'не установлен');
            assert.equal(runtime.window.document.querySelector('#SeerrApiKey').type, 'password');
            assert.equal(runtime.writes, 0);
            assert.equal(runtime.window.JellyfinEnhanced, undefined);
            for (const request of runtime.requests) {
                assert.equal(request.options.body, undefined);
                assert.equal(request.options.headers.Authorization, 'MediaBrowser Token="session-token"');
                assert.equal(JSON.stringify(request).includes('service-secret'), false);
                assert.equal(JSON.stringify(request).includes('unsaved-secret'), false);
            }
        } finally { runtime.dom.window.close(); }
    });
}
