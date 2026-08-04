(function () {
    'use strict';

    if (window.__kinopoiskRuntimeNativeStyleInstalled) {
        return;
    }

    window.__kinopoiskRuntimeNativeStyleInstalled = true;

    var renderTimer = null;
    var sequence = 0;

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

    function ensureStyles() {
        if (document.getElementById('kinopoiskRuntimeNativeStyle')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'kinopoiskRuntimeNativeStyle';
        style.textContent = [
            '.kp-runtime-navigation,.kp-carousel-navigation{display:none!important}',
            '.kp-native-navigation[hidden]{display:none!important}',
            '.kp-native-navigation.emby-scrollbuttons{position:static!important;top:auto!important;right:auto!important;left:auto!important;min-width:auto!important;min-height:0!important;padding:0!important;margin-left:auto!important;display:flex;align-items:center;justify-content:center;color:inherit;opacity:1!important;pointer-events:auto!important}',
            '.kp-native-navigation .emby-scrollbuttons-button{display:inline-flex;align-items:center;justify-content:center;margin:0;padding:.45em;border:0;background:transparent;color:inherit;cursor:pointer}',
            '.kp-native-navigation .emby-scrollbuttons-button>.material-icons{display:block;min-width:24px;min-height:24px;font-size:1.7em;line-height:1}',
            '.kp-native-navigation .emby-scrollbuttons-button:disabled{opacity:.3;cursor:default}',
            '.kp-native-navigation .emby-scrollbuttons-button:not(:disabled):hover,.kp-native-navigation .emby-scrollbuttons-button:not(:disabled):focus-visible{background:rgba(255,255,255,.12);outline:0}',
            '.kp-recommendations-header,.kp-review-toolbar{position:relative}',
            '.kp-runtime-release-row.kp-native-release-row{display:flex;align-items:center;flex-wrap:wrap;gap:.65em;width:100%;margin:.15em 0 0;padding:0!important;border:0!important;border-radius:0!important;background:transparent!important;box-shadow:none!important}',
            '.kp-native-release-row .kp-runtime-release-date{display:flex;align-items:center;flex-wrap:wrap;gap:.85em;width:auto;max-width:100%;margin:0;padding:0!important;border:0!important;border-radius:0!important;background:transparent!important;box-shadow:none!important}',
            '.kp-native-release-row .kp-runtime-release-date-entry{display:inline-flex;align-items:center;gap:.3em;white-space:nowrap}',
            '.kp-native-release-row .kp-runtime-release-date-entry>.material-icons{font-size:1.2em}',
            '.kp-native-release-row .kp-runtime-release-date-label{font-weight:650}',
            '.kp-native-release-row .kp-runtime-release-date-value.is-missing{opacity:.58}',
            '.kp-native-release-row .kp-runtime-release-date-more{display:inline-flex;align-items:center;justify-content:center;margin:0;padding:.12em .28em;border:0;border-radius:50%;background:transparent;color:inherit;cursor:pointer}',
            '.kp-native-release-row .kp-runtime-release-date-more:hover,.kp-native-release-row .kp-runtime-release-date-more:focus-visible{background:rgba(255,255,255,.12);outline:0}',
            '@media(max-width:600px){.kp-native-release-row .kp-runtime-release-date-label{display:none}.kp-native-release-row .kp-runtime-release-date{gap:.55em}}'
        ].join('');
        document.head.appendChild(style);
    }

    function getScrollerId(scroller) {
        if (!scroller.dataset.kpNativeScrollerId) {
            sequence += 1;
            scroller.dataset.kpNativeScrollerId = 'kp-native-scroller-' + String(sequence);
        }
        return scroller.dataset.kpNativeScrollerId;
    }

    function scrollCarousel(scroller, direction) {
        var distance = Math.max(320, Math.floor(scroller.clientWidth * .86));
        scroller.scrollBy({ left: direction * distance, behavior: 'smooth' });
    }

    function createScrollButton(direction, label) {
        var button = createElement(
            'button',
            'emby-scrollbuttons-button paper-icon-button-light'
        );
        button.type = 'button';
        button.setAttribute('is', 'paper-icon-button-light');
        button.setAttribute('data-ripple', 'false');
        button.setAttribute('data-direction', direction);
        button.title = label;
        button.setAttribute('aria-label', label);
        button.appendChild(createElement(
            'span',
            'material-icons ' + (direction === 'left' ? 'chevron_left' : 'chevron_right')
        ));
        button.firstChild.setAttribute('aria-hidden', 'true');
        return button;
    }

    function removeLegacyNavigation(header) {
        Array.prototype.forEach.call(
            header.querySelectorAll('.kp-runtime-navigation,.kp-carousel-navigation'),
            function (navigation) {
                navigation.hidden = true;
                navigation.setAttribute('aria-hidden', 'true');
            }
        );
    }

    function bindNavigation(header, scroller, labelPrefix) {
        if (!header || !scroller) {
            return;
        }

        removeLegacyNavigation(header);
        var bindingId = getScrollerId(scroller);
        var navigation = header.querySelector('.kp-native-navigation');
        if (navigation && navigation.dataset.kpNativeBinding !== bindingId) {
            navigation.remove();
            navigation = null;
        }

        if (!navigation) {
            navigation = createElement(
                'div',
                'emby-scrollbuttons kp-native-navigation'
            );
            navigation.dataset.kpNativeBinding = bindingId;

            var previous = createScrollButton(
                'left',
                'Предыдущие ' + labelPrefix
            );
            var next = createScrollButton(
                'right',
                'Следующие ' + labelPrefix
            );
            navigation.append(previous, next);
            header.appendChild(navigation);

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

            if (!scroller.hasAttribute('tabindex')) {
                scroller.tabIndex = 0;
            }
            if (scroller.dataset.kpNativeKeyboard !== 'true') {
                scroller.dataset.kpNativeKeyboard = 'true';
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

            var pending = false;
            function update() {
                pending = false;
                var maximum = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
                var hasOverflow = maximum > 20;
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
                navigation.__kpNativeResizeObserver = resizeObserver;
            }
            var mutationObserver = new MutationObserver(scheduleUpdate);
            mutationObserver.observe(scroller, {
                childList: true,
                subtree: true,
                attributes: true,
                attributeFilter: ['hidden', 'style', 'class']
            });
            navigation.__kpNativeMutationObserver = mutationObserver;
            navigation.__kpNativeUpdate = scheduleUpdate;
            scheduleUpdate();
        } else if (typeof navigation.__kpNativeUpdate === 'function') {
            navigation.__kpNativeUpdate();
        }
    }

    function patchCarousels(page) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.kp-recommendations-section'),
            function (section) {
                bindNavigation(
                    section.querySelector('.kp-recommendations-header'),
                    section.querySelector('.kp-recommendations-scroller'),
                    'рекомендации'
                );
            }
        );

        Array.prototype.forEach.call(
            page.querySelectorAll('.tmdb-reviews-section'),
            function (section) {
                bindNavigation(
                    section.querySelector('.kp-review-toolbar'),
                    section.querySelector('.tmdb-review-swipe-container'),
                    'рецензии'
                );
            }
        );
    }

    function patchReleaseRow(page) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.kp-runtime-release-row'),
            function (row) {
                row.classList.add('kp-native-release-row');
                Array.prototype.forEach.call(
                    row.querySelectorAll('.kp-runtime-release-date-entry'),
                    function (entry) {
                        var label = entry.querySelector('.kp-runtime-release-date-label');
                        var value = entry.querySelector('.kp-runtime-release-date-value');
                        var labelText = String(label && label.textContent || 'Дата релиза').trim();
                        var valueText = String(value && value.textContent || '—').trim();
                        var title = labelText
                            + ': '
                            + (valueText === '—' ? 'дата отсутствует' : valueText)
                            + ' · источник: TMDB';
                        entry.title = title;
                        entry.setAttribute('aria-label', title);
                        entry.dataset.source = 'TMDB';
                    }
                );

                var calendar = row.querySelector('.kp-runtime-release-date-more');
                if (calendar) {
                    calendar.classList.add('paper-icon-button-light');
                    calendar.title = 'Все даты релиза · источники: TMDB и КиноПоиск';
                    calendar.setAttribute('aria-label', calendar.title);
                }
            }
        );
    }

    function renderCurrentPage() {
        var page = document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage:not(.hide)')
            || document;
        patchCarousels(page);
        patchReleaseRow(page);
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(renderCurrentPage, 140);
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
    console.info('[КиноПоиск] Штатный стиль runtime-интерфейса зарегистрирован.');
}());
