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
    const names = options.lifecycle ? ['kinopoiskDetailPageLifecycle.js', 'kinopoiskRecommendationActions.js', 'kinopoiskRecommendations.js'] : ['kinopoiskRecommendationActions.js', 'kinopoiskRecommendations.js'];
    for (const name of names) window.eval(fs.readFileSync(path.join(root, name), 'utf8'));
    return { dom, window, calls, lists, switchUser(value) { currentUser = value; window.KinopoiskRecommendationActions.refresh(); },
        favorites(active = true) {
            window.document.querySelector('.itemDetailPage').hidden = true;
            let home = window.document.querySelector('#indexPage');
            if (!home) {
                home = window.document.createElement('div');
                home.id = 'indexPage';
                home.innerHTML = '<div id="homeTab" class="tabContent"><div class="sections">Главная</div></div><div id="favoritesTab" class="tabContent"><div class="sections"><section id="native-favorites">Штатное избранное</section></div></div>';
                window.document.body.appendChild(home);
            }
            window.location.hash = '#/home?tab=' + (active ? 1 : 0);
            home.querySelector('#favoritesTab').classList.toggle('is-active', active);
        },
        get favorite() { return favorite; }, get heart() { return window.document.querySelector('.kp-similar-card__heart'); } };
}

for (const library of [false, true]) {
    test('повторное обновление не перемещает значок и не перезапускает наведение, медиатека=' + library, async () => {
        const app = fixture({ library, lifecycle: true });
        try {
            await wait(() => app.heart && !app.heart.disabled, 'Действия не готовы.');
            // Observe the real lifecycle: moving the icon used to cause a new
            // render, moving it again indefinitely even without pointer input.
            await new Promise(resolve => setTimeout(resolve, 500));
            const card = app.window.document.querySelector('.kp-similar-card');
            const mutations = [];
            const observer = new app.window.MutationObserver(entries => mutations.push(...entries));
            observer.observe(card, { childList: true, subtree: true, attributes: true });
            for (let i = 0; i < 4; i++) {
                app.window.KinopoiskRecommendationActions.refresh();
                await new Promise(resolve => setTimeout(resolve, 150));
            }
            observer.disconnect();
            assert.equal(mutations.length, 0, 'Неизменённая карточка продолжает перерисовываться.');
        } finally { app.dom.window.close(); }
    });
}

test('желаемое открыто в штатном избранном, удалено из обоих представлений и очищено при уходе', async () => {
    const app = fixture({ lifecycle: true });
    try {
        await wait(() => app.heart && !app.heart.disabled, 'Действия не готовы.');
        app.heart.click();
        await wait(() => app.heart.getAttribute('aria-pressed') === 'true', 'Фильм не сохранён.');
        app.favorites();
        const section = () => app.window.document.querySelector('#kinopoiskFavoritesWishlist');
        await wait(() => section()?.querySelector('.kp-similar-card__title')?.textContent === 'Сохранённый фильм', 'Желаемое отсутствует в штатном избранном.');
        assert.ok(app.window.document.querySelector('#native-favorites'), 'Штатные разделы удалены.');
        await wait(() => section()?.querySelector('.kp-similar-card__heart')?.disabled === false, 'Действия в избранном не готовы.');
        assert.equal(section().querySelector('.kp-similar-card__heart').getAttribute('aria-pressed'), 'true');
        section().querySelector('.kp-similar-card__heart').click();
        await wait(() => section()?.textContent.includes('В желаемом пока ничего нет'), 'Удаление не обновило избранное.');
        assert.equal(app.lists.get('first').length, 0);
        assert.equal(app.calls.some(call => /Seerr|jellyseerr|KinopoiskRequests/.test(call.url)), false);
        app.favorites(false);
        await wait(() => !section(), 'Желаемое осталось на главной вкладке.');
    } finally { app.dom.window.close(); }
});

test('штатное избранное не показывает желаемое прежнего пользователя или запоздавший ответ', async () => {
    const app = fixture({ lifecycle: true });
    try {
        app.lists.set('first', [{ kinopoiskId: 101, name: 'Первый пользователь', mediaType: 'movie' }]);
        app.favorites();
        const section = () => app.window.document.querySelector('#kinopoiskFavoritesWishlist');
        await wait(() => section()?.textContent.includes('Первый пользователь'), 'Первый список не открыт.');
        const original = app.window.fetch;
        let release;
        app.window.fetch = (url, options) => url === '/KinopoiskRecommendations/wishlist'
            ? new Promise(resolve => { release = () => resolve(Response.json({ items: [{ kinopoiskId: 102, name: 'Запоздавший ответ' }] })); })
            : original(url, options);
        app.window.document.dispatchEvent(new app.window.CustomEvent('kinopoisk:wishlist-changed', { detail: { userId: 'first' } }));
        await wait(() => release, 'Запрос списка не начат.');
        app.window.fetch = original;
        app.switchUser('second');
        await wait(() => section()?.dataset.userId === 'second' && section().textContent.includes('В желаемом пока ничего нет'), 'Список нового пользователя не открыт.');
        release();
        await new Promise(resolve => setTimeout(resolve, 200));
        assert.equal(section().textContent.includes('Первый пользователь'), false);
        assert.equal(section().textContent.includes('Запоздавший ответ'), false);
    } finally { app.dom.window.close(); }
});

test('желаемое в избранном перелистывается и восстанавливается после штатного пересоздания разделов', async () => {
    const app = fixture();
    try {
        app.lists.set('first', Array.from({ length: 23 }, (_, index) => ({ kinopoiskId: 101 + index, name: 'Фильм ' + (index + 1), mediaType: 'movie' })));
        app.favorites();
        const section = () => app.window.document.querySelector('#kinopoiskFavoritesWishlist');
        await wait(() => section()?.querySelectorAll('.kp-similar-card').length === 20, 'Первая страница не ограничена.');
        [...section().querySelectorAll('button')].find(button => button.textContent === 'Далее').click();
        await wait(() => section()?.querySelectorAll('.kp-similar-card').length === 3, 'Вторая страница не открыта.');
        assert.ok(section().textContent.includes('Фильм 23'));
        app.window.document.querySelector('#favoritesTab .sections').innerHTML = '<section id="native-favorites">Обновлённое избранное</section>';
        await wait(() => section()?.querySelectorAll('.kp-similar-card').length === 20, 'Штатная инициализация удалила желаемое.');
        assert.ok(app.window.document.querySelector('#native-favorites'));
    } finally { app.dom.window.close(); }
});

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
