(function () {
    'use strict';

    if (window.__kinopoiskRecommendationScrollbarStyleInstalled) {
        return;
    }

    window.__kinopoiskRecommendationScrollbarStyleInstalled = true;

    var style = document.createElement('style');
    style.id = 'kinopoiskRecommendationScrollbarStyle';
    style.textContent = [
        '.kp-recommendations-scroller{-ms-overflow-style:none!important;scrollbar-width:none!important}',
        '.kp-recommendations-scroller::-webkit-scrollbar{display:none!important;width:0!important;height:0!important}'
    ].join('');
    document.head.appendChild(style);

    console.info('[КиноПоиск] Полоса прокрутки рекомендаций скрыта.');
}());
