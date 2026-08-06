(function () {
    'use strict';

    if (window.__kinopoiskRuntimeUiCorrectionsInstalled) {
        return;
    }
    window.__kinopoiskRuntimeUiCorrectionsInstalled = true;
    var renderTimer = null;
    var scrollerSequence = 0;

    function getVisibleDetailPage() {
        var pages = document.querySelectorAll('#itemDetailPage, .itemDetailPage');
        for (var index = 0; index < pages.length; index++) {
            var page = pages[index];
            if (!page.isConnected || page.hidden || page.classList.contains('hide')) {
                continue;
            }
            var style = window.getComputedStyle(page);
            if (style.display !== 'none' && style.visibility !== 'hidden') {
                return page;
            }
        }
        return null;
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskRuntimeUiCorrectionsStyles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'kinopoiskRuntimeUiCorrectionsStyles';
        style.textContent = [
            '.kp-runtime-navigation[hidden],.kp-carousel-navigation[hidden]{display:none!important}',
            '.kp-runtime-navigation-button,.kp-carousel-button{font-family:inherit!important;font-size:1.8em!important;font-weight:400!important;line-height:1!important;padding:0!important}',
            '@media(hover:hover) and (pointer:fine){.kp-recommendations-section .kp-runtime-navigation,.tmdb-reviews-section .kp-carousel-navigation,.tmdb-reviews-section .kp-runtime-navigation{opacity:0;pointer-events:none;transition:opacity .16s ease}.kp-recommendations-section:hover .kp-runtime-navigation,.kp-recommendations-section:focus-within .kp-runtime-navigation,.tmdb-reviews-section:hover .kp-carousel-navigation,.tmdb-reviews-section:focus-within .kp-carousel-navigation,.tmdb-reviews-section:hover .kp-runtime-navigation,.tmdb-reviews-section:focus-within .kp-runtime-navigation{opacity:1;pointer-events:auto}}'
        ].join('');
        document.head.appendChild(style);
    }

    function getScrollerId(scroller) {
        if (!scroller.dataset.kpUiCorrectionsScrollerId) {
            scrollerSequence += 1;
            scroller.dataset.kpUiCorrectionsScrollerId =
                'kp-ui-scroller-' + String(scrollerSequence);
        }
        return scroller.dataset.kpUiCorrectionsScrollerId;
    }

    function normalizeNavigation(navigation, scroller, labelPrefix) {
        if (!navigation || !scroller) {
            return;
        }
        var buttons = navigation.querySelectorAll('button');
        if (buttons.length < 2) {
            return;
        }
        var previous = buttons[0];
        var next = buttons[1];
        previous.classList.remove('material-icons');
        next.classList.remove('material-icons');
        previous.textContent = '‹';
        next.textContent = '›';
        previous.title = 'Предыдущие ' + labelPrefix;
        next.title = 'Следующие ' + labelPrefix;
        previous.setAttribute('aria-label', previous.title);
        next.setAttribute('aria-label', next.title);

        var bindingId = getScrollerId(scroller);
        if (navigation.dataset.kpUiCorrectionsBinding === bindingId) {
            if (typeof navigation.__kpUiCorrectionsUpdate === 'function') {
                navigation.__kpUiCorrectionsUpdate();
            }
            return;
        }
        navigation.dataset.kpUiCorrectionsBinding = bindingId;

        var pending = false;
        function update() {
            pending = false;
            var maximum = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
            var hasOverflow = maximum > 4;
            navigation.hidden = !hasOverflow;
            previous.disabled = !hasOverflow || scroller.scrollLeft <= 4;
            next.disabled = !hasOverflow || scroller.scrollLeft >= maximum - 4;
        }
        function scheduleUpdate() {
            if (!pending) {
                pending = true;
                window.requestAnimationFrame(update);
            }
        }
        scroller.addEventListener('scroll', scheduleUpdate, { passive: true });
        window.addEventListener('resize', scheduleUpdate);
        if (typeof ResizeObserver === 'function') {
            var resizeObserver = new ResizeObserver(scheduleUpdate);
            resizeObserver.observe(scroller);
            navigation.__kpUiCorrectionsResizeObserver = resizeObserver;
        }
        var mutationObserver = new MutationObserver(scheduleUpdate);
        mutationObserver.observe(scroller, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['hidden', 'style', 'class']
        });
        navigation.__kpUiCorrectionsMutationObserver = mutationObserver;
        navigation.__kpUiCorrectionsUpdate = scheduleUpdate;
        scheduleUpdate();
    }

    function patchCarousels(page) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.kp-recommendations-section'),
            function (section) {
                normalizeNavigation(
                    section.querySelector('.kp-runtime-navigation'),
                    section.querySelector('.kp-recommendations-scroller'),
                    'рекомендации'
                );
            }
        );
        Array.prototype.forEach.call(
            page.querySelectorAll('.tmdb-reviews-section'),
            function (section) {
                var toolbar = section.querySelector('.kp-review-toolbar');
                normalizeNavigation(
                    toolbar && (
                        toolbar.querySelector('.kp-carousel-navigation')
                        || toolbar.querySelector('.kp-runtime-navigation')
                    ),
                    section.querySelector('.tmdb-review-swipe-container'),
                    'рецензии'
                );
            }
        );
    }

    function renderCurrentPage() {
        var page = getVisibleDetailPage();
        if (page) {
            patchCarousels(page);
        }
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentPage, 180);
    }

    ensureStyles();
    var observer = new MutationObserver(scheduleRender);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('hashchange', scheduleRender);
    window.addEventListener('popstate', scheduleRender);
    document.addEventListener('viewshow', scheduleRender, true);
    scheduleRender();
    console.info('[КиноПоиск] Корректировки навигации зарегистрированы.');
}());
