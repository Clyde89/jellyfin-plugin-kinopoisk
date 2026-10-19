// Runs on the self-hosted runner. HTTP fixtures never contact the user's Seerr.
// Jellyfin styles are fetched at an exact upstream commit by the workflow.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');
const sass = require('sass');

const root = path.resolve(__dirname, '../..');
const web = path.join(root, 'src/Jellyfin.Plugin.Kinopoisk/Web');
const manifest = fs.readFileSync(path.join(root, 'src/Jellyfin.Plugin.Kinopoisk/Services/KinopoiskWebClientBundle.cs'), 'utf8');
const scripts = [...manifest.matchAll(/"Jellyfin[.]Plugin[.]Kinopoisk[.]Web[.]([^"\s]+[.]js)"/g)].map(match => match[1]);
assert.equal(scripts.length, 12);
const bundle = scripts.map(name => fs.readFileSync(path.join(web, name), 'utf8')).join('\n\n');
const css = ['card.scss', 'emby-button.scss', 'dialoghelper.scss'].map(name => sass.compile(path.join(process.env.JELLYFIN_STYLE_DIR, name)).css).join('\n');
const poster = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600"><rect width="400" height="600" fill="#20394a"/><circle cx="200" cy="240" r="100" fill="#375970"/><text x="200" y="410" text-anchor="middle" font-size="36" font-family="sans-serif" fill="white">WATERWORLD</text><text x="200" y="455" text-anchor="middle" font-size="24" font-family="sans-serif" fill="white">1995</text></svg>';
const items = [{ kinopoiskId: 8169, name: 'Водный мир', originalName: 'Waterworld', year: 1995, mediaType: 'movie',
    overview: 'В будущем полярные льды растаяли и вода покрыла Землю. Рекомендация выбранного фильма с КиноПоиска.',
    ratingKinopoisk: 7.6, posterUrl: 'https://kinopoiskapiunofficial.tech/images/posters/kp/8169.jpg' }];
let posts = [];
let serviceConnected = true;
let serviceReads = 0;
let browser;
let origin;
const errors = [];

function json(response, value, status = 200) {
    response.writeHead(status, { 'content-type': 'application/json' });
    response.end(JSON.stringify(value));
}
const server = http.createServer(async (request, response) => {
    const url = new URL(request.url, origin || 'http://localhost');
    if (url.pathname === '/web/') {
        response.setHeader('content-type', 'text/html; charset=utf-8');
        response.end(`<!doctype html><html lang="ru" dir="ltr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><style>${css}
        body{margin:0;padding:24px;background:#101010;color:#eee;font:16px Arial,sans-serif}h2{font-size:24px}.card{width:220px}.dialog{background:#202020}.textActionButton{border:0;background:transparent;padding:0;color:inherit}.raised{background:#303030}.button-submit{background:#00a4dc;color:white}button:disabled{opacity:.6}.material-icons{font-size:22px}.itemsContainer{overflow-x:auto}.cardText{overflow:hidden}.cardText-secondary{color:#bbb}.verticalSection{margin-bottom:30px}
        </style></head><body><div class="itemDetailPage" id="active"><div class="detailPagePrimaryContent"></div><section id="similarCollapsible"><h2>Штатная карточка Jellyfin</h2><div class="itemsContainer"><div class="card overflowPortraitCard card-hoverable" id="native-card"><div class="cardBox cardBox-bottompadded"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait"></div><button class="cardContent cardImageContainer coveredImage" style="background-image:url('/poster.svg')" aria-label="Штатный постер"></button><div class="cardOverlayContainer"><button class="cardOverlayButton cardOverlayButton-hover cardOverlayFab-primary paper-icon-button-light" aria-label="Воспроизвести">▶</button></div></div><div class="cardText cardTextCentered cardText-first">Водный мир</div><div class="cardText cardTextCentered cardText-secondary">1995</div></div></div></div></section></div><script>
        window.ApiClient={accessToken:()=> 'fixture-token',getCurrentUserId:()=> 'fixture-user',getUrl:p=>new URL(p,location.origin).href,getItem:(_u,id)=>fetch('/item/'+id).then(r=>r.json())};
        </script><script src="/bundle.js"></script></body></html>`);
        return;
    }
    if (url.pathname === '/bundle.js') { response.setHeader('content-type', 'text/javascript'); response.end(bundle); return; }
    if (url.pathname === '/poster.svg' || url.pathname === '/KinopoiskPresentation/image') { response.setHeader('content-type', 'image/svg+xml'); response.end(poster); return; }
    if (url.pathname.startsWith('/item/')) { json(response, { Id: url.pathname.split('/').pop(), Type: 'Movie', ProviderIds: { Kinopoisk: '100' } }); return; }
    if (url.pathname === '/KinopoiskPresentation/100/similars') { json(response, { items }); return; }
    if (url.pathname.startsWith('/KinopoiskRecommendations/library/')) { json(response, { itemId: serviceConnected ? null : 'library-8169' }); return; }
    if (url.pathname.startsWith('/KinopoiskPresentation/')) { json(response, { ratings: {}, releaseDates: [], relations: [], professions: [], items: [] }); return; }
    if (url.pathname.startsWith('/KinopoiskRequests/')) {
        if (request.headers['x-emby-token'] !== 'fixture-token') { json(response, {}, 401); return; }
        if (url.pathname.endsWith('/user-status')) { json(response, serviceConnected
            ? { provider: 'seerr', active: true, userFound: true, jellyseerrUserId: '17' }
            : { provider: 'disabled', active: false, userFound: false, reason: 'disabled' }); return; }
        serviceReads += 1;
        if (url.pathname.endsWith('/search')) { json(response, { results: [{ id: 9804, mediaType: 'movie', title: 'Водный мир', originalTitle: 'Waterworld', releaseDate: '1995-07-28' }] }); return; }
        if (url.pathname.endsWith('/movie/9804')) { json(response, { id: 9804, mediaInfo: { status: posts.length ? 2 : 1 } }); return; }
        if (url.pathname.endsWith('/overrideRule')) { json(response, []); return; }
        if (url.pathname.endsWith('/request') && request.method === 'POST') {
            let body = '';
            for await (const chunk of request) { body += chunk; }
            posts.push(JSON.parse(body));
            json(response, { id: 77 }); return;
        }
    }
    json(response, {}, 404);
});

(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    origin = 'http://127.0.0.1:' + server.address().port;
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', route => {
        const url = new URL(route.request().url());
        return url.origin === origin || url.protocol === 'blob:' ? route.continue() : route.abort();
    });
    await page.goto(origin + '/web/#/details?id=item-1');
    const card = page.locator('.kp-similar-card');
    await card.locator('.kp-similar-card__request').filter({ hasText: /^Запрос$/ }).waitFor();
    assert.equal(await page.evaluate(() => typeof window.JellyfinEnhanced), 'undefined');
    assert.equal(await card.locator('a[href*="kinopoisk.ru"]').count(), 0);
    const style = async selector => page.locator(selector).evaluate(node => {
        const s = getComputedStyle(node);
        return { background: s.backgroundColor, backgroundImage: s.backgroundImage, opacity: s.opacity, radius: s.borderRadius, transition: s.transition, transform: s.transform };
    });
    await page.locator('#native-card').hover();
    await page.waitForTimeout(250);
    const nativeHover = await style('#native-card .cardOverlayContainer');
    await card.hover();
    await page.waitForTimeout(250);
    assert.deepEqual(await style('.kp-similar-card .cardOverlayContainer'), nativeHover, 'Hover overlay diverged from Jellyfin');
    assert.equal((await style('.kp-similar-card .cardBox')).transform, 'none');
    assert.equal((await style('.kp-similar-card .cardScalable')).transform, 'none');
    assert.equal(await card.locator('.cardOverlayContainer').evaluate(node => getComputedStyle(node).opacity), '1');
    assert.equal(await card.boundingBox().then(box => box.width), await page.locator('#native-card').boundingBox().then(box => box.width));
    fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });
    await page.screenshot({ path: path.join(root, 'artifacts/recommendations-hover.png'), fullPage: true });
    await card.locator('.kp-similar-card__request').click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Запрос', exact: true }).waitFor({ state: 'visible' });
    await page.waitForFunction(() => !document.querySelector('.kp-recommendation-dialog__submit').disabled);
    assert.equal(posts.length, 0);
    const dialogBox = await dialog.boundingBox();
    assert.ok(Math.abs(dialogBox.x + dialogBox.width / 2 - 640) < 2, 'Native dialog styles displaced the recommendation dialog');
    await page.screenshot({ path: path.join(root, 'artifacts/recommendations-confirm.png'), fullPage: true });
    await dialog.getByRole('button', { name: 'Запрос', exact: true }).click();
    await dialog.getByRole('status').filter({ hasText: 'Запрос отправлен' }).waitFor();
    assert.deepEqual(posts, [{ mediaType: 'movie', mediaId: 9804 }]);
    await dialog.getByRole('button', { name: 'Закрыть', exact: true }).click();
    await card.locator('.cardText-first button').click();
    await dialog.getByRole('status').filter({ hasText: /^Запрошено$/ }).waitFor();
    assert.equal(await dialog.locator('.kp-recommendation-dialog__submit').isDisabled(), true);
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'detached' });
    assert.equal(posts.length, 1);
    assert.deepEqual(errors, [], 'The complete embedded bundle raised an error');

    // Touch needs one tap, and the dialog must fit a narrow viewport.
    posts = [];
    const mobile = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    const mobilePage = await mobile.newPage();
    mobilePage.on('pageerror', error => errors.push(error.message));
    await mobilePage.goto(origin + '/web/#/details?id=item-1');
    await mobilePage.locator('.kp-similar-card[data-kp-seerr-fallback-state="ready"]').waitFor();
    await mobilePage.locator('.kp-similar-card .cardImageContainer').tap();
    await mobilePage.getByRole('dialog').waitFor();
    const box = await mobilePage.getByRole('dialog').boundingBox();
    assert.ok(box.x >= 0 && box.x + box.width <= 390);
    assert.equal(posts.length, 0);
    await mobilePage.screenshot({ path: path.join(root, 'artifacts/recommendations-mobile.png'), fullPage: true });
    assert.deepEqual(errors, []);

    await mobile.close();
    serviceConnected = false;
    const previousReads = serviceReads;
    const standalone = await browser.newContext({ viewport: { width: 1280, height: 1000 } });
    const standalonePage = await standalone.newPage();
    standalonePage.on('pageerror', error => errors.push(error.message));
    await standalonePage.goto(origin + '/web/#/details?id=item-1');
    await standalonePage.locator('.kp-similar-card[data-kp-seerr-fallback-state="standalone"]').waitFor();
    await standalonePage.locator('.kp-similar-card .cardImageContainer').click();
    const standaloneDialog = standalonePage.getByRole('dialog');
    await standaloneDialog.getByRole('link', { name: 'Открыть в Jellyfin' }).waitFor();
    assert.match(await standaloneDialog.textContent(), /Рекомендация выбранного фильма/);
    assert.equal(await standaloneDialog.locator('.kp-recommendation-dialog__submit').isVisible(), false);
    assert.equal(serviceReads, previousReads, 'Standalone cards contacted the request service');
    await standalonePage.screenshot({ path: path.join(root, 'artifacts/recommendations-standalone.png'), fullPage: true });
    assert.deepEqual(errors, []);
    console.log('Browser checks passed: complete bundle, authenticated proxy, native hover parity, click, confirmation, single POST, status refresh, keyboard, touch.');
    console.log(JSON.stringify({ nativeHover, modules: scripts.length, errors }));
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
    if (browser) { await browser.close(); }
    await new Promise(resolve => server.close(resolve));
});
