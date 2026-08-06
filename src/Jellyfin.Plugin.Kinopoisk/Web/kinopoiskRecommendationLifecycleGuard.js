(function () {
    'use strict';

    if (window.__kinopoiskRecommendationLifecycleGuardInstalled) {
        return;
    }
    window.__kinopoiskRecommendationLifecycleGuardInstalled = true;
    var alignTimer = null;

    function isVisible(element) {
        if (!element || !element.isConnected || element.hidden) {
            return false;
        }
        var style = window.getComputedStyle(element);
        var rect = element.getBoundingClientRect();
        return style.display !== 'none'
            && style.visibility !== 'hidden'
            && rect.width > 0
            && rect.height > 0;
    }

    function getVisibleDetailPage() {
        var pages = document.querySelectorAll('#itemDetailPage, .itemDetailPage');
        for (var index = 0; index < pages.length; index++) {
            if (isVisible(pages[index]) && !pages[index].classList.contains('hide')) {
                return pages[index];
            }
        }
        return null;
    }

    function findReferenceNavigation(section) {
        var previous = section.previousElementSibling;
        var direct = previous && previous.querySelector(
            '.emby-scrollbuttons:not(.kp-native-navigation)'
        );
        if (isVisible(direct)) {
            return direct;
        }
        var page = getVisibleDetailPage();
        if (!page) {
            return null;
        }
        var sectionTop = section.getBoundingClientRect().top;
        var candidates = Array.prototype.filter.call(
            page.querySelectorAll('.emby-scrollbuttons:not(.kp-native-navigation)'),
            function (candidate) {
                return !section.contains(candidate) && isVisible(candidate);
            }
        );
        candidates.sort(function (left, right) {
            var leftRect = left.getBoundingClientRect();
            var rightRect = right.getBoundingClientRect();
            var leftScore = (leftRect.top > sectionTop ? 100000 : 0)
                + Math.abs(sectionTop - leftRect.top);
            var rightScore = (rightRect.top > sectionTop ? 100000 : 0)
                + Math.abs(sectionTop - rightRect.top);
            return leftScore - rightScore;
        });
        return candidates[0] || null;
    }

    function alignSection(section) {
        var header = section.querySelector('.kp-recommendations-header');
        var navigation = section.querySelector('.kp-native-navigation');
        if (!header || !navigation || !isVisible(navigation)) {
            return;
        }
        var reference = findReferenceNavigation(section);
        if (!reference) {
            navigation.style.removeProperty('--kp-native-navigation-left');
            navigation.dataset.kpAlignmentSource = 'fallback';
            return;
        }
        var offset = Math.max(0, Math.round(
            reference.getBoundingClientRect().left
            - header.getBoundingClientRect().left
        ));
        navigation.style.setProperty(
            '--kp-native-navigation-left',
            String(offset) + 'px'
        );
        navigation.dataset.kpAlignmentSource = 'native-carousel';
    }

    function alignAll() {
        alignTimer = null;
        Array.prototype.forEach.call(
            document.querySelectorAll('.kp-recommendations-section'),
            alignSection
        );
    }

    function scheduleAlignment() {
        if (!alignTimer) {
            alignTimer = setTimeout(alignAll, 120);
        }
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskRecommendationLifecycleGuardStyles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'kinopoiskRecommendationLifecycleGuardStyles';
        style.textContent = [
            '@media(max-width:900px){',
            '.kp-recommendations-header>.kp-native-navigation.emby-scrollbuttons{',
            'flex:0 0 100%!important;',
            'width:100%!important;',
            'box-sizing:border-box!important;',
            'margin:0!important;',
            'padding:0 0 0 var(--kp-native-navigation-left,2.5em)!important;',
            'display:flex!important;',
            'justify-content:flex-start!important;',
            'transform:none!important',
            '}',
            '}'
        ].join('');
        document.head.appendChild(style);
    }

    ensureStyles();
    var observer = new MutationObserver(scheduleAlignment);
    observer.observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('resize', scheduleAlignment);
    window.addEventListener('hashchange', scheduleAlignment);
    window.addEventListener('popstate', scheduleAlignment);
    document.addEventListener('viewshow', scheduleAlignment, true);
    scheduleAlignment();
    console.info('[КиноПоиск] Геометрическое выравнивание навигации зарегистрировано.');
}());
