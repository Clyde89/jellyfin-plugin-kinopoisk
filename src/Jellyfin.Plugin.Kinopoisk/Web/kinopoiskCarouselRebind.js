(function () {
    'use strict';

    if (window.__kinopoiskCarouselRebindInstalled) {
        return;
    }

    window.__kinopoiskCarouselRebindInstalled = true;

    var sequence = 0;
    var timer = null;

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

    function getScrollerId(scroller) {
        if (!scroller.dataset.kpScrollerId) {
            sequence += 1;
            scroller.dataset.kpScrollerId = 'kp-scroller-' + String(sequence);
        }
        return scroller.dataset.kpScrollerId;
    }

    function scrollCarousel(scroller, direction) {
        var distance = Math.max(320, Math.floor(scroller.clientWidth * 0.86));
        scroller.scrollBy({ left: direction * distance, behavior: 'smooth' });
    }

    function bindRecommendationSection(section) {
        var header = section.querySelector('.kp-recommendations-header');
        var scroller = section.querySelector('.kp-recommendations-scroller');
        if (!header || !scroller) {
            return;
        }

        var scrollerId = getScrollerId(scroller);
        var navigation = header.querySelector('.kp-runtime-navigation');
        if (navigation && navigation.dataset.kpBoundScrollerId === scrollerId) {
            if (typeof navigation.__kpUpdate === 'function') {
                navigation.__kpUpdate();
            }
            return;
        }
        if (navigation) {
            navigation.remove();
        }

        navigation = createElement('div', 'kp-runtime-navigation');
        navigation.dataset.kpBoundScrollerId = scrollerId;
        var previous = createElement('button', 'kp-runtime-navigation-button material-icons', 'chevron_left');
        var next = createElement('button', 'kp-runtime-navigation-button material-icons', 'chevron_right');
        previous.type = 'button';
        next.type = 'button';
        previous.title = 'Предыдущие рекомендации';
        next.title = 'Следующие рекомендации';
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
        scheduleUpdate();
    }

    function bindCurrentCarousels() {
        Array.prototype.forEach.call(
            document.querySelectorAll('.kp-recommendations-section'),
            bindRecommendationSection
        );
    }

    function scheduleBind() {
        clearTimeout(timer);
        timer = setTimeout(bindCurrentCarousels, 120);
    }

    var observer = new MutationObserver(scheduleBind);
    observer.observe(document.documentElement, {
        childList: true,
        subtree: true
    });
    window.addEventListener('hashchange', scheduleBind);
    window.addEventListener('popstate', scheduleBind);
    document.addEventListener('viewshow', scheduleBind, true);
    scheduleBind();
}());
