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
assert.equal(scripts.length, 14);
const bundle = scripts.map(name => fs.readFileSync(path.join(web, name), 'utf8')).join('\n\n');
const iconCss = fs.readFileSync(path.join(process.env.JELLYFIN_STYLE_DIR, 'material-design-icons.css'), 'utf8').replace(/@font-face\s*\{[\s\S]*?\}/, '');
const css = iconCss + ['card.scss', 'emby-button.scss', 'dialoghelper.scss', 'styles/librarybrowser.scss', 'styles/scrollstyles.scss', 'themes/_base/_theme.scss', 'elements/emby-scroller/emby-scroller.scss', 'elements/emby-scrollbuttons/emby-scrollbuttons.scss'].map(name => sass.compile(path.join(process.env.JELLYFIN_STYLE_DIR, name)).css).join('\n');
// The actual Jellyfin click handler is exercised with a playback recorder.
// Imports are substituted only in this fixture; the production bundle delegates
// to the normal emby-itemscontainer handler already loaded by Jellyfin Web.
const shortcutSource = fs.readFileSync(path.join(process.env.JELLYFIN_STYLE_DIR, 'shortcuts.js'), 'utf8')
    .replace(/^import[ \t][\s\S]*?;[ \t]*$/gm, '').replace(/export function /g, 'function ').replace('export default', 'const shortcutExports =');
const nativeActions = Object.fromEntries([...fs.readFileSync(path.join(process.env.JELLYFIN_STYLE_DIR, 'itemAction.ts'), 'utf8')
    .matchAll(/(\w+) = '([^']+)'/g)].map(match => [match[1], match[2]]));
assert.equal(nativeActions.Resume, 'resume');
const shortcutFixture = '(function(){const ItemAction=' + JSON.stringify(nativeActions) + `;
window.playbackRequests=[];
const playbackManager={canPlay:()=>true,play:options=>{window.playbackRequests.push(options);return Promise.resolve();}};
const dom={parentWithClass:(node,name)=>node.closest('.'+name),parentWithAttribute:(node,name)=>node.closest('['+name+']')};
const userSettings={getSortValuesLegacy:()=>({sortBy:'SortName',sortOrder:'Ascending'})};
` + shortcutSource + ';document.addEventListener("click",onClick);})();';
// Run Jellyfin's actual legacy scroller and arrow handlers. Only the v0
// custom-element lifecycle adapter and service imports belong to the fixture.
const nativeSource = name => fs.readFileSync(path.join(process.env.JELLYFIN_STYLE_DIR, name), 'utf8').replace(/^import[^;]*;\s*$/gm, '');
const scrollUtils = require('node:module').stripTypeScriptTypes(nativeSource('elements/emby-scrollbuttons/utils.ts').replace(/^export /gm, ''), { mode: 'transform' });
const carouselFixture = `(function(){
const dom={addEventListener:(node,name,handler,options)=>node.addEventListener(name,handler,options),removeEventListener:(node,name,handler,options)=>node.removeEventListener(name,handler,options)};
const browser={animate:true,supportsCssAnimation:()=>true};
const layoutManager={desktop:matchMedia('(pointer:fine)').matches,tv:false};
const focusManager={focusableParent:node=>node.closest('button,a,input')};
const inputManager={on(){},off(){}};
const hasFinePointer=()=>matchMedia('(pointer:fine)').matches;
const globalize={getIsRTL:()=>document.documentElement.dir==='rtl',getIsElementRTL:node=>getComputedStyle(node).direction==='rtl',translate:key=>key==='Previous'?'Назад':'Далее'};
const registered=new Map(),upgraded=new Map();
function upgrade(){
 document.querySelectorAll('[is]').forEach(node=>{
  const name=node.getAttribute('is'),prototype=registered.get(name);
  if(name==='paper-icon-button-light')node.classList.add(name);
  if(name==='emby-linkbutton'||name==='emby-button')node.classList.add('emby-button');
  if(prototype&&!upgraded.has(node)){Object.setPrototypeOf(node,prototype);upgraded.set(node,false);if(prototype.createdCallback)prototype.createdCallback.call(node);}
 });
 upgraded.forEach((attached,node)=>{const connected=node.isConnected;if(connected!==attached){upgraded.set(node,connected);const callback=connected?node.attachedCallback:node.detachedCallback;if(callback)callback.call(node);if(!connected)upgraded.delete(node);}});
}
document.registerElement=(name,options)=>{registered.set(name,options.prototype);upgrade();};
window.CustomElements={upgradeSubtree:upgrade};
new MutationObserver(upgrade).observe(document.documentElement,{childList:true,subtree:true});
` + nativeSource('lib/scroller/index.js').replace('export default scrollerFactory;', 'const ScrollerFactory=scrollerFactory;')
    + scrollUtils
    + nativeSource('elements/emby-scrollbuttons/emby-scrollbuttons.js')
    + nativeSource('elements/emby-scroller/emby-scroller.js').replace("import('../emby-scrollbuttons/emby-scrollbuttons')", 'Promise.resolve()')
    + '})();';
new (require('node:vm').Script)(carouselFixture, { filename: 'native-carousel-fixture.js' });
const poster = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600"><rect width="400" height="600" fill="#20394a"/><circle cx="200" cy="240" r="100" fill="#375970"/><text x="200" y="410" text-anchor="middle" font-size="36" font-family="sans-serif" fill="white">WATERWORLD</text><text x="200" y="455" text-anchor="middle" font-size="24" font-family="sans-serif" fill="white">1995</text></svg>';
const items = [{ imdbId: 'tt0114898', kinopoiskId: 8169, name: 'Водный мир', originalName: 'Waterworld', year: 1995, mediaType: 'movie',
    overview: 'В будущем полярные льды растаяли и вода покрыла Землю. Рекомендация выбранного фильма с КиноПоиска.',
    ratingKinopoisk: 7.6, posterUrl: 'https://kinopoiskapiunofficial.tech/images/posters/kp/8169.jpg' }];
let posts = [];
let serviceConnected = true;
let enhancedConnected = false;
let enhancedInstalled = false;
let serviceReads = 0;
let mediaStatus = null;
let libraryAvailable = false;
let favorite = false;
let wishes = [];
const enhancedCalls = [];
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
        @font-face{font-family:'Material Icons';font-style:normal;font-weight:400;src:url('/material-icons.woff2') format('woff2')}
        .material-icons{font-family:'Material Icons';font-weight:normal;font-style:normal;display:inline-block;line-height:1;letter-spacing:normal;text-transform:none;white-space:nowrap;word-wrap:normal;direction:ltr;font-feature-settings:'liga'}
        .hide,[hidden]{display:none!important}#indexPage .tabContent:not(.is-active){display:none}body{margin:0;padding:24px;overflow-x:hidden;background:#101010;color:#eee;font:16px Arial,sans-serif}h2{font-size:24px}.card{width:220px}.dialog{background:#202020}.textActionButton{border:0;background:transparent;padding:0;color:inherit}.raised{background:#303030}.button-submit{background:#00a4dc;color:white}button:disabled{opacity:.6}.material-icons{font-size:22px}.cardText{overflow:hidden}.cardText-secondary{color:#bbb}.verticalSection{margin-bottom:30px}
        </style></head><body><div class="itemDetailPage" id="active"><div class="detailPagePrimaryContent"></div><section id="similarCollapsible"><h2>Штатная карточка Jellyfin</h2><div class="itemsContainer"><div class="card overflowPortraitCard card-hoverable" id="native-card"><div class="cardBox cardBox-bottompadded"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait"></div><button class="cardContent cardImageContainer coveredImage" style="background-image:url('/poster.svg')" aria-label="Штатный постер"></button><div class="cardOverlayContainer"><button class="cardOverlayButton cardOverlayButton-hover cardOverlayFab-primary paper-icon-button-light" aria-label="Воспроизвести">▶</button></div></div><div class="cardText cardTextCentered cardText-first">Водный мир</div><div class="cardText cardTextCentered cardText-secondary">1995</div></div></div></div></section></div><div id="indexPage" class="page homePage" hidden><div id="homeTab" class="tabContent pageTabContent"><div class="sections"></div></div><div id="favoritesTab" class="tabContent pageTabContent"><div class="sections"><section class="verticalSection" id="native-favorites"><div class="sectionTitleContainer sectionTitleContainer-cards padded-left"><a is="emby-linkbutton" href="#/list?itemTypes=Movie&amp;isFavorite=true" class="more button-flat button-flat-mini sectionTitleTextButton"><h2 class="sectionTitle sectionTitle-cards">Фильмы</h2></a></div><div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale"><div is="emby-itemscontainer" class="itemsContainer scrollSlider focuscontainer-x" id="native-favorite-items"></div></div></section></div></div></div><script>
        document.documentElement.classList.add(matchMedia('(pointer:fine)').matches?'layout-desktop':'layout-mobile');
        const nativeFavorite=document.querySelector('#native-card').cloneNode(true);nativeFavorite.id='native-favorite-card';document.querySelector('#native-favorite-items').appendChild(nativeFavorite);
        window.fixtureFavorites=active=>{document.querySelector('#active').hidden=true;document.querySelector('#indexPage').hidden=false;document.querySelector('#favoritesTab').classList.toggle('is-active',active);document.querySelector('#homeTab').classList.toggle('is-active',!active);location.hash='#/home?tab='+(active?1:0);};
        if(location.hash.startsWith('#/home'))window.fixtureFavorites(location.hash.includes('tab=1'));
        window.ApiClient={accessToken:()=> 'fixture-token',getCurrentUserId:()=> 'fixture-user',serverInfo:()=>({Id:'fixture-server'}),getUrl:p=>new URL(p,location.origin).href,getItem:(_u,id)=>fetch('/item/'+id).then(r=>r.json())};
        </script><script src="/native-carousels.js"></script><script src="/native-shortcuts.js"></script><script src="/bundle.js"></script></body></html>`);
        return;
    }
    if (url.pathname === '/material-icons.woff2') { response.setHeader('content-type', 'font/woff2'); response.end(fs.readFileSync(path.join(process.env.JELLYFIN_STYLE_DIR, 'MaterialIcons-Regular.woff2'))); return; }
    if (url.pathname === '/native-carousels.js') { response.setHeader('content-type', 'text/javascript'); response.end(carouselFixture); return; }
    if (url.pathname === '/native-shortcuts.js') { response.setHeader('content-type', 'text/javascript'); response.end(shortcutFixture); return; }
    if (url.pathname === '/bundle.js') { response.setHeader('content-type', 'text/javascript'); response.end(bundle); return; }
    if (url.pathname === '/poster.svg' || url.pathname === '/KinopoiskPresentation/image') { response.setHeader('content-type', 'image/svg+xml'); response.end(poster); return; }
    if (url.pathname.startsWith('/item/')) { json(response, { Id: url.pathname.split('/').pop(), Type: 'Movie', Name: 'Водный мир', OriginalTitle: 'Waterworld', ProductionYear: 1995, ProviderIds: { Kinopoisk: '100', Imdb: 'tt0114898' } }); return; }
    if (url.pathname === '/KinopoiskPresentation/100/similars') { json(response, { items }); return; }
    if (url.pathname === '/KinopoiskRequests/integrations') { json(response, { enhanced: { available: enhancedInstalled || enhancedConnected, state: enhancedInstalled || enhancedConnected ? 'available' : 'missing' } }); return; }
    if (url.pathname === '/JellyfinEnhanced/public-config') { enhancedCalls.push({ path: url.pathname }); json(response, { TmdbEnabled: true }); return; }
    if (url.pathname.startsWith('/JellyfinEnhanced/tmdb/')) { enhancedCalls.push({ path: url.pathname }); json(response, {}, 404); return; }
    if (url.pathname === '/KinopoiskRecommendations/state') {
        const keys = (url.searchParams.get('keys') || '').split(',');
        json(response, { items: keys.map(key => ({ key, itemId: libraryAvailable ? 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' : null,
            canPlay: libraryAvailable, favorite, playbackPositionTicks: libraryAvailable ? 12345678 : 0,
            wished: wishes.some(item => key === item.mediaType + ':kp:' + item.kinopoiskId) })) }); return;
    }
    if (url.pathname === '/KinopoiskRecommendations/wishlist') {
        if (request.method === 'PUT') {
            let body = ''; for await (const chunk of request) body += chunk;
            wishes = [JSON.parse(body)]; json(response, { saved: true }); return;
        }
        json(response, { items: wishes }); return;
    }
    if (url.pathname.startsWith('/KinopoiskRecommendations/wishlist/')) { wishes = []; json(response, { saved: false }); return; }
    if (url.pathname.startsWith('/UserFavoriteItems/')) { favorite = request.method === 'POST'; json(response, { IsFavorite: favorite }); return; }
    if (url.pathname.startsWith('/KinopoiskRecommendations/library/')) { json(response, { itemId: serviceConnected ? null : 'library-8169' }); return; }
    if (url.pathname.startsWith('/KinopoiskPresentation/')) { json(response, { ratings: {}, releaseDates: [], relations: [], professions: [], items: [] }); return; }
    if (url.pathname.startsWith('/KinopoiskRequests/') || url.pathname.startsWith('/JellyfinEnhanced/jellyseerr/')) {
        if (request.headers['x-emby-token'] !== 'fixture-token') { json(response, {}, 401); return; }
        if (url.pathname.startsWith('/JellyfinEnhanced/')) {
            enhancedCalls.push({ path: url.pathname, user: request.headers['x-jellyfin-user-id'] });
            if (!enhancedConnected) { json(response, {}, 404); return; }
        }
        if (url.pathname === '/KinopoiskRequests/user-status' && enhancedConnected) {
            json(response, { provider: 'enhanced', active: false, userFound: false }); return;
        }
        if (url.pathname.endsWith('/user-status')) { json(response, serviceConnected
            ? { provider: 'seerr', active: true, userFound: true, jellyseerrUserId: '17' }
            : { provider: 'disabled', active: false, userFound: false, reason: 'disabled' }); return; }
        serviceReads += 1;
        if (url.pathname.endsWith('/search')) { json(response, { results: [{ id: 9804, mediaType: 'movie', title: 'Водный мир', originalTitle: 'Waterworld', releaseDate: '1995-07-28' }] }); return; }
        if (url.pathname.endsWith('/movie/9804')) { json(response, { id: 9804, mediaInfo: {
            status: mediaStatus || (posts.length ? 2 : 1), requests: posts.length ? [{ id: 77, status: 2 }] : []
        } }); return; }
        if (url.pathname.endsWith('/quota')) { json(response, { movie: { limit: 5, used: posts.length, remaining: 5 - posts.length, days: 7 }, tv: { limit: 0 } }); return; }
        if (url.pathname.endsWith('/link')) { json(response, { mode: 'auto', url: 'https://seerr.example/subpath' }); return; }
        if (url.pathname.endsWith('/overrideRule')) { json(response, []); return; }
        if (url.pathname.endsWith('/request') && request.method === 'POST') {
            let body = '';
            for await (const chunk of request) { body += chunk; }
            posts.push(JSON.parse(body));
            json(response, { id: 77, status: 2, media: { status: 2 } }); return;
        }
    }
    json(response, {}, 404);
});

(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    origin = 'http://127.0.0.1:' + server.address().port;
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    page.on('pageerror', error => { errors.push(error.message); console.error(error.stack || error.message); });
    await page.route('**/*', route => {
        const url = new URL(route.request().url());
        return url.origin === origin || url.protocol === 'blob:' ? route.continue() : route.abort();
    });
    await page.goto(origin + '/web/#/details?id=item-1');
    const card = page.locator('.kp-similar-card');
    await card.locator('.kp-similar-card__request').filter({ hasText: /^Запросить$/ }).waitFor();
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
    // Keep hovering long enough for the former observer/reinsert loop to fire.
    await page.waitForFunction(() => !document.querySelector('.kp-similar-card__heart').disabled);
    await card.locator('.kp-similar-card__info').hover();
    await page.waitForTimeout(350);
    const hoverStability = await card.evaluate(async node => {
        let moves = 0;
        const info = node.querySelector('.kp-similar-card__info');
        const observer = new MutationObserver(changes => { moves += changes.filter(change => [...change.addedNodes, ...change.removedNodes].includes(info)).length; });
        observer.observe(node, { childList: true, subtree: true });
        const transforms = [];
        for (let index = 0; index < 8; index++) {
            transforms.push(getComputedStyle(info).transform);
            await new Promise(resolve => setTimeout(resolve, 150));
        }
        observer.disconnect();
        return { moves, transforms };
    });
    assert.equal(hoverStability.moves, 0, 'Information hover kept moving its button');
    assert.equal(new Set(hoverStability.transforms).size, 1, 'Information hover kept pulsing');
    assert.match(hoverStability.transforms[0], /^matrix\(1[.]4/);
    fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });
    await page.screenshot({ path: path.join(root, 'artifacts/recommendations-hover.png'), fullPage: true });
    await card.locator('.kp-similar-card__request').click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Запросить', exact: true }).waitFor({ state: 'visible' });
    await page.waitForFunction(() => !document.querySelector('.kp-recommendation-dialog__submit').disabled);
    assert.equal(posts.length, 0);
    await dialog.locator('.kp-recommendation-dialog__quota').filter({ hasText: 'осталось 5 из 5' }).waitFor();
    assert.equal(await dialog.getByRole('link', { name: 'Открыть в Seerr', exact: true }).getAttribute('href'), 'https://seerr.example/subpath/movie/9804');
    const dialogBox = await dialog.boundingBox();
    assert.ok(Math.abs(dialogBox.x + dialogBox.width / 2 - 640) < 2, 'Native dialog styles displaced the recommendation dialog');
    assert.equal(await dialog.getByRole('button', { name: 'Закрыть', exact: true }).evaluate(node => getComputedStyle(node).backgroundColor), 'rgba(0, 0, 0, 0)');
    for (const name of ['КиноПоиск', 'Открыть в Seerr']) {
        const link = dialog.getByRole('link', { name, exact: true });
        await link.hover();
        await page.waitForTimeout(250);
        assert.equal(await link.evaluate(node => getComputedStyle(node).color), 'rgb(0, 164, 220)', name + ' missed the native hover color');
        assert.equal(await link.evaluate(node => getComputedStyle(node).backgroundColor), 'rgba(0, 0, 0, 0)');
    }

    await page.screenshot({ path: path.join(root, 'artifacts/recommendations-confirm.png'), fullPage: true });
    await dialog.getByRole('button', { name: 'Запросить', exact: true }).click();
    await dialog.getByRole('status').filter({ hasText: 'Запрос отправлен' }).waitFor();
    assert.deepEqual(posts, [{ mediaType: 'movie', mediaId: 9804 }]);
    await dialog.getByRole('button', { name: 'Закрыть', exact: true }).click();
    await page.mouse.move(1100, 200);
    await page.waitForTimeout(250);
    assert.equal(await card.evaluate(node => node.contains(document.activeElement)), false, 'Pointer close retained focus on the card');
    assert.equal(await card.locator('.cardOverlayContainer').evaluate(node => getComputedStyle(node).opacity), '0', 'Hover remained after pointer close');
    await page.screenshot({ path: path.join(root, 'artifacts/recommendations-after-close.png'), fullPage: true });
    await card.locator('.cardText-first button').click();
    await dialog.getByRole('status').filter({ hasText: /^Одобрено$/ }).waitFor();
    mediaStatus = 3;
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await dialog.getByRole('status').filter({ hasText: /^Загружается$/ }).waitFor();
    await card.locator('.kp-similar-card__request').filter({ hasText: /^Загружается$/ }).waitFor();
    mediaStatus = 5;
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await dialog.getByRole('status').filter({ hasText: /^Доступно$/ }).waitFor();
    assert.equal(posts.length, 1, 'Status refresh repeated a POST');
    await page.screenshot({ path: path.join(root, 'artifacts/recommendations-live-status.png'), fullPage: true });
    assert.equal(await dialog.locator('.kp-recommendation-dialog__submit').isDisabled(), true);
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'detached' });
    assert.equal(posts.length, 1);
    // Keyboard users retain their position and can reopen the dialog.
    const keyboardOpener = card.locator('.cardText-first button');
    await keyboardOpener.focus();
    await keyboardOpener.press('Enter');
    await dialog.waitFor({ state: 'visible' });
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'detached' });
    assert.equal(await keyboardOpener.evaluate(node => document.activeElement === node), true, 'Keyboard close lost the opener');
    assert.deepEqual(errors, [], 'The complete embedded bundle raised an error');

    // Auto mode discovers the server integration without any Enhanced JS global.
    enhancedConnected = true;
    mediaStatus = null;
    posts = [];
    const enhancedPage = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    enhancedPage.on('pageerror', error => { errors.push(error.message); console.error(error.stack || error.message); });
    await enhancedPage.goto(origin + '/web/#/details?id=item-1');
    await enhancedPage.getByRole('button', { name: 'Запросить', exact: true }).click();
    const enhancedDialog = enhancedPage.getByRole('dialog');
    await enhancedDialog.getByRole('button', { name: 'Запросить', exact: true }).waitFor({ state: 'visible' });
    assert.equal(await enhancedPage.evaluate(() => typeof window.JellyfinEnhanced), 'undefined');
    assert.ok(enhancedCalls.some(call => call.path.endsWith('/user-status')));
    assert.ok(enhancedCalls.filter(call => call.path.includes('/jellyseerr/')).every(call => call.user === 'fixture-user'));
    assert.equal(posts.length, 0);
    await enhancedPage.screenshot({ path: path.join(root, 'artifacts/recommendations-enhanced.png'), fullPage: true });
    await enhancedPage.close();
    enhancedConnected = false;

    serviceConnected = false;
    const previousReads = serviceReads;
    const standalone = await browser.newContext({ viewport: { width: 1280, height: 1000 } });
    const standalonePage = await standalone.newPage();
    standalonePage.on('pageerror', error => { errors.push(error.message); console.error(error.stack || error.message); });
    await standalonePage.goto(origin + '/web/#/details?id=item-1');
    await standalonePage.locator('.kp-similar-card[data-kp-seerr-fallback-state="standalone"]').waitFor();
    await standalonePage.locator('.kp-similar-card .cardScalable').scrollIntoViewIfNeeded();
    const standalonePoster = await standalonePage.locator('.kp-similar-card .cardScalable').boundingBox();
    await standalonePage.mouse.click(standalonePoster.x + standalonePoster.width / 2, standalonePoster.y + standalonePoster.height / 2);
    const standaloneDialog = standalonePage.getByRole('dialog');
    await standaloneDialog.getByRole('link', { name: 'Открыть в Jellyfin' }).waitFor();
    assert.match(await standaloneDialog.textContent(), /Рекомендация выбранного фильма/);
    assert.equal(await standaloneDialog.locator('.kp-recommendation-dialog__submit').isVisible(), false);
    const libraryLink = standaloneDialog.getByRole('link', { name: 'Открыть в Jellyfin' });
    await libraryLink.hover();
    await standalonePage.waitForTimeout(250);
    assert.equal(await libraryLink.evaluate(node => getComputedStyle(node).color), 'rgb(0, 164, 220)');

    assert.equal(serviceReads, previousReads, 'Standalone cards contacted the request service');
    await standalonePage.screenshot({ path: path.join(root, 'artifacts/recommendations-standalone.png'), fullPage: true });
    assert.deepEqual(errors, []);
    await standaloneDialog.getByRole('button', { name: 'Закрыть', exact: true }).click();
    const heart = standalonePage.locator('.kp-similar-card__heart');
    await standalonePage.waitForFunction(() => !document.querySelector('.kp-similar-card__heart').disabled);
    await heart.click();
    await standalonePage.waitForFunction(() => document.querySelector('.kp-similar-card__heart').getAttribute('aria-pressed') === 'true');
    assert.equal(wishes.length, 1);
    assert.equal(posts.length, 0, 'Heart created an unsolicited Seerr request');
    await standalonePage.getByRole('tab', { name: 'Желаемое', exact: true }).click();
    await standalonePage.locator('.kp-similar-card__title').filter({ hasText: 'Водный мир' }).waitFor();
    await standalonePage.screenshot({ path: path.join(root, 'artifacts/recommendations-wishlist.png'), fullPage: true });
    // A new browser page reads the list saved on the server.
    await standalonePage.reload();
    await standalonePage.getByRole('tab', { name: 'Желаемое', exact: true }).click();
    await standalonePage.locator('.kp-similar-card__title').filter({ hasText: 'Водный мир' }).waitFor();
    // The native Favorites tab displays the same server-backed wishlist.
    await standalonePage.evaluate(() => window.fixtureFavorites(true));
    const favorites = standalonePage.locator('#kinopoiskFavoritesWishlist');
    await favorites.locator('.kp-similar-card__title').filter({ hasText: 'Водный мир' }).waitFor();
    await standalonePage.locator('#native-favorites').waitFor();
    assert.equal(await favorites.locator('[is="emby-itemscontainer"]').count(), 1);
    await favorites.locator('.kp-similar-card__info').click();
    await standaloneDialog.getByRole('link', { name: 'Открыть в Jellyfin' }).waitFor();
    assert.equal(await standaloneDialog.locator('.kp-recommendation-dialog__submit').isVisible(), false);
    await standalonePage.keyboard.press('Escape');

    await standalonePage.waitForTimeout(350);
    const nativePosterBox = await standalonePage.locator('#native-favorite-card .cardImageContainer').boundingBox();
    const wishPosterBox = await favorites.locator('.kp-similar-card__poster').boundingBox();
    assert.ok(Math.abs(nativePosterBox.x - wishPosterBox.x) < 1, 'Favorites rows have different left padding');
    assert.ok(Math.abs(nativePosterBox.width - wishPosterBox.width) < 1, 'Favorites poster widths differ');
    assert.ok(Math.abs(nativePosterBox.height - wishPosterBox.height) < 1, 'Favorites poster heights differ');
    await standalonePage.getByRole('heading', { name: 'Готово к просмотру — фильмы', exact: true }).waitFor();
    await favorites.getByRole('heading', { name: 'Хочу посмотреть', exact: true }).waitFor();
    assert.match(await favorites.locator('.kp-favorites-wishlist-description').textContent(), /ещё не скачан/);
    assert.equal(await favorites.locator('.emby-scrollbuttons-button:visible').count(), 0, 'Sparse wishlist has navigation arrows');
    console.log('Native Favorites alignment:', JSON.stringify({ nativePosterBox, wishPosterBox }));
    await standalonePage.screenshot({ path: path.join(root, 'artifacts/recommendations-favorites.png'), fullPage: true });
    assert.equal(serviceReads, previousReads, 'Favorites introduced a request-service dependency');
    await standalonePage.reload();
    await favorites.locator('.kp-similar-card__title').waitFor();
    assert.equal(posts.length, 0);
    // Direct Seerr and auto-discovered Enhanced work on Favorites too.
    serviceConnected = true;
    for (const useEnhanced of [false, true]) {
        enhancedConnected = useEnhanced;
        await standalonePage.reload();
        const saved = favorites.locator('.kp-similar-card');
        await saved.locator('.kp-similar-card__request').filter({ hasText: /^Запросить$/ }).waitFor();
        await saved.locator('.kp-similar-card__info').click();
        await standaloneDialog.getByRole('button', { name: 'Запросить', exact: true }).waitFor();
        await standalonePage.waitForFunction(() => !document.querySelector('.kp-recommendation-dialog__submit').disabled);
        assert.equal(posts.length, 0, 'Opening a saved title sent a request');
        await standalonePage.keyboard.press('Escape');
    }
    enhancedConnected = false;
    serviceConnected = false;
    await standalonePage.reload();
    await favorites.locator('.kp-similar-card__heart:not([disabled])').waitFor();
    wishes = Array.from({ length: 12 }, (_, index) => ({ ...items[0], kinopoiskId: 8169 + index, name: 'Сохранённый фильм ' + (index + 1) }));
    await standalonePage.reload();
    await standalonePage.waitForFunction(() => document.querySelectorAll('#kinopoiskFavoritesWishlist .kp-similar-card').length === 12);
    const nextWish = favorites.locator('.emby-scrollbuttons-button[data-direction="right"]');
    const previousWish = favorites.locator('.emby-scrollbuttons-button[data-direction="left"]');
    await nextWish.waitFor({ state: 'visible' });
    assert.equal(await previousWish.isDisabled(), true);
    await nextWish.click();
    await standalonePage.waitForFunction(() => document.querySelector('#kinopoiskFavoritesWishlist .kp-recommendations-scroller').getScrollPosition() > 0);
    assert.equal(await previousWish.isDisabled(), false);
    for (let attempt = 0; attempt < 4 && !await nextWish.isDisabled(); attempt++) {
        await nextWish.click();
        await standalonePage.waitForTimeout(350);
    }
    assert.equal(await nextWish.isDisabled(), true, 'Carousel did not stop at the final card');
    await standalonePage.setViewportSize({ width: 3200, height: 1000 });
    await standalonePage.waitForFunction(() => document.querySelector('#kinopoiskFavoritesWishlist [is="emby-scrollbuttons"]').hidden);
    assert.equal(await favorites.locator('.emby-scrollbuttons-button:visible').count(), 0, 'Resize left arrows without overflow');
    await standalonePage.setViewportSize({ width: 1280, height: 1000 });
    await nextWish.waitFor({ state: 'visible' });
    await standalonePage.evaluate(() => document.querySelector('#kinopoiskFavoritesWishlist .kp-recommendations-scroller').scrollToBeginning());
    await standalonePage.waitForTimeout(350);
    await standalonePage.screenshot({ path: path.join(root, 'artifacts/recommendations-favorites-carousel.png'), fullPage: true });
    wishes = [{ ...items[0] }];
    await standalonePage.reload();
    await favorites.locator('.kp-similar-card__heart:not([disabled])').waitFor();
    assert.equal(await favorites.locator('.emby-scrollbuttons-button:visible').count(), 0, 'Removing titles left stale arrows');
    await favorites.locator('.kp-similar-card__heart').click();
    await favorites.getByText('В желаемом пока ничего нет.', { exact: false }).waitFor();
    assert.equal(wishes.length, 0);
    await standalonePage.evaluate(() => window.fixtureFavorites(false));
    await favorites.waitFor({ state: 'detached' });
    await standalonePage.close();
    wishes = [];
    libraryAvailable = true;
    const localPage = await standalone.newPage();
    localPage.on('pageerror', error => { errors.push(error.message); console.error(error.stack || error.message); });
    await localPage.goto(origin + '/web/#/details?id=item-1');
    const localCard = localPage.locator('.kp-similar-card');
    await localCard.locator('.kp-similar-card__play:not([hidden])').waitFor();
    await localCard.hover();
    await localCard.getByRole('button', { name: 'Продолжить просмотр', exact: true }).click();
    assert.deepEqual(await localPage.evaluate(() => window.playbackRequests), [{
        ids: ['aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'], startPositionTicks: 12345678, serverId: 'fixture-server',
        queryOptions: { SortBy: 'SortName', SortOrder: 'Ascending' }
    }]);
    assert.equal(await localPage.getByRole('dialog').count(), 0, 'Play opened the details dialog');
    await localCard.locator('.kp-similar-card__heart').click();
    await localPage.waitForFunction(() => document.querySelector('.kp-similar-card__heart').getAttribute('aria-pressed') === 'true');
    assert.equal(favorite, true);
    assert.equal(posts.length, 0);
    assert.equal(await localCard.locator('.kp-similar-card__request').isVisible(), false);
    await localPage.screenshot({ path: path.join(root, 'artifacts/recommendations-native-play.png'), fullPage: true });
    await localCard.locator('.kp-similar-card__info').click();
    await localPage.getByRole('dialog').waitFor();
    assert.equal(await localPage.evaluate(() => window.playbackRequests.length), 1, 'Information click started playback');
    await localPage.close();
    assert.deepEqual(errors, []);
    // Touch needs one tap, and the dialog must fit a narrow viewport.
    posts = [];
    serviceConnected = true;
    libraryAvailable = false;
    const mobile = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    const mobilePage = await mobile.newPage();
    mobilePage.on('pageerror', error => { errors.push(error.message); console.error(error.stack || error.message); });
    await mobilePage.goto(origin + '/web/#/details?id=item-1');
    await mobilePage.locator('.kp-similar-card[data-kp-seerr-fallback-state="ready"]').waitFor();
    await mobilePage.locator('.kp-similar-card .cardScalable').scrollIntoViewIfNeeded();
    const touchPoster = await mobilePage.locator('.kp-similar-card .cardScalable').boundingBox();
    await mobilePage.touchscreen.tap(touchPoster.x + touchPoster.width / 2, touchPoster.y + touchPoster.height / 2);
    await mobilePage.getByRole('dialog').waitFor();
    const box = await mobilePage.getByRole('dialog').boundingBox();
    await mobilePage.screenshot({ path: path.join(root, 'artifacts/recommendations-mobile.png'), fullPage: true });
    const mobileLayout = await mobilePage.evaluate(() => ({ innerWidth, screenWidth: screen.width,
        visualWidth: visualViewport.width, documentWidth: document.documentElement.scrollWidth,
        dialog: getComputedStyle(document.querySelector('dialog')).cssText }));
    console.log('Mobile dialog layout:', JSON.stringify({ box, ...mobileLayout }));
    assert.ok(box.x >= 0 && box.x + box.width <= 390, JSON.stringify({ box, ...mobileLayout }));
    assert.equal(posts.length, 0);
    await mobilePage.screenshot({ path: path.join(root, 'artifacts/recommendations-mobile.png'), fullPage: true });
    assert.deepEqual(errors, []);

    await mobile.close();
    // Полный bundle воспроизвёл инцидент: разные IMDb ID при отсутствующем Enhanced.
    serviceConnected = true;
    enhancedConnected = false;
    enhancedInstalled = false;
    enhancedCalls.length = 0;
    const incidentPage = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
    incidentPage.on('pageerror', error => errors.push(error.message));
    await incidentPage.goto(origin + '/web/#/details?id=incident');
    await incidentPage.locator('[data-kp-seerr-fallback-state="ready"]').first().waitFor();
    const resolved = await incidentPage.evaluate(() => Promise.all(Array.from({ length: 40 }, (_, index) =>
        window.KinopoiskTmdbResolver.resolve('tt' + (9000000 + index), index % 2 ? 'Series' : 'Movie'))));
    assert.ok(resolved.every(id => id === null));
    assert.equal(enhancedCalls.length, 0, 'Отсутствующий Enhanced получил запросы из полного bundle');
    await incidentPage.locator('.kp-similar-card .cardOverlayFab-primary').first().click();
    await incidentPage.getByRole('dialog').waitFor();
    await incidentPage.close();
    assert.deepEqual(errors, []);
    console.log('Browser checks passed: complete bundle, authenticated proxy, native hover parity, click, confirmation, single POST, live status refresh, per-user quota, Seerr links, keyboard, touch, native playback handler, resume, favorites, persistent wishlist, stable information hover, native Favorites tab, standalone and optional Seerr/Enhanced actions.');
    console.log(JSON.stringify({ nativeHover, modules: scripts.length, errors }));
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
    if (browser) { await browser.close(); }
    await new Promise(resolve => server.close(resolve));
});
