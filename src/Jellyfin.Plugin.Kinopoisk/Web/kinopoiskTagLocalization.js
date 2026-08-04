(function () {
    'use strict';

    if (window.__kinopoiskTagLocalizationInstalled) {
        return;
    }

    window.__kinopoiskTagLocalizationInstalled = true;

    var renderTimer = null;
    var translations = Object.freeze({
        'alien': 'инопланетянин',
        'alien invasion': 'вторжение инопланетян',
        'android': 'андроид',
        'apocalypse': 'апокалипсис',
        'artificial intelligence': 'искусственный интеллект',
        'astronaut': 'астронавт',
        'based on novel': 'экранизация романа',
        'based on true story': 'основано на реальных событиях',
        'betrayal': 'предательство',
        'black comedy': 'чёрная комедия',
        'coming of age': 'взросление',
        'conspiracy': 'заговор',
        'cosmic horror': 'космический хоррор',
        'cosmic threat': 'космическая угроза',
        'crime investigation': 'расследование преступления',
        'cyberpunk': 'киберпанк',
        'deep space': 'дальний космос',
        'deep-space mission': 'миссия в дальнем космосе',
        'dystopia': 'антиутопия',
        'family': 'семья',
        'female protagonist': 'главная героиня',
        'friendship': 'дружба',
        'future': 'будущее',
        'ghost': 'призрак',
        'government conspiracy': 'правительственный заговор',
        'haunted house': 'дом с привидениями',
        'hidden identity': 'скрытая личность',
        'horror': 'хоррор',
        'immersive tension': 'нарастающее напряжение',
        'inner conflict': 'внутренний конфликт',
        'investigation': 'расследование',
        'isolation': 'изоляция',
        'kidnapping': 'похищение',
        'loss': 'утрата',
        'love': 'любовь',
        'memory loss': 'потеря памяти',
        'murder': 'убийство',
        'mystery': 'тайна',
        'nasa': 'NASA',
        'nasa mission': 'миссия NASA',
        'near future': 'ближайшее будущее',
        'outer space': 'космос',
        'orbital survival': 'выживание на орбите',
        'paranoia': 'паранойя',
        'post-apocalyptic': 'постапокалипсис',
        'psychological horror': 'психологический хоррор',
        'psychological thriller': 'психологический триллер',
        'revenge': 'месть',
        'robot': 'робот',
        'serial killer': 'серийный убийца',
        'slow burn': 'медленное развитие',
        'slow-burn suspense': 'медленно нарастающий саспенс',
        'solitary struggle': 'одиночная борьба',
        'space': 'космос',
        'space mission': 'космическая миссия',
        'space station': 'космическая станция',
        'spaceship': 'космический корабль',
        'spy': 'шпион',
        'supernatural': 'сверхъестественное',
        'surveillance': 'наблюдение',
        'survival': 'выживание',
        'survival horror': 'хоррор на выживание',
        'time loop': 'временная петля',
        'time travel': 'путешествие во времени',
        'trapped': 'в ловушке',
        'unknown threat': 'неизвестная угроза',
        'unseen danger': 'невидимая опасность',
        'unseen presence': 'незримое присутствие',
        'war': 'война',
        'woman in peril': 'женщина в опасности',
        'zombie': 'зомби'
    });

    function normalizeTag(value) {
        return String(value || '')
            .trim()
            .toLocaleLowerCase('en-US')
            .replace(/[–—]/g, '-')
            .replace(/\s+/g, ' ');
    }

    function getVisiblePage() {
        return document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage:not(.hide)')
            || document;
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskTagLocalizationStyles')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'kinopoiskTagLocalizationStyles';
        style.textContent = [
            '.kp-tags-details{margin:.8em 0;border:1px solid rgba(255,255,255,.1);border-radius:.55em;background:rgba(255,255,255,.035);overflow:hidden}',
            '.kp-tags-details>summary{cursor:pointer;list-style:none;padding:.62em .78em;font-weight:650;display:flex;align-items:center;justify-content:space-between}',
            '.kp-tags-details>summary::-webkit-details-marker{display:none}',
            '.kp-tags-details>summary:after{content:"＋";opacity:.72}',
            '.kp-tags-details[open]>summary:after{content:"−"}',
            '.kp-tags-list{display:flex;gap:.42em;flex-wrap:wrap;padding:0 .78em .78em}',
            '.kp-tag-link{display:inline-flex;padding:.3em .58em;border-radius:999px;background:rgba(255,255,255,.08);color:inherit;text-decoration:none}',
            '.kp-tag-link:hover,.kp-tag-link:focus-visible{background:rgba(255,255,255,.16)}'
        ].join('');
        document.head.appendChild(style);
    }

    function createLocalizedBlock(itemTags) {
        var links = Array.prototype.slice.call(itemTags.querySelectorAll('a'));
        var localized = [];

        links.forEach(function (link) {
            var original = String(link.dataset.kpOriginalTag || link.textContent || '').trim();
            var translated = translations[normalizeTag(original)];
            if (!translated) {
                return;
            }

            var clone = link.cloneNode(false);
            clone.dataset.kpOriginalTag = original;
            clone.classList.add('kp-tag-link');
            clone.textContent = translated;
            clone.removeAttribute('title');
            clone.setAttribute('aria-label', translated);
            localized.push(clone);
        });

        if (!localized.length) {
            return null;
        }

        var details = document.createElement('details');
        details.className = 'kp-tags-details';
        details.dataset.kpTagsSource = 'standard-item-tags';
        details.appendChild(document.createElement('summary')).textContent = 'Темы и теги';

        var list = document.createElement('div');
        list.className = 'kp-tags-list';
        localized.forEach(function (link) {
            list.appendChild(link);
        });
        details.appendChild(list);
        return details;
    }

    function localizeTags() {
        ensureStyles();
        var page = getVisiblePage();
        var itemTags = page.querySelector('.itemTags');
        if (!itemTags || itemTags.classList.contains('hide')) {
            return;
        }

        var existing = page.querySelector('.kp-tags-details[data-kp-tags-source="standard-item-tags"]');
        var sourceSignature = Array.prototype.map.call(
            itemTags.querySelectorAll('a'),
            function (link) { return String(link.textContent || '').trim(); }
        ).join('|');

        if (existing && existing.dataset.kpTagsSignature === sourceSignature) {
            itemTags.hidden = true;
            return;
        }
        if (existing) {
            existing.remove();
        }

        var block = createLocalizedBlock(itemTags);
        if (!block) {
            itemTags.hidden = true;
            return;
        }

        block.dataset.kpTagsSignature = sourceSignature;
        itemTags.parentNode.insertBefore(block, itemTags);
        itemTags.hidden = true;
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(localizeTags, 250);
    }

    ensureStyles();
    var observer = new MutationObserver(scheduleRender);
    observer.observe(document.documentElement, {
        childList: true,
        subtree: true
    });
    window.addEventListener('hashchange', scheduleRender);
    window.addEventListener('popstate', scheduleRender);
    document.addEventListener('viewshow', scheduleRender, true);
    scheduleRender();
    console.info('[КиноПоиск] Визуальная локализация тегов зарегистрирована.');
}());
