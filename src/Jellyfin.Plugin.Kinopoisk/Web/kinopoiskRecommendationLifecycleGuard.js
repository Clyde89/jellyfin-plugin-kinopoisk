(function () {
    'use strict';

    if (window.__kinopoiskRecommendationLifecycleGuardInstalled) {
        return;
    }

    window.__kinopoiskRecommendationLifecycleGuardInstalled = true;

    var retryDelays = [0, 120, 280, 520, 900, 1450, 2200, 3400, 5200];
    var generation = 0;
    var activeItemId = null;
    var retryTimers = [];
    var internalDispatch = false;
    var mutationTimer = null;

    function getCurrentItemId() {
        var candidates = [window.location.search || ''];
        var hash = window.location.hash || '';
        var queryIndex = hash.indexOf('?');
        if (queryIndex >= 0) {
            candidates.push(hash.substring(queryIndex));
        }
        for (var index = 0; index < candidates.length; index++) {
            var itemId = new URLSearchParams(candidates[index]).get('id');
            if (itemId) {
                return itemId;
            }
        }
        return null;
    }

    function getVisibleDetailPage() {
        return document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.itemDetailPage:not(.hide)')
            || null;
    }

    function getAnchor(page) {
        if (!page || !page.isConnected) {
            return null;
        }
        return page.querySelector('#similarCollapsible')
            || page.querySelector('.similarCollapsible');
    }

    function getSection(page) {
        if (!page || !page.isConnected) {
            return null;
        }
        return page.querySelector('#kinopoiskRecommendationsSection');
    }

    function sectionMatches(page, itemId) {
        var section = getSection(page);
        return Boolean(
            section
            && section.isConnected
            && section.dataset.itemId === itemId
        );
    }

    function clearRetries() {
        retryTimers.forEach(function (timer) {
            clearTimeout(timer);
        });
        retryTimers = [];
    }

    function requestRecommendationRender(itemId, expectedGeneration) {
        if (expectedGeneration !== generation || getCurrentItemId() !== itemId) {
            return;
        }

        var page = getVisibleDetailPage();
        if (sectionMatches(page, itemId)) {
            clearRetries();
            return;
        }

        var anchor = getAnchor(page);
        if (!anchor || !anchor.parentNode || !anchor.isConnected) {
            return;
        }

        internalDispatch = true;
        try {
            document.dispatchEvent(new Event('viewshow'));
        } finally {
            internalDispatch = false;
        }
    }

    function startRetryCycle() {
        var itemId = getCurrentItemId();
        generation += 1;
        var expectedGeneration = generation;
        activeItemId = itemId;
        clearRetries();

        if (!itemId) {
            return;
        }

        retryDelays.forEach(function (delay) {
            retryTimers.push(setTimeout(function () {
                requestRecommendationRender(itemId, expectedGeneration);
            }, delay));
        });
    }

    function scheduleMutationCheck() {
        if (mutationTimer) {
            return;
        }
        mutationTimer = setTimeout(function () {
            mutationTimer = null;
            var itemId = getCurrentItemId();
            if (itemId !== activeItemId) {
                startRetryCycle();
                return;
            }
            if (!itemId) {
                return;
            }
            var page = getVisibleDetailPage();
            if (!sectionMatches(page, itemId) && getAnchor(page)) {
                requestRecommendationRender(itemId, generation);
            }
        }, 180);
    }

    function ensureStyles() {
        if (document.getElementById('kinopoiskRecommendationLifecycleGuardStyles')) {
            return;
        }
        var style = document.createElement('style');
        style.id = 'kinopoiskRecommendationLifecycleGuardStyles';
        style.textContent = [
            '@media(max-width:600px){',
            '.kp-recommendations-header>.kp-native-navigation.emby-scrollbuttons{',
            'margin-left:auto!important;',
            'margin-right:2.5em!important;',
            'transform:none!important',
            '}',
            '}'
        ].join('');
        document.head.appendChild(style);
    }

    ensureStyles();

    var observer = new MutationObserver(scheduleMutationCheck);
    observer.observe(document.documentElement, {
        childList: true,
        subtree: true
    });

    window.addEventListener('hashchange', startRetryCycle);
    window.addEventListener('popstate', startRetryCycle);
    document.addEventListener('viewshow', function () {
        if (!internalDispatch) {
            startRetryCycle();
        }
    }, true);

    window.setInterval(function () {
        var itemId = getCurrentItemId();
        if (!itemId) {
            return;
        }
        var page = getVisibleDetailPage();
        if (!sectionMatches(page, itemId) && getAnchor(page)) {
            startRetryCycle();
        }
    }, 2500);

    startRetryCycle();
    console.info('[КиноПоиск] Защита жизненного цикла рекомендаций зарегистрирована.');
}());
