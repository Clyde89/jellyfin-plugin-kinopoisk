const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const webRoot = path.resolve(__dirname, '../../src/Jellyfin.Plugin.Kinopoisk/Web');

function readScript(name) {
    return fs.readFileSync(path.join(webRoot, name), 'utf8');
}

function createRuntime(body, itemFactory, responseFactory) {
    const dom = new JSDOM('<!doctype html><html><head></head><body>' + body + '</body></html>', {
        pretendToBeVisual: true,
        runScripts: 'outside-only',
        url: 'https://jellyfin.test/web/#/details?id=item-1'
    });
    const { window } = dom;
    const requests = [];
    let objectUrlSequence = 0;
    window.console = {
        info() {},
        debug() {},
        warn() {},
        error() {}
    };
    window.Response = global.Response;
    window.Blob = global.Blob;
    window.AbortController = global.AbortController;
    window.URL.createObjectURL = () => 'blob:kinopoisk-' + String(++objectUrlSequence);
    window.URL.revokeObjectURL = () => {};
    window.ApiClient = {
        accessToken: () => 'test-token',
        getCurrentUserId: () => 'user-1',
        getUrl: value => value,
        getItem: (_userId, itemId) => Promise.resolve(itemFactory(itemId))
    };
    window.fetch = async (url, options) => {
        requests.push({ url: String(url), options });
        if (String(url) === '/KinopoiskRequests/integrations') {
            return jsonResponse({ enhanced: { available: window.__fixtureEnhanced === true,
                state: window.__fixtureEnhanced ? 'available' : 'missing' } });
        }
        if (String(url) === '/JellyfinEnhanced/public-config') {
            const response = await responseFactory(String(url));
            const config = await response.json();
            return jsonResponse({ TmdbEnabled: true, ...config }, response.status);
        }
        return responseFactory(String(url));
    };
    window.eval(readScript('kinopoiskEnhancedIntegration.js'));
    return { dom, window, requests };
}

async function waitFor(predicate, message, timeout = 3000) {
    const started = Date.now();
    while (Date.now() - started < timeout) {
        if (predicate()) {
            return;
        }
        await new Promise(resolve => setTimeout(resolve, 20));
    }
    assert.fail(message);
}

function jsonResponse(value, status = 200) {
    return new Response(JSON.stringify(value), {
        status,
        headers: { 'content-type': 'application/json' }
    });
}

function imageResponse() {
    return new Response(new Blob(['image']), {
        status: 200,
        headers: { 'content-type': 'image/jpeg' }
    });
}

function movie(itemId, kinopoiskId) {
    return {
        Id: itemId,
        Type: 'Movie',
        ProviderIds: {
            Kinopoisk: String(kinopoiskId),
            Imdb: 'tt13964560'
        }
    };
}

function presentation(kinopoiskId) {
    return {
        kinopoiskId,
        ratings: { kinopoisk: 7.1, kinopoiskVotes: 1000, imdb: 6.8, imdbVotes: 500 },
        releaseDates: [
            { type: 'WORLD_PREMIER', date: '2025-03-07', country: 'США', source: 'КиноПоиск' },
            { type: 'PREMIERE', date: '2025-03-20', country: 'Россия', source: 'КиноПоиск' },
            { type: 'COUNTRY_SPECIFIC', date: '2025-03-20', country: 'Россия', source: 'КиноПоиск' },
            { type: 'PREMIERE', date: '2025-04-01', country: 'Россия', source: 'КиноПоиск', reRelease: true }
        ],
        professions: [],
        relations: [{
            kinopoiskId: kinopoiskId + 1,
            name: 'Продолжение',
            relationType: 'SEQUEL',
            posterUrl: 'https://kinopoiskapiunofficial.tech/images/posters/kp/2.jpg',
            kinopoiskUrl: 'https://www.kinopoisk.ru/film/' + String(kinopoiskId + 1) + '/'
        }]
    };
}

async function createRecommendationRuntime(itemOverrides = {}, proxyResponse) {
    const runtime = createRuntime(
        '<div id="active" class="itemDetailPage"><div class="detailPagePrimaryContent"></div>'
            + '<div id="similarCollapsible"><div id="native-card" class="card"></div></div></div>',
        itemId => movie(itemId, 100),
        url => proxyResponse && (url.startsWith('/KinopoiskRequests/') || url.startsWith('/JellyfinEnhanced/')) ? proxyResponse(url)
            : url.startsWith('/KinopoiskPresentation/image?')
            ? imageResponse()
            : jsonResponse({ items: [{
                kinopoiskId: 101,
                name: 'Похожий фильм',
                mediaType: 'movie',
                year: 2025,
                ratingKinopoisk: 7.3,
                overview: 'Описание похожего фильма.',
                posterUrl: 'https://kinopoiskapiunofficial.tech/images/posters/kp/101.jpg',
                ...itemOverrides
            }] })
    );
    runtime.window.document.querySelector('#native-card').getBoundingClientRect = () => ({ width: 200 });
    runtime.window.eval(readScript('kinopoiskDetailPageLifecycle.js'));
    runtime.window.eval(readScript('kinopoiskRecommendations.js'));
    await waitFor(() => runtime.window.document.querySelector('.kp-similar-card'),
        'Резервная карточка рекомендации не появилась.');
    return runtime;
}

test('постер и название открывают действия внутри Jellyfin без внешнего перехода', async () => {
    const runtime = await createRecommendationRuntime({ posterUrl: null, overview: null });
    const { window } = runtime;
    try {
        const card = window.document.querySelector('.kp-similar-card');
        const poster = card.querySelector('.cardImageContainer');
        const title = card.querySelector('.cardText-first button');
        assert.equal(poster.tagName, 'BUTTON');
        assert.equal(title.tagName, 'BUTTON');
        assert.equal(card.querySelector('a[href*="kinopoisk.ru"]'), null);
        assert.ok(card.querySelector('.cardOverlayContainer .cardOverlayFab-primary'));
        assert.ok(card.querySelector('.kp-similar-card__request'));
        let opened = 0;
        window.document.addEventListener('kinopoisk:recommendation-open', event => {
            assert.equal(event.detail.card, card);
            opened += 1;
        });
        poster.click();
        title.click();
        card.querySelector('.kp-similar-card__request').click();
        assert.equal(opened, 3);
        assert.equal(window.location.hash, '#/details?id=item-1');
        assert.equal(window.document.getElementById('kinopoiskRecommendationsStyles').textContent.includes('linear-gradient'), false);
    } finally {
        runtime.dom.window.close();
    }
});

test('активная SPA-карточка, даты и владение верхней строкой обработаны согласованно', async () => {
    const body = `
        <div id="stale" class="itemDetailPage hide">
            <div class="itemMiscInfo-primary"></div>
            <div class="detailPagePrimaryContent"></div>
        </div>
        <div id="active" class="itemDetailPage">
            <div class="itemMiscInfo-primary"></div>
            <div class="detailPagePrimaryContent"></div>
        </div>`;
    const runtime = createRuntime(
        body,
        itemId => itemId === 'item-2' ? movie(itemId, 200) : movie(itemId, 100),
        url => {
            if (url.startsWith('/KinopoiskPresentation/image?')) {
                return imageResponse();
            }
            const match = url.match(/^\/KinopoiskPresentation\/(\d+)$/);
            return match ? jsonResponse(presentation(Number(match[1]))) : jsonResponse({ items: [] });
        }
    );
    const { window, requests } = runtime;
    window.eval(readScript('kinopoiskDetailPageLifecycle.js'));
    window.eval(readScript('kinopoiskEnhancedPresentation.js'));

    await waitFor(
        () => window.document.querySelector('#active #kinopoiskEnhancedPresentationPanel'),
        'Блок КиноПоиска не появился на активной карточке.'
    );
    assert.equal(window.document.querySelector('#stale #kinopoiskEnhancedPresentationPanel'), null);
    assert.equal(window.document.querySelectorAll('#active .kp-date-chip').length, 2);
    assert.match(window.document.querySelector('#active .kp-date-list').textContent, /Мировая премьера/);
    assert.match(window.document.querySelector('#active .kp-date-list').textContent, /Премьера в России/);
    assert.equal(window.document.querySelectorAll('#active .kp-standalone-release-dates').length, 1);

    await waitFor(
        () => requests.some(request => request.url.startsWith('/KinopoiskPresentation/image?url=')),
        'Постер не был запрошен через защищённый маршрут.'
    );
    assert.equal(
        requests.some(request => request.url === 'https://kinopoiskapiunofficial.tech/images/posters/kp/2.jpg'),
        false
    );

    window.JellyfinEnhanced = {};
    window.KinopoiskDetailPageLifecycle.requestRender();
    await waitFor(
        () => !window.document.querySelector('#active .kp-standalone-release-dates'),
        'Автономная верхняя строка не была передана Jellyfin Enhanced.'
    );
    assert.equal(window.document.querySelector('.mediaInfoItem-releaseDate'), null);
    assert.ok(window.document.querySelector('#active #kinopoiskEnhancedPresentationPanel'));

    const next = window.document.createElement('div');
    next.id = 'next';
    next.className = 'itemDetailPage';
    next.innerHTML = '<div class="itemMiscInfo-primary"></div><div class="detailPagePrimaryContent"></div>';
    window.document.querySelector('#active').classList.add('hide');
    window.document.body.appendChild(next);
    window.history.pushState({}, '', '/web/#/details?id=item-2');

    await waitFor(
        () => window.document.querySelector('#next #kinopoiskEnhancedPresentationPanel[data-item-id="item-2"]'),
        'Новая карточка после pushState не была отрисована.'
    );
    assert.equal(window.document.querySelector('#active #kinopoiskEnhancedPresentationPanel'), null);
    assert.equal(window.document.querySelector('#next .kp-standalone-release-dates'), null);
    runtime.dom.window.close();
});

test('рецензии повторно проверены после поздней готовности Spoiler Guard', async () => {
    const body = `
        <div id="stale" class="itemDetailPage hide"><div class="detailPagePrimaryContent"></div></div>
        <div id="active" class="itemDetailPage"><div class="detailPagePrimaryContent"></div></div>`;
    const runtime = createRuntime(
        body,
        itemId => movie(itemId, 100),
        url => url.includes('/reviews?')
            ? jsonResponse({
                total: 1,
                totalPages: 1,
                page: 1,
                items: [{
                    kinopoiskId: 1,
                    type: 'POSITIVE',
                    date: '2026-01-01',
                    author: 'Автор',
                    title: 'Рецензия',
                    description: 'Текст'
                }]
            })
            : jsonResponse({})
    );
    const { window } = runtime;
    window.JellyfinEnhanced = {};
    window.eval(readScript('kinopoiskDetailPageLifecycle.js'));
    window.eval(readScript('kinopoiskReviewsIntegration.js'));

    await waitFor(
        () => window.document.querySelector('#active .kp-review-card'),
        'Рецензия не появилась на активной карточке.'
    );
    assert.equal(window.document.querySelector('#stale .kp-review-card'), null);

    window.JellyfinEnhanced.pluginConfig = {
        SpoilerBlurEnabled: true,
        SpoilerStripReviews: true
    };
    window.JellyfinEnhanced.spoilerBlur = {
        whenLoaded: () => Promise.resolve(),
        isLoadOk: () => true,
        getUserPrefs: () => ({ HideReviews: true }),
        isMovieEnabledFor: () => true
    };
    window.KinopoiskDetailPageLifecycle.requestRender();

    await waitFor(
        () => !window.document.querySelector('#active .kp-reviews-fallback'),
        'Рецензии не были скрыты после готовности Spoiler Guard.'
    );
    runtime.dom.window.close();
});

test('ширина рекомендаций взята у штатной карточки, а постер загружен через серверный кэш', async () => {
    const body = `
        <div id="active" class="itemDetailPage">
            <div class="detailPagePrimaryContent"></div>
            <div id="similarCollapsible"><div id="native-card" class="card"></div></div>
        </div>`;
    let imageAttempts = 0;
    const runtime = createRuntime(
        body,
        itemId => movie(itemId, 100),
        url => {
            if (url.startsWith('/KinopoiskPresentation/image?')) {
                imageAttempts += 1;
                if (imageAttempts < 3) {
                    return new Response('', { status: 503 });
                }
                return imageResponse();
            }
            if (url.includes('/similars')) {
                return jsonResponse({
                    items: [{
                        kinopoiskId: 101,
                        name: 'Похожий фильм',
                        mediaType: 'movie',
                        year: 2025,
                        ratingKinopoisk: 7.3,
                        posterUrl: 'https://kinopoiskapiunofficial.tech/images/posters/kp/101.jpg'
                    }]
                });
            }
            return jsonResponse({});
        }
    );
    const { window, requests } = runtime;
    window.document.querySelector('#native-card').getBoundingClientRect = () => ({
        width: 244,
        height: 366,
        top: 0,
        left: 0,
        right: 244,
        bottom: 366
    });
    window.eval(readScript('kinopoiskDetailPageLifecycle.js'));
    window.eval(readScript('kinopoiskRecommendations.js'));

    await waitFor(
        () => window.document.querySelector('#active #kinopoiskRecommendationsSection .kp-similar-card'),
        'Карточка рекомендации не появилась.'
    );
    const section = window.document.querySelector('#kinopoiskRecommendationsSection');
    assert.equal(section.style.getPropertyValue('--kp-native-card-width'), '244.00px');
    window.document.querySelector('#native-card').getBoundingClientRect = () => ({ width: 200 });
    window.dispatchEvent(new window.Event('resize'));
    await waitFor(() => section.style.getPropertyValue('--kp-native-card-width') === '200.00px',
        'Ширина не обновилась при изменении размера окна.');
    assert.equal(section.classList.contains('detailVerticalSection'), true);
    assert.equal(section.classList.contains('verticalSection-extrabottompadding'), true);
    const nativeScroller = section.querySelector('.kp-recommendations-scroller');
    assert.equal(nativeScroller.parentElement, section);
    assert.equal(nativeScroller.getAttribute('is'), 'emby-scroller');
    assert.equal(nativeScroller.getAttribute('data-centerfocus'), 'true');
    assert.equal(nativeScroller.classList.contains('no-padding'), true);
    const nativeItems = nativeScroller.querySelector('.kp-recommendations-items');
    assert.equal(nativeItems.getAttribute('is'), 'emby-itemscontainer');
    assert.equal(nativeItems.classList.contains('scrollSlider'), true);
    assert.equal(nativeItems.classList.contains('itemsContainer'), true);
    assert.equal(section.querySelector('.kp-runtime-navigation'), null);
    assert.equal(section.querySelector('.kp-native-navigation'), null);
    const image = section.querySelector('.cardImageContainer');
    await waitFor(
        () => image.style.backgroundImage.includes('blob:kinopoisk-'),
        'Постер рекомендации не был применён после загрузки через кэш.'
    );
    assert.ok(requests.some(request => request.url.startsWith('/KinopoiskPresentation/image?url=')));
    assert.equal(imageAttempts, 3);
    assert.equal(
        requests.some(request => request.url === 'https://kinopoiskapiunofficial.tech/images/posters/kp/101.jpg'),
        false
    );
    const tabs = section.querySelectorAll('.kp-recommendations-tab');
    tabs[1].click();
    await waitFor(
        () => nativeItems.textContent.includes('Дополнительная подборка пока недоступна.'),
        'Вкладка The Movie Database не была открыта в штатном scroller.'
    );
    assert.equal(section.querySelector('.kp-recommendations-scroller'), nativeScroller);
    tabs[0].click();
    await waitFor(
        () => nativeItems.querySelector('.kp-similar-card'),
        'Вкладка КиноПоиска не была восстановлена в штатном scroller.'
    );
    assert.equal(section.querySelector('.kp-recommendations-scroller'), nativeScroller);
    runtime.dom.window.close();
});

async function createProxyRecommendation(itemOverrides = {}, options = {}) {
    let searches = 0;
    const runtime = await createRecommendationRuntime(itemOverrides, async url => {
        if (url.endsWith('/user-status')) {
            return options.status ? options.status(url) : jsonResponse({ active: true, userFound: true, jellyseerrUserId: '17' });
        }
        if (url.includes('/search?')) {
            searches += 1;
            return options.search ? options.search(url, searches) : jsonResponse({ results: [{
                id: 501, mediaType: itemOverrides.mediaType || 'movie', title: itemOverrides.name || 'Похожий фильм',
                releaseDate: String(itemOverrides.year || 2025), firstAirDate: String(itemOverrides.year || 2025)
            }] });
        }
        if (/\/movie\/1000\/(similar|recommendations)$/.test(url)) { return jsonResponse({ results: options.related || [] }); }
        if (/\/(movie|tv)\/501$/.test(url)) {
            return options.details ? options.details() : jsonResponse({
                id: 501, mediaInfo: { status: 1 }, seasons: [{ seasonNumber: 1, episodeCount: 6 }]
            });
        }
        if (url.endsWith('/quota')) { return options.quota ? options.quota() : jsonResponse({ movie: { limit: 0 }, tv: { limit: 0 } }); }
        if (url.endsWith('/link')) { return jsonResponse(options.link || { mode: 'auto', url: 'https://seerr.example/subpath' }); }
        if (url === '/JellyfinEnhanced/public-config') { return jsonResponse(options.enhancedConfig || { JellyseerrBaseUrl: 'http://legacy-seerr:5055/base' }); }
        if (url.endsWith('/overrideRule')) { return jsonResponse(options.rules || []); }
        if (url.endsWith('/request')) { return options.request ? options.request() : jsonResponse({ id: 77 }); }
        throw new Error('Unexpected proxy request: ' + url);
    });
    runtime.window.__fixtureEnhanced = options.enhanced === true;
    if (options.manualRefresh) {
        runtime.window.HTMLElement.prototype.getBoundingClientRect = () => ({ width: 100, height: 100, top: 0, bottom: 100, left: 0, right: 100 });
        const originalTimeout = runtime.window.setTimeout.bind(runtime.window);
        const ticks = [];
        runtime.window.setTimeout = (callback, delay, ...args) => {
            if ([30000, 60000, 120000].includes(delay)) {
                ticks.push(callback);
                return originalTimeout(() => {}, 1000000);
            }
            return originalTimeout(callback, delay, ...args);
        };
        runtime.nextRefresh = () => { assert.ok(ticks.length, 'Обновление не запланировано.'); ticks.shift()(); };
    }
    runtime.window.eval(readScript('kinopoiskRecommendationSeerrFallback.js'));
    return runtime;
}

function requestPosts(runtime) {
    return runtime.requests.filter(request => request.options?.method === 'POST');
}

async function openRecommendation(runtime, selector = '.cardImageContainer') {
    runtime.window.document.querySelector('.kp-similar-card ' + selector).click();
    await waitFor(() => runtime.window.document.querySelector('.kp-recommendation-dialog'), 'Сведения не открылись.');
    return runtime.window.document.querySelector('.kp-recommendation-dialog');
}

for (const mediaType of ['movie', 'tv']) {
    test('реальные обработчики карточки обращаются к серверному прокси без модулей Enhanced: ' + mediaType, async () => {
        const runtime = await createProxyRecommendation({ mediaType });
        try {
            const { window } = runtime;
            await waitFor(() => window.document.querySelector('[data-kp-seerr-fallback-state="ready"]'), 'Seerr без клиентского Enhanced не подключился.');
            assert.equal(window.JellyfinEnhanced, undefined);
            const card = window.document.querySelector('.kp-similar-card');
            assert.equal(card.dataset.tmdbId, '501');
            assert.equal(card.querySelector('.kp-similar-card__request').textContent, 'Запросить');
            assert.equal(requestPosts(runtime).length, 0);
            const dialog = await openRecommendation(runtime, '.kp-similar-card__request');
            await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Подтверждение запроса не подготовилось.');
            assert.match(dialog.textContent, /Описание похожего фильма/);
            assert.equal(requestPosts(runtime).length, 0, 'Открытие карточки отправило запрос без подтверждения.');
            if (mediaType === 'tv') { dialog.querySelector('input').checked = true; }
            const button = dialog.querySelector('.kp-recommendation-dialog__submit');
            button.click();
            button.click();
            await waitFor(() => dialog.textContent.includes('Запрос отправлен'), 'Запрос не был подтверждён.');
            const posts = requestPosts(runtime);
            assert.equal(posts.length, 1, 'Двойной клик создал повторный POST.');
            assert.deepEqual(JSON.parse(posts[0].options.body), {
                mediaType, mediaId: 501, ...(mediaType === 'tv' ? { seasons: [1] } : {})
            });
            for (const request of runtime.requests.filter(r => r.url.startsWith('/KinopoiskRequests/') || r.url.startsWith('/JellyfinEnhanced/'))) {
                assert.equal(request.options.headers['X-Emby-Token'], 'test-token');
                assert.equal(request.options.credentials, 'same-origin');
                assert.equal(request.options.headers['X-API-KEY'], undefined);
            }
            assert.equal(runtime.requests.filter(r => /\/100\/similars$/.test(r.url)).length, 1);
        } finally { runtime.dom.window.close(); }
    });
}

test('сломанные клиентские модули Enhanced не блокируют серверный запрос', async () => {
    const runtime = await createProxyRecommendation();
    try {
        const unavailable = () => { throw new Error('Enhanced client module failed to load'); };
        runtime.window.JellyfinEnhanced = {
            jellyseerrAPI: { checkUserStatus: unavailable, search: unavailable, fetchMovieDetails: unavailable, requestMedia: unavailable },
            jellyseerrUI: { createJellyseerrCard: unavailable }
        };
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Подтверждение недоступно.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => requestPosts(runtime).length === 1, 'Прокси не использован.');
        assert.deepEqual(JSON.parse(requestPosts(runtime)[0].options.body), { mediaId: 501, mediaType: 'movie' });
    } finally { runtime.dom.window.close(); }
});

test('ошибка расширенного окна Enhanced возвращает подтверждение без потери обычного запроса', async () => {
    const runtime = await createProxyRecommendation({}, { enhanced: true, status: url => jsonResponse(url.startsWith('/KinopoiskRequests/')
        ? { provider: 'enhanced' } : { active: true, userFound: true, jellyseerrUserId: '17' }) });
    try {
        let advancedCalls = 0;
        runtime.window.JellyfinEnhanced = {
            jellyseerrAPI: { shouldShowAdvanced: () => true },
            jellyseerrUI: { showMovieRequestModal() { advancedCalls += 1; throw new Error('Missing Enhanced dependency'); } }
        };
        const original = await openRecommendation(runtime);
        await waitFor(() => !original.querySelector('.kp-recommendation-dialog__submit').disabled, 'Подтверждение не готово.');
        original.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => {
            const current = runtime.window.document.querySelector('.kp-recommendation-dialog');
            return current && current !== original && !current.querySelector('.kp-recommendation-dialog__submit').disabled;
        }, 'Подтверждение после ошибки Enhanced не восстановилось.');
        assert.equal(requestPosts(runtime).length, 0);
        runtime.window.document.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => requestPosts(runtime).length === 1, 'Обычный запрос не отправлен.');
        assert.equal(advancedCalls, 1);
    } finally { runtime.dom.window.close(); }
});

for (const [status, caption] of [[2, 'Запрошено'], [3, 'Загружается'], [5, 'Доступно']]) {
    test('уже обработанный фильм нельзя запросить повторно: ' + caption, async () => {
        const runtime = await createProxyRecommendation({}, { details: () => jsonResponse({ id: 501, mediaInfo: { status } }) });
        try {
            await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="ready"]'), 'Состояние не получено.');
            assert.equal(runtime.window.document.querySelector('.kp-similar-card__request').textContent, caption);
            const dialog = await openRecommendation(runtime);
            await waitFor(() => dialog.querySelector('[role="status"]').textContent === caption, 'Состояние не перенесено в сведения.');
            assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
            assert.equal(requestPosts(runtime).length, 0);
        } finally { runtime.dom.window.close(); }
    });
}

test('связанный библиотечный фильм открывается в Jellyfin', async () => {
    const runtime = await createProxyRecommendation({}, { details: () => jsonResponse({
        id: 501, mediaInfo: { status: 5, jellyfinMediaId: 'library-item' }
    }) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__submit').textContent === 'Открыть в Jellyfin', 'Ссылка на медиатеку не получена.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        assert.equal(runtime.window.location.hash, '#/details?id=library-item');
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});

test('частично доступный сериал разрешает выбрать только отсутствующий сезон', async () => {
    const runtime = await createProxyRecommendation({ mediaType: 'tv' }, { details: () => jsonResponse({
        id: 501, mediaInfo: { status: 4, jellyfinMediaId: 'library-item', seasons: [{ seasonNumber: 1, status: 5 }] },
        seasons: [{ seasonNumber: 1, episodeCount: 6 }, { seasonNumber: 2, episodeCount: 8 }]
    }) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.querySelectorAll('input').length === 2, 'Сезоны не получены.');
        const inputs = dialog.querySelectorAll('input');
        assert.equal(inputs[0].disabled, true);
        assert.equal(inputs[1].disabled, false);
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        assert.equal(requestPosts(runtime).length, 0);
        inputs[1].checked = true;
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => requestPosts(runtime).length === 1, 'Выбранный сезон не отправлен.');
        assert.deepEqual(JSON.parse(requestPosts(runtime)[0].options.body).seasons, [2]);
    } finally { runtime.dom.window.close(); }
});

for (const reason of ['unlinked', 'blocked']) {
    test('недоступность интеграции показана явно: ' + reason, async () => {
        const runtime = await createProxyRecommendation({}, { status: () => jsonResponse({ active: false, userFound: false, reason }) });
        try {
            await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="retry"]'), 'Ошибка не показана.');
            assert.equal(runtime.window.document.querySelector('.kp-similar-card__request').textContent, 'Seerr недоступен');
            const dialog = await openRecommendation(runtime);
            await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__submit').textContent === 'Запрос недоступен', 'Объяснение не показано.');
            assert.ok(dialog.querySelector('[role="status"]').textContent.length > 20);
            assert.equal(runtime.window.location.hash, '#/details?id=item-1');
            assert.equal(requestPosts(runtime).length, 0);
        } finally { runtime.dom.window.close(); }
    });
}

test('временная ошибка поиска восстановилась без модулей Enhanced', async () => {
    const runtime = await createProxyRecommendation({}, { search: (_url, attempt) => attempt === 1
        ? new Response('{}', { status: 503 })
        : jsonResponse({ results: [{ id: 501, mediaType: 'movie', title: 'Похожий фильм', releaseDate: '2025' }] }) });
    try {
        await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="ready"]'), 'Повторное сопоставление не выполнено.', 7000);
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});

test('неоднозначные названия и разные годы не создают запрос другого фильма', async () => {
    const runtime = await createProxyRecommendation({ name: 'Дюна', year: 2000 }, { search: () => jsonResponse({ results: [
        { id: 1, mediaType: 'movie', title: 'Дюна', releaseDate: '1984' },
        { id: 2, mediaType: 'movie', title: 'Дюна', releaseDate: '2000' },
        { id: 3, mediaType: 'movie', title: 'Дюна', releaseDate: '2000' },
        { id: 4, mediaType: 'tv', name: 'Дюна', firstAirDate: '2000' }
    ] }) });
    try {
        await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="unmatched"]'), 'Неоднозначный результат не отклонён.');
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.textContent.includes('Однозначное совпадение'), 'Причина не показана.');
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});

for (const status of [403, 409, 429, 503]) {
    test('отказ запроса показан без автоматического повторного POST: ' + status, async () => {
        const runtime = await createProxyRecommendation({}, { request: () => new Response(JSON.stringify({ message: 'Запрос отклонён' }), { status }) });
        try {
            const dialog = await openRecommendation(runtime);
            await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Запрос недоступен.');
            dialog.querySelector('.kp-recommendation-dialog__submit').click();
            await waitFor(() => dialog.textContent.includes('Проверьте состояние'), 'Ошибка не показана.');
            assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
            assert.equal(requestPosts(runtime).length, 1);
        } finally { runtime.dom.window.close(); }
    });
}

test('правило Seerr применено только к подходящему фильму и пользователю', async () => {
    const runtime = await createProxyRecommendation({}, { enhanced: true, status: url => jsonResponse(url.startsWith('/KinopoiskRequests/') ? { provider: 'enhanced' } : { active: true, userFound: true, jellyseerrUserId: '17' }), rules: [
        { id: 1, radarrServiceId: 9, users: '99', profileId: 1 },
        { id: 2, radarrServiceId: 2, users: '17', profileId: 3, rootFolder: '/movies', tags: '4|5' }
    ] });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Запрос недоступен.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => requestPosts(runtime).length === 1, 'Запрос не отправлен.');
        assert.deepEqual(JSON.parse(requestPosts(runtime)[0].options.body), {
            mediaId: 501, mediaType: 'movie', serverId: 2, profileId: 3, rootFolder: '/movies', tags: [4, 5]
        });
    } finally { runtime.dom.window.close(); }
});

test('смена пользователя закрывает подтверждение и не отправляет запрос', async () => {
    const runtime = await createProxyRecommendation();
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Подтверждение не готово.');
        runtime.window.ApiClient.getCurrentUserId = () => 'user-2';
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        assert.equal(requestPosts(runtime).length, 0);
        runtime.window.document.dispatchEvent(new runtime.window.Event('kinopoisk:recommendations-rendered'));
        await waitFor(() => !runtime.window.document.querySelector('.kp-recommendation-dialog'), 'Окно прежнего пользователя не закрыто.');
    } finally { runtime.dom.window.close(); }
});


test('без сервиса запросов рекомендации, описание и библиотека работают автономно', async () => {
    const runtime = await createRecommendationRuntime({}, url => {
        assert.equal(url, '/KinopoiskRequests/user-status');
        return jsonResponse({ provider: 'disabled', active: false, userFound: false, reason: 'disabled' });
    });
    const originalFetch = runtime.window.fetch;
    runtime.window.fetch = (url, options) => String(url).startsWith('/KinopoiskRecommendations/library/')
        ? Promise.resolve(jsonResponse({ itemId: 'abcdef123456' })) : originalFetch(url, options);
    runtime.window.eval(readScript('kinopoiskRecommendationSeerrFallback.js'));
    try {
        await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="standalone"]'), 'Автономный режим не выбран.');
        assert.equal(runtime.window.document.querySelector('.kp-similar-card__request').textContent, 'Подробнее');
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.querySelector('.kp-recommendation-library'), 'Фильм из библиотеки не найден.');
        assert.match(dialog.textContent, /Описание похожего фильма/);
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').hidden, true);
        assert.equal(dialog.querySelector('.kp-recommendation-library').getAttribute('href'), '#/details?id=abcdef123456');
        assert.equal(runtime.requests.some(r => r.url.includes('/JellyfinEnhanced/')), false);
        assert.equal(runtime.requests.some(r => r.url.includes('/search?')), false);
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});

test('автоматическое подключение без Enhanced оставляет самостоятельную карточку', async () => {
    const runtime = await createRecommendationRuntime({}, url => url.startsWith('/KinopoiskRequests/')
        ? jsonResponse({ provider: 'enhanced', active: false, userFound: false })
        : jsonResponse({}, 404));
    runtime.window.eval(readScript('kinopoiskRecommendationSeerrFallback.js'));
    try {
        await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="standalone"]'), 'Отсутствующий Enhanced заблокировал карточку.');
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.textContent.includes('Для запросов подключите Seerr'), 'Не объяснено отсутствие сервиса запросов.');
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').hidden, true);
        assert.equal(requestPosts(runtime).length, 0);
        dialog.querySelector('.kp-recommendation-dialog__actions button:not(.kp-recommendation-dialog__submit)').click();
        assert.equal(dialog.isConnected, false);
    } finally { runtime.dom.window.close(); }
});

test('автоматически обнаруженный Enhanced обслуживает запрос без клиентских модулей', async () => {
    const runtime = await createProxyRecommendation({}, {
        enhanced: true,
        status: url => jsonResponse(url.startsWith('/KinopoiskRequests/')
            ? { provider: 'enhanced', active: false, userFound: false }
            : { active: true, userFound: true, jellyseerrUserId: '17' })
    });
    try {
        await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="ready"]'), 'Enhanced не обнаружен.');
        assert.equal(runtime.window.JellyfinEnhanced, undefined);
        const dialog = await openRecommendation(runtime);
        const submit = dialog.querySelector('.kp-recommendation-dialog__submit');
        await waitFor(() => !submit.disabled, 'Запрос через Enhanced не подготовлен.');
        assert.equal(submit.textContent, 'Запросить');
        submit.click();
        await waitFor(() => dialog.textContent.includes('Запрос отправлен'), 'Заявка не отправлена через Enhanced.');
        assert.equal(requestPosts(runtime)[0].url, '/JellyfinEnhanced/jellyseerr/request');
        for (const request of runtime.requests.filter(r => r.url.startsWith('/JellyfinEnhanced/jellyseerr'))) {
            assert.equal(request.options.headers['X-Jellyfin-User-Id'], 'user-1');
            assert.equal(request.options.headers.Authorization, 'MediaBrowser Token="test-token"');
        }
    } finally { runtime.dom.window.close(); }
});

test('подключение Seerr после автономного режима применяется при повторном открытии', async () => {
    let connected = false;
    const runtime = await createProxyRecommendation({}, {
        status: () => jsonResponse(connected
            ? { provider: 'seerr', active: true, userFound: true }
            : { provider: 'disabled', active: false, userFound: false, reason: 'disabled' })
    });
    try {
        const first = await openRecommendation(runtime);
        await waitFor(() => first.querySelector('.kp-recommendation-dialog__status').textContent === '', 'Автономный режим не готов.');
        first.querySelector('.kp-recommendation-dialog__actions button').click();
        connected = true;
        const second = await openRecommendation(runtime);
        await waitFor(() => !second.querySelector('.kp-recommendation-dialog__submit').disabled, 'Сохранённое подключение не применено.');
        assert.equal(second.querySelector('.kp-recommendation-dialog__submit').textContent, 'Запросить');
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});

for (const keyboard of [false, true]) {
    test('закрытие подробностей восстанавливает фокус только для клавиатуры: ' + keyboard, async () => {
        const runtime = await createProxyRecommendation();
        try {
            const opener = runtime.window.document.querySelector('.kp-similar-card__request');
            opener.focus();
            opener.dispatchEvent(new runtime.window.MouseEvent('click', { bubbles: true, detail: keyboard ? 0 : 1 }));
            const dialog = runtime.window.document.querySelector('.kp-recommendation-dialog');
            await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Окно не готово.');
            dialog.querySelector('.kp-recommendation-dialog__actions button').click();
            assert.equal(runtime.window.document.activeElement === opener, keyboard);
        } finally { runtime.dom.window.close(); }
    });
}

test('Seerr без права запроса сохраняет описание и блокирует отправку', async () => {
    const runtime = await createProxyRecommendation({}, { status: () => jsonResponse({ provider: 'seerr', active: true, userFound: true, canRequestMovie: false }) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.textContent.includes('Нет права запроса'), 'Права Seerr не учтены.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        assert.equal(requestPosts(runtime).length, 0);
        assert.equal(runtime.requests.some(r => r.url.includes('/JellyfinEnhanced/')), false);
    } finally { runtime.dom.window.close(); }
});


test('дополнительная подборка использует самостоятельные карточки и запросы без Enhanced', async () => {
    const runtime = await createProxyRecommendation({}, { related: [{
        id: 501, mediaType: 'movie', title: 'Фильм из TMDB', releaseDate: '2025-01-01',
        overview: 'Описание из дополнительной подборки.', posterPath: '/poster.jpg'
    }] });
    try {
        runtime.window.KinopoiskTmdbResolver = { resolve: () => Promise.resolve('1000') };
        runtime.window.document.querySelectorAll('.kp-recommendations-tab')[1].click();
        await waitFor(() => runtime.window.document.querySelector('.kp-similar-card')?.__kinopoiskRecommendationItem.tmdbId === 501, 'Самостоятельная карточка TMDB не появилась.');
        assert.equal(runtime.window.document.querySelectorAll('.kp-similar-card').length, 1);
        assert.equal(runtime.window.JellyfinEnhanced, undefined);
        assert.ok(runtime.window.document.querySelector('.kp-similar-card .cardOverlayContainer'));
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Запрос TMDB недоступен.');
        assert.match(dialog.textContent, /Описание из дополнительной подборки/);
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => requestPosts(runtime).length === 1, 'Запрос не отправлен.');
        assert.deepEqual(JSON.parse(requestPosts(runtime)[0].options.body), { mediaType: 'movie', mediaId: 501 });
    } finally { runtime.dom.window.close(); }
});


for (const [requestStatus, caption, blocked] of [[1, 'Ожидает одобрения', true], [2, 'Одобрено', true],
    [3, 'Отклонено', false], [4, 'Ошибка заявки', true], [5, 'Заявка завершена', false]]) {
    test('статус заявки показан отдельно от доступности: ' + caption, async () => {
        const runtime = await createProxyRecommendation({}, { details: () => jsonResponse({
            id: 501, mediaInfo: { status: 1, requests: [{ id: 12, status: requestStatus, is4k: false }] }
        }) });
        try {
            const dialog = await openRecommendation(runtime);
            await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__quota').textContent.includes('без ограничений'), 'Квота не получена.');
            assert.equal(runtime.window.document.querySelector('.kp-similar-card__request').textContent, caption);
            assert.match(dialog.querySelector('[role="status"]').textContent, new RegExp(caption));
            assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, blocked);
            assert.equal(requestPosts(runtime).length, 0);
        } finally { runtime.dom.window.close(); }
    });
}

test('квота фильма блокирует отправку при исчерпанном лимите', async () => {
    const runtime = await createProxyRecommendation({}, { quota: () => jsonResponse({ movie: { limit: 2, days: 7, remaining: 0, restricted: true } }) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__quota').textContent.includes('осталось 0'), 'Квота не показана.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        assert.equal(requestPosts(runtime).length, 0);
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
    } finally { runtime.dom.window.close(); }
});

test('квота сериала учитывает число выбранных сезонов', async () => {
    const runtime = await createProxyRecommendation({ mediaType: 'tv' }, {
        quota: () => jsonResponse({ tv: { limit: 3, days: 7, used: 2, remaining: 1 } }),
        details: () => jsonResponse({ id: 501, mediaInfo: { status: 1 }, seasons: [1, 2].map(seasonNumber => ({ seasonNumber, episodeCount: 6 })) })
    });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__quota').textContent.includes('осталось 1'), 'Квота не получена.');
        const inputs = dialog.querySelectorAll('input');
        inputs.forEach(input => { input.checked = true; });
        inputs[0].dispatchEvent(new runtime.window.Event('change', { bubbles: true }));
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
        assert.match(dialog.querySelector('.kp-recommendation-dialog__quota').textContent, /Выбрано: 2/);
        inputs[1].checked = false;
        inputs[1].dispatchEvent(new runtime.window.Event('change', { bubbles: true }));
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, false);
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => requestPosts(runtime).length === 1, 'Разрешённый сезон не запрошен.');
        assert.deepEqual(JSON.parse(requestPosts(runtime)[0].options.body).seasons, [1]);
    } finally { runtime.dom.window.close(); }
});

test('квота повторно проверяется перед отправкой и не создаёт лишнюю заявку', async () => {
    let reads = 0;
    const runtime = await createProxyRecommendation({}, { quota: () => jsonResponse({ movie: { limit: 1, remaining: reads++ ? 0 : 1 } }) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Кнопка не готова.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => dialog.querySelector('[role="status"]').textContent.includes('Лимит'), 'Изменившаяся квота не учтена.');
        assert.equal(requestPosts(runtime).length, 0);
        assert.equal(reads, 2);
    } finally { runtime.dom.window.close(); }
});

test('недоступная квота обозначена явно и оставлена на проверку Seerr', async () => {
    const runtime = await createProxyRecommendation({}, { quota: () => jsonResponse({}, 404) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__quota').textContent.includes('Лимит проверит Seerr'), 'Ошибка квоты скрыта.');
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, false);
        assert.doesNotMatch(dialog.textContent, /без ограничений/);
    } finally { runtime.dom.window.close(); }
});

for (const url of ['https://seerr.example/subpath', 'http://192.168.1.1:5055', 'javascript:alert(1)', 'https://u:secret@seerr.example', 'https://seerr.example/?token=secret']) {
    test('кнопка Seerr использует только безопасный адрес: ' + url.split('?')[0], async () => {
        const runtime = await createProxyRecommendation({}, { link: { mode: 'custom', url } });
        try {
            const dialog = await openRecommendation(runtime);
            await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__quota').textContent.includes('без ограничений'), 'Действия не подготовлены.');
            const link = dialog.querySelector('.kp-recommendation-seerr-link');
            const safe = /^https?:/.test(url) && !url.includes('@') && !url.includes('?');
            assert.equal(link.hidden, !safe);
            if (safe) { assert.equal(link.href, url + '/movie/501'); assert.equal(link.rel, 'noopener noreferrer'); }
            assert.equal(requestPosts(runtime).length, 0);
        } finally { runtime.dom.window.close(); }
    });
}

test('Enhanced предоставляет адрес ссылки и квоту без клиентских модулей', async () => {
    const runtime = await createProxyRecommendation({ mediaType: 'tv' }, {
        enhanced: true,
        status: url => jsonResponse(url.startsWith('/KinopoiskRequests/') ? { provider: 'enhanced' }
            : { active: true, userFound: true, jellyseerrUserId: '17' }), link: { mode: 'auto', url: '' }
    });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-seerr-link').hidden, 'Ссылка Enhanced не найдена.');
        assert.equal(dialog.querySelector('.kp-recommendation-seerr-link').href, 'http://legacy-seerr:5055/base/tv/501');
        assert.ok(runtime.requests.some(r => r.url === '/JellyfinEnhanced/jellyseerr/quota'));
        assert.equal(runtime.window.JellyfinEnhanced, undefined);
    } finally { runtime.dom.window.close(); }
});

test('живое обновление сохраняет выбранный сезон и фокус открытого окна', async () => {
    let state = 1;
    const runtime = await createProxyRecommendation({ mediaType: 'tv' }, { manualRefresh: true, details: () => jsonResponse({
        id: 501, mediaInfo: { status: state }, seasons: [1, 2].map(seasonNumber => ({ seasonNumber, episodeCount: 6 }))
    }) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Окно не готово.');
        const input = dialog.querySelector('input[value="2"]');
        input.checked = true;
        input.focus();
        const searches = runtime.requests.filter(r => r.url.includes('/search?')).length;
        state = 4;
        runtime.nextRefresh();
        await waitFor(() => runtime.window.document.querySelector('.kp-similar-card__request').textContent === 'Частично доступно', 'Состояние карточки не обновлено.');
        assert.equal(dialog.isConnected, true);
        assert.equal(input.checked, true);
        assert.equal(runtime.window.document.activeElement, input);
        assert.equal(runtime.requests.filter(r => r.url.includes('/search?')).length, searches, 'Обновление повторило поиск вместо чтения известного TMDB ID.');
        state = 5;
        runtime.nextRefresh();
        await waitFor(() => dialog.querySelector('[role="status"]').textContent === 'Доступно', 'Открытое окно не обновлено.');
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});

test('скрытая страница не запрашивает живые статусы', async () => {
    const runtime = await createProxyRecommendation({}, { manualRefresh: true });
    try {
        await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="ready"]'), 'Карточка не готова.');
        const reads = runtime.requests.length;
        Object.defineProperty(runtime.window.document, 'hidden', { configurable: true, value: true });
        runtime.nextRefresh();
        await new Promise(resolve => setTimeout(resolve, 60));
        assert.equal(runtime.requests.length, reads);
    } finally { runtime.dom.window.close(); }
});


test('автоматическое одобрение из ответа Seerr сразу отражено в карточке', async () => {
    const runtime = await createProxyRecommendation({}, { request: () => jsonResponse({ id: 77, status: 2, media: { status: 2 } }) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Кнопка не готова.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => dialog.textContent.includes('Запрос отправлен'), 'Ответ Seerr не получен.');
        assert.equal(runtime.window.document.querySelector('.kp-similar-card__request').textContent, 'Одобрено');
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
        assert.equal(requestPosts(runtime).length, 1);
    } finally { runtime.dom.window.close(); }
});


for (const [mode, expected] of [['auto', 'https://public.example/seerr'], ['local', 'http://legacy-seerr:5055']]) {
    test('Enhanced сохранил выбор локального адреса и сопоставление адреса Jellyfin: ' + mode, async () => {
        const runtime = await createProxyRecommendation({}, {
            enhanced: true,
            status: url => jsonResponse(url.startsWith('/KinopoiskRequests/') ? { provider: 'enhanced' }
                : { active: true, userFound: true, jellyseerrUserId: '17' }),
            link: { mode, url: '' }, enhancedConfig: { JellyseerrBaseUrl: 'http://legacy-seerr:5055',
                JellyseerrUrlMappings: 'https://another.example|https://other.example\nhttps://jellyfin.test/|https://public.example/seerr/' }
        });
        try {
            const dialog = await openRecommendation(runtime);
            await waitFor(() => !dialog.querySelector('.kp-recommendation-seerr-link').hidden, 'Ссылка не получена.');
            assert.equal(dialog.querySelector('.kp-recommendation-seerr-link').href, expected + '/movie/501');
        } finally { runtime.dom.window.close(); }
    });
}


test('запоздавшее живое состояние прежнего пользователя не меняет новую карточку', async () => {
    let defer = false;
    let complete;
    const runtime = await createProxyRecommendation({}, { manualRefresh: true, details: () => {
        if (defer) { defer = false; return new Promise(resolve => { complete = resolve; }); }
        return jsonResponse({ id: 501, mediaInfo: { status: 1 } });
    } });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Окно не готово.');
        defer = true;
        runtime.nextRefresh();
        await waitFor(() => complete, 'Обновление не началось.');
        runtime.window.ApiClient.getCurrentUserId = () => 'user-2';
        runtime.window.document.dispatchEvent(new runtime.window.Event('kinopoisk:recommendations-rendered'));
        await waitFor(() => !dialog.isConnected, 'Окно прежнего пользователя не закрыто.');
        complete(jsonResponse({ id: 501, mediaInfo: { status: 5 } }));
        await new Promise(resolve => setTimeout(resolve, 150));
        assert.equal(runtime.window.document.querySelector('.kp-similar-card__request').textContent, 'Запросить');
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});


test('ответ Seerr 202 без заявки не показан как успешное создание', async () => {
    const runtime = await createProxyRecommendation({}, { request: () => jsonResponse({ message: 'No seasons available to request' }, 202) });
    try {
        const dialog = await openRecommendation(runtime);
        await waitFor(() => !dialog.querySelector('.kp-recommendation-dialog__submit').disabled, 'Окно не готово.');
        dialog.querySelector('.kp-recommendation-dialog__submit').click();
        await waitFor(() => dialog.textContent.includes('не подтвердил создание'), 'Отсутствующая заявка показана как успешная.');
        assert.doesNotMatch(dialog.textContent, /Запрос отправлен/);
        assert.equal(requestPosts(runtime).length, 1);
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').disabled, true);
    } finally { runtime.dom.window.close(); }
});


test('живые статусы возобновлены после возвращения к карточкам прокруткой', async () => {
    let state = 1;
    const runtime = await createProxyRecommendation({}, { manualRefresh: true, details: () => jsonResponse({ id: 501, mediaInfo: { status: state } }) });
    try {
        await waitFor(() => runtime.window.document.querySelector('[data-kp-seerr-fallback-state="ready"]'), 'Карточка не готова.');
        const card = runtime.window.document.querySelector('.kp-similar-card');
        card.getBoundingClientRect = () => ({ width: 100, top: 2000, bottom: 2100, left: 0, right: 100 });
        const reads = runtime.requests.length;
        runtime.nextRefresh();
        await new Promise(resolve => setTimeout(resolve, 60));
        assert.equal(runtime.requests.length, reads, 'Скрытая прокруткой карточка обратилась к Seerr.');
        state = 5;
        card.getBoundingClientRect = () => ({ width: 100, top: 0, bottom: 100, left: 0, right: 100 });
        runtime.window.document.dispatchEvent(new runtime.window.Event('scroll'));
        await waitFor(() => card.querySelector('.kp-similar-card__request').textContent === 'Доступно', 'Обновление после прокрутки не возобновлено.');
        assert.equal(requestPosts(runtime).length, 0);
    } finally { runtime.dom.window.close(); }
});
