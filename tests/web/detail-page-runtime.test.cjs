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

function jsonResponse(value) {
    return new Response(JSON.stringify(value), {
        status: 200,
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

async function createRecommendationRuntime(itemOverrides = {}) {
    const runtime = createRuntime(
        '<div id="active" class="itemDetailPage"><div class="detailPagePrimaryContent"></div>'
            + '<div id="similarCollapsible"><div id="native-card" class="card"></div></div></div>',
        itemId => movie(itemId, 100),
        url => url.startsWith('/KinopoiskPresentation/image?')
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

test('постер рекомендации открывал тот же фильм, что и название, без Seerr', async () => {
    const runtime = await createRecommendationRuntime();
    const { window } = runtime;
    try {
        const card = window.document.querySelector('.kp-similar-card');
        const poster = card.querySelector('.cardImageContainer');
        const title = card.querySelector('.cardText-first a');
        assert.equal(poster.tagName, 'A', 'Постер не был доступен для перехода.');
        assert.equal(poster.href, title.href);
        assert.equal(poster.href, 'https://www.kinopoisk.ru/film/101/');
        assert.equal(poster.target, '_blank');
        assert.match(poster.rel, /noopener/);
        assert.match(poster.getAttribute('aria-label'), /Похожий фильм/);
        poster.focus();
        assert.equal(window.document.activeElement, poster);

        let destination = null;
        poster.addEventListener('click', event => {
            destination = event.currentTarget.href;
            event.preventDefault();
        });
        card.querySelector('.cardScalable').dispatchEvent(new window.MouseEvent('mouseenter'));
        const description = poster.querySelector('.kp-similar-card__overview');
        assert.ok(description, 'Описание не было частью ссылки постера.');
        description.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
        assert.equal(destination, title.href, 'Описание перекрыло переход по постеру.');
        assert.equal(card.querySelector('button:disabled'), null);
    } finally {
        runtime.dom.window.close();
    }
});

test('карточка рекомендации сохранила оформление без стилей Jellyfin Enhanced', async () => {
    const runtime = await createRecommendationRuntime();
    const { window } = runtime;
    try {
        const card = window.document.querySelector('.kp-similar-card');
        const meta = card.querySelector('.jellyseerr-meta');
        const poster = card.querySelector('.cardImageContainer');
        assert.equal(window.getComputedStyle(meta).display, 'flex');
        assert.notEqual(window.getComputedStyle(meta).gap, '0px');
        assert.equal(window.getComputedStyle(poster).position, 'absolute');
        assert.equal(window.getComputedStyle(card.querySelector('.kp-similar-card__badge')).position, 'absolute');
        const overviewStyle = window.getComputedStyle(card.querySelector('.kp-similar-card__overview'));
        assert.equal(overviewStyle.position, 'absolute');
        assert.equal(overviewStyle.pointerEvents, 'none');
        assert.equal(window.getComputedStyle(card.querySelector('.cardText-first a')).textDecorationLine, 'none');
    } finally {
        runtime.dom.window.close();
    }
});

test('переход по рекомендации сохранился при отсутствии постера и описания', async () => {
    const runtime = await createRecommendationRuntime({ posterUrl: null, overview: null, mediaType: 'tv' });
    try {
        const poster = runtime.window.document.querySelector('.kp-similar-card .cardImageContainer');
        assert.equal(poster.tagName, 'A');
        assert.equal(poster.href, 'https://www.kinopoisk.ru/film/101/');
        assert.match(poster.textContent, /Краткое описание отсутствует/);
        assert.match(poster.textContent, /СЕРИАЛ/);
    } finally {
        runtime.dom.window.close();
    }
});

test('резервная карточка заменена карточкой Seerr после поздней готовности интеграции', async () => {
    const runtime = await createRecommendationRuntime({ imdbId: 'tt1234567' });
    const { window } = runtime;
    try {
        let clicks = 0;
        let cardOptions = null;
        window.KinopoiskTmdbResolver = { resolve: () => Promise.resolve('501') };
        window.JellyfinEnhanced = {
            jellyseerrAPI: {
                checkUserStatus: () => Promise.resolve({ active: true, userFound: true }),
                fetchMovieDetails: () => Promise.resolve({ id: 501 })
            },
            jellyseerrUI: {
                createJellyseerrCard(item, active, userFound) {
                    cardOptions = { item, active, userFound };
                    const card = window.document.createElement('div');
                    card.className = 'card jellyseerr-card enhanced-test-card';
                    card.innerHTML = '<a class="cardImageContainer" href="#/seerr/501"></a>'
                        + '<div class="jellyseerr-meta"></div>';
                    card.querySelector('a').addEventListener('click', event => {
                        event.preventDefault();
                        clicks += 1;
                    });
                    return card;
                }
            }
        };
        window.eval(readScript('kinopoiskRecommendationSeerrFallback.js'));
        await waitFor(() => window.document.querySelector('.enhanced-test-card'),
            'Карточка Seerr не заменила резервную карточку.');
        const card = window.document.querySelector('.enhanced-test-card');
        assert.equal(window.document.querySelector('.kp-similar-card--fallback'), null);
        assert.equal(cardOptions.item.id, 501);
        assert.equal(cardOptions.active, true);
        assert.equal(cardOptions.userFound, true);
        card.querySelector('a').click();
        assert.equal(clicks, 1, 'Обработчик карточки Seerr был утрачен при замене.');
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
        () => nativeItems.textContent.includes('Интеграция Jellyfin Enhanced недоступна.'),
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
