const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');
const root = path.resolve(__dirname, '../../src/Jellyfin.Plugin.Kinopoisk/Web');
const wait = async (condition, message) => {
    const started = Date.now();
    while (Date.now() - started < 4000) { if (condition()) return; await new Promise(resolve => setTimeout(resolve, 20)); }
    assert.fail(message);
};
function fixture(options = {}) {
    const dom = new JSDOM('<!doctype html><div class="itemDetailPage"><section id="similarCollapsible"><div class="itemsContainer"></div></section></div>', {
        url: 'https://jellyfin.test/web/#/details?id=item-1', runScripts: 'outside-only', pretendToBeVisual: true
    });
    const { window } = dom;
    window.console = { info() {}, debug() {}, warn() {}, error() {} };
    window.AbortController = AbortController;
    let currentUser = 'first';
    let favorite = false;
    const lists = new Map();
    const calls = [];
    window.ApiClient = { getCurrentUserId: () => currentUser, accessToken: () => 'token-' + currentUser,
        serverInfo: () => ({ Id: 'server-1' }), getUrl: value => value,
        getItem: async () => ({ Id: 'item-1', Type: 'Movie', ProviderIds: { Kinopoisk: '100' } }) };
    window.fetch = async (url, request = {}) => {
        calls.push({ url: String(url), ...request });
        const data = lists.get(currentUser) || [];
        if (String(url).endsWith('/similars')) return Response.json({ items: [{ kinopoiskId: 101, name: 'Сохранённый фильм', year: 2025, mediaType: 'movie' }] });
        if (String(url).startsWith('/KinopoiskRecommendations/state?')) {
            if (options.state) return options.state(currentUser);
            return Response.json({ items: [{ key: 'movie:kp:101', itemId: options.library ? 'library-101' : null,
                canPlay: !!options.library, favorite, playbackPositionTicks: 12345, wished: data.length > 0 }] });
        }
        if (url === '/KinopoiskRecommendations/wishlist' && request.method === 'PUT') {
            if (options.failSave) return Response.json({ message: 'Ошибка сохранения.' }, { status: 503 });
            lists.set(currentUser, [JSON.parse(request.body)]);
            return Response.json({ saved: true });
        }
        if (url === '/KinopoiskRecommendations/wishlist') return Response.json({ items: data });
        if (String(url).startsWith('/KinopoiskRecommendations/wishlist/')) { lists.set(currentUser, []); return Response.json({ saved: false }); }
        if (String(url).startsWith('/UserFavoriteItems/')) { favorite = request.method === 'POST'; return Response.json({ IsFavorite: favorite }); }
        throw new Error('Unexpected route: ' + url);
    };
    for (const name of ['kinopoiskRecommendationActions.js', 'kinopoiskRecommendations.js']) window.eval(fs.readFileSync(path.join(root, name), 'utf8'));
    return { dom, window, calls, lists, switchUser(value) { currentUser = value; window.KinopoiskRecommendationActions.refresh(); },
        get favorite() { return favorite; }, get heart() { return window.document.querySelector('.kp-similar-card__heart'); } };
}

test('личное желаемое сохранено и открыто без Seerr и Enhanced', async () => {
    const app = fixture();
    try {
        await wait(() => app.heart && !app.heart.disabled, 'Сердечко не подготовлено.');
        app.heart.click(); app.heart.click();
        await wait(() => app.heart.getAttribute('aria-pressed') === 'true', 'Фильм не сохранён.');
        assert.equal(app.calls.filter(call => call.method === 'PUT').length, 1);
        assert.equal(app.window.document.querySelector('.kp-similar-card__play').hidden, true);
        app.window.document.querySelector('[data-source="wishlist"]').click();
        await wait(() => app.window.document.querySelector('.kp-similar-card__title')?.textContent === 'Сохранённый фильм', 'Желаемое не открыто.');
        await wait(() => app.heart && !app.heart.disabled, 'Состояние желаемого не получено.');
        app.heart.click();
        await wait(() => app.window.document.querySelector('.kp-recommendations-items').textContent.includes('В желаемом пока ничего нет'), 'Удаление не обновило список.');
        assert.equal(app.calls.some(call => /Seerr|jellyseerr|KinopoiskRequests/.test(call.url)), false);
        assert.equal(app.window.JellyfinEnhanced, undefined);
    } finally { app.dom.window.close(); }
});

test('доступный фильм получил штатное действие resume и избранное Jellyfin', async () => {
    const app = fixture({ library: true });
    try {
        await wait(() => app.heart && !app.heart.disabled, 'Действия не подготовлены.');
        const card = app.window.document.querySelector('.kp-similar-card');
        const play = card.querySelector('.kp-similar-card__play');
        assert.equal(play.hidden, false);
        assert.equal(play.dataset.action, 'resume');
        assert.equal(play.getAttribute('aria-label'), 'Продолжить просмотр');
        assert.equal(play.querySelector('.material-icons').textContent, '');
        assert.equal(play.querySelector('.material-icons').classList.contains('play_arrow'), true);
        assert.equal(card.dataset.id, 'library-101');
        assert.equal(card.dataset.serverid, 'server-1');
        assert.equal(card.dataset.positionticks, '12345');
        assert.equal(card.querySelector('.kp-similar-card__native-actions').contains(card.querySelector('.kp-similar-card__info')), true);
        app.heart.click();
        await wait(() => app.favorite, 'Штатное избранное не обновилось.');
        assert.equal(app.calls.filter(call => call.url.startsWith('/UserFavoriteItems/') && call.method === 'POST').length, 1);
        assert.equal(app.calls.some(call => call.method === 'PUT'), false);
        assert.equal(app.lists.size, 0);
    } finally { app.dom.window.close(); }
});

test('ошибка сохранения не показана как заполненное сердечко', async () => {
    const app = fixture({ failSave: true });
    try {
        await wait(() => app.heart && !app.heart.disabled, 'Действия не готовы.');
        app.heart.click();
        await wait(() => app.window.document.querySelector('.kp-card-action-status').textContent.includes('Ошибка сохранения'), 'Ошибка не показана.');
        assert.equal(app.heart.getAttribute('aria-pressed'), 'false');
        assert.equal(app.window.document.querySelector('.kp-card-action-status').classList.contains('is-error'), true);
        assert.equal(app.lists.size, 0);
        assert.equal(app.calls.filter(call => call.method === 'PUT').length, 1);
    } finally { app.dom.window.close(); }
});

test('смена пользователя не переносит личное желаемое и отметку сердечка', async () => {
    const app = fixture();
    try {
        await wait(() => app.heart && !app.heart.disabled, 'Действия не готовы.');
        app.heart.click();
        await wait(() => app.heart.getAttribute('aria-pressed') === 'true', 'Фильм не сохранён.');
        app.switchUser('second');
        await wait(() => app.window.document.querySelector('#kinopoiskRecommendationsSection')?.dataset.userId === 'second', 'Раздел прежнего пользователя не заменён.');
        await wait(() => app.heart && !app.heart.disabled, 'Действия нового пользователя не готовы.');
        assert.equal(app.heart.getAttribute('aria-pressed'), 'false');
        app.window.document.querySelector('[data-source="wishlist"]').click();
        await wait(() => app.window.document.querySelector('.kp-recommendations-items').textContent.includes('В желаемом пока ничего нет'), 'Показан чужой список.');
        assert.equal(app.lists.get('first').length, 1);
        assert.equal(app.lists.has('second'), false);
    } finally { app.dom.window.close(); }
});
