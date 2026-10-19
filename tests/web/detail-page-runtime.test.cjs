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
        return responseFactory(String(url));
    };
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
        if (url.endsWith('/overrideRule')) { return jsonResponse(options.rules || []); }
        if (url.endsWith('/request')) { return options.request ? options.request() : jsonResponse({ id: 77 }); }
        throw new Error('Unexpected proxy request: ' + url);
    });
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
            assert.equal(card.querySelector('.kp-similar-card__request').textContent, 'Запрос');
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
    const runtime = await createProxyRecommendation({}, { status: url => jsonResponse(url.startsWith('/KinopoiskRequests/')
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
    const runtime = await createProxyRecommendation({}, { status: url => jsonResponse(url.startsWith('/KinopoiskRequests/') ? { provider: 'enhanced' } : { active: true, userFound: true, jellyseerrUserId: '17' }), rules: [
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
        await waitFor(() => dialog.querySelector('.kp-recommendation-dialog__status').textContent === '', 'Ложная ошибка подключения.');
        assert.equal(dialog.querySelector('.kp-recommendation-dialog__submit').hidden, true);
        assert.equal(requestPosts(runtime).length, 0);
        dialog.querySelector('.kp-recommendation-dialog__actions button:not(.kp-recommendation-dialog__submit)').click();
        assert.equal(dialog.isConnected, false);
    } finally { runtime.dom.window.close(); }
});

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
