(function () {
    'use strict';

    if (window.__kinopoiskRuntimePolishInstalled) {
        return;
    }
    window.__kinopoiskRuntimePolishInstalled = true;
    var renderTimer = null;

    function createElement(tagName, className, text) {
        var element = document.createElement(tagName);
        if (className) {
            element.className = className;
        }
        if (text != null) {
            element.textContent = text;
        }
        return element;
    }

    function getVisibleDetailPage() {
        var lifecycle = window.KinopoiskDetailPageLifecycle;
        if (lifecycle) {
            return lifecycle.getActivePage();
        }
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
        if (document.getElementById('kinopoiskRuntimePolishStyles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'kinopoiskRuntimePolishStyles';
        style.textContent = [
            '.kp-runtime-navigation{display:flex;align-items:center;gap:.28em;margin-left:auto}',
            '.kp-runtime-navigation-button{display:inline-flex;align-items:center;justify-content:center;width:2.45em;height:2.45em;border:0;border-radius:50%;background:rgba(0,0,0,.52);color:inherit;cursor:pointer;font:inherit}',
            '.kp-runtime-navigation-button:hover,.kp-runtime-navigation-button:focus-visible{background:rgba(0,0,0,.78);outline:2px solid rgba(255,255,255,.65);outline-offset:2px}',
            '.kp-runtime-navigation-button:disabled{opacity:.28;cursor:default;outline:0}',
            '.kp-recommendations-scroller,.tmdb-review-swipe-container{scrollbar-width:none;scroll-snap-type:x proximity;overscroll-behavior-inline:contain}',
            '.kp-recommendations-scroller::-webkit-scrollbar,.tmdb-review-swipe-container::-webkit-scrollbar{display:none}',
            '.kp-recommendations-items>* ,.tmdb-review-swipe-container>*{scroll-snap-align:start}'
        ].join('');
        document.head.appendChild(style);
    }

    function scrollCarousel(scroller, direction) {
        var distance = Math.max(320, Math.floor(scroller.clientWidth * .86));
        scroller.scrollBy({ left: direction * distance, behavior: 'smooth' });
    }

    function attachKeyboardNavigation(scroller) {
        if (scroller.dataset.kpKeyboardNavigation === 'true') {
            return;
        }
        scroller.dataset.kpKeyboardNavigation = 'true';
        if (!scroller.hasAttribute('tabindex')) {
            scroller.tabIndex = 0;
        }
        scroller.addEventListener('keydown', function (event) {
            if (event.key === 'ArrowLeft') {
                event.preventDefault();
                scrollCarousel(scroller, -1);
            } else if (event.key === 'ArrowRight') {
                event.preventDefault();
                scrollCarousel(scroller, 1);
            }
        });
    }

    function ensureNavigation(header, scroller, labelPrefix) {
        if (!header || !scroller) {
            return;
        }
        attachKeyboardNavigation(scroller);
        var navigation = header.querySelector('.kp-runtime-navigation');
        if (!navigation) {
            navigation = createElement('div', 'kp-runtime-navigation');
            var previous = createElement(
                'button',
                'kp-runtime-navigation-button material-icons',
                'chevron_left'
            );
            var next = createElement(
                'button',
                'kp-runtime-navigation-button material-icons',
                'chevron_right'
            );
            previous.type = 'button';
            next.type = 'button';
            previous.title = 'Предыдущие ' + labelPrefix;
            next.title = 'Следующие ' + labelPrefix;
            previous.setAttribute('aria-label', previous.title);
            next.setAttribute('aria-label', next.title);
            previous.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                scrollCarousel(scroller, -1);
            });
            next.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                scrollCarousel(scroller, 1);
            });
            navigation.append(previous, next);
            header.appendChild(navigation);

            var pending = false;
            function update() {
                pending = false;
                var maximum = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
                previous.disabled = scroller.scrollLeft <= 4;
                next.disabled = maximum <= 4 || scroller.scrollLeft >= maximum - 4;
            }
            function scheduleUpdate() {
                if (!pending) {
                    pending = true;
                    window.requestAnimationFrame(update);
                }
            }
            scroller.addEventListener('scroll', scheduleUpdate, { passive: true });
            window.addEventListener('resize', scheduleUpdate);
            navigation.__kpUpdate = scheduleUpdate;
        }
        if (typeof navigation.__kpUpdate === 'function') {
            navigation.__kpUpdate();
        }
    }

    function enhanceCarousels(page) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.kp-recommendations-section'),
            function (section) {
                ensureNavigation(
                    section.querySelector('.kp-recommendations-header'),
                    section.querySelector('.kp-recommendations-scroller'),
                    'рекомендации'
                );
            }
        );
        Array.prototype.forEach.call(
            page.querySelectorAll('.tmdb-reviews-section'),
            function (section) {
                var toolbar = section.querySelector('.kp-review-toolbar');
                var scroller = section.querySelector('.tmdb-review-swipe-container');
                if (toolbar && scroller && !toolbar.querySelector('.kp-carousel-navigation')) {
                    ensureNavigation(toolbar, scroller, 'рецензии');
                }
            }
        );
    }

    function renderCurrentPage() {
        var page = getVisibleDetailPage();
        if (page) {
            enhanceCarousels(page);
        }
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentPage, 150);
    }

    ensureStyles();
    var lifecycle = window.KinopoiskDetailPageLifecycle;
    if (lifecycle) {
        lifecycle.subscribe(scheduleRender);
    } else {
        scheduleRender();
    }
    console.info('[КиноПоиск] Полировка каруселей зарегистрирована.');
}());
