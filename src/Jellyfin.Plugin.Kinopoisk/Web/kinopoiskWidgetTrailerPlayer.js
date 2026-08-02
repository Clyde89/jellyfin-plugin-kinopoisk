(function () {
    'use strict';

    if (window.__kinopoiskWidgetTrailerPlayerInstalled) {
        return;
    }

    window.__kinopoiskWidgetTrailerPlayerInstalled = true;

    var supportedHosts = new Set([
        'widgets.kinopoisk.ru',
        'www.youtube.com',
        'youtube.com',
        'm.youtube.com',
        'youtu.be',
        'www.youtube-nocookie.com',
        'youtube-nocookie.com',
        'disk.yandex.ru',
        'disk.yandex.com',
        'yadi.sk'
    ]);
    var bypassNativeClick = false;
    var activeDialog = null;
    var bodyOverflow = '';

    function getCurrentItemId() {
        var candidates = [window.location.search || ''];
        var hash = window.location.hash || '';
        var queryIndex = hash.indexOf('?');
        if (queryIndex >= 0) {
            candidates.push(hash.substring(queryIndex));
        }

        for (var index = 0; index < candidates.length; index++) {
            var value = new URLSearchParams(candidates[index]).get('id');
            if (value) {
                return value;
            }
        }

        return null;
    }

    function getApiClient() {
        return window.ApiClient || null;
    }

    function normalizeUrl(rawUrl) {
        if (!rawUrl) {
            return null;
        }

        try {
            var parsed = new URL(String(rawUrl), window.location.origin);
            var host = parsed.hostname.toLowerCase();
            if (!supportedHosts.has(host) || (parsed.protocol !== 'https:' && parsed.protocol !== 'http:')) {
                return null;
            }

            parsed.protocol = 'https:';
            parsed.username = '';
            parsed.password = '';
            parsed.hash = '';
            return parsed;
        } catch (error) {
            console.warn('[КиноПоиск] Некорректная ссылка трейлера отклонена.', error);
            return null;
        }
    }

    function normalizeYoutubeEmbed(parsed) {
        var host = parsed.hostname.toLowerCase();
        var videoId = null;

        if (host === 'youtu.be') {
            videoId = parsed.pathname.split('/').filter(Boolean)[0] || null;
        } else {
            var parts = parsed.pathname.split('/').filter(Boolean);
            if (parts.length > 1 && ['embed', 'v', 'shorts', 'live'].indexOf(parts[0].toLowerCase()) >= 0) {
                videoId = parts[1];
            } else {
                videoId = parsed.searchParams.get('v');
            }
        }

        if (!videoId || !/^[A-Za-z0-9_-]{6,20}$/.test(videoId)) {
            return null;
        }

        return {
            kind: 'youtube',
            sourceUrl: parsed.href,
            playerUrl: 'https://www.youtube-nocookie.com/embed/'
                + encodeURIComponent(videoId)
                + '?autoplay=1&rel=0&modestbranding=1&playsinline=1'
        };
    }

    function normalizeWidget(parsed) {
        if (parsed.hostname.toLowerCase() !== 'widgets.kinopoisk.ru') {
            return null;
        }

        parsed.searchParams.set('onlyPlayer', '1');
        parsed.searchParams.set('autoplay', '1');
        parsed.searchParams.set('cover', '1');
        return {
            kind: 'kinopoisk-widget',
            sourceUrl: parsed.href,
            playerUrl: parsed.href
        };
    }

    function normalizeExternal(parsed) {
        var host = parsed.hostname.toLowerCase();
        if (host !== 'disk.yandex.ru' && host !== 'disk.yandex.com' && host !== 'yadi.sk') {
            return null;
        }

        return {
            kind: 'external',
            sourceUrl: parsed.href,
            playerUrl: parsed.href
        };
    }

    function normalizeTrailer(item, index) {
        var parsed = normalizeUrl(item && item.Url);
        if (!parsed) {
            return null;
        }

        var normalized = normalizeWidget(parsed)
            || normalizeYoutubeEmbed(parsed)
            || normalizeExternal(parsed);
        if (!normalized) {
            return null;
        }

        normalized.name = (item && item.Name) || ('Трейлер ' + String(index + 1));
        return normalized;
    }

    function getRemoteTrailers(item) {
        var trailers = Array.isArray(item && item.RemoteTrailers)
            ? item.RemoteTrailers
            : [];

        return trailers
            .map(normalizeTrailer)
            .filter(Boolean)
            .sort(function (left, right) {
                var priority = {
                    'kinopoisk-widget': 0,
                    'youtube': 1,
                    'external': 2
                };
                return priority[left.kind] - priority[right.kind];
            });
    }

    function fetchCurrentItem() {
        var apiClient = getApiClient();
        var itemId = getCurrentItemId();
        if (!apiClient || !itemId || typeof apiClient.getItem !== 'function') {
            return Promise.resolve(null);
        }

        return apiClient.getItem(apiClient.getCurrentUserId(), itemId)
            .catch(function (error) {
                console.warn('[КиноПоиск] Не удалось получить карточку для запуска трейлера.', error);
                return null;
            });
    }

    function restoreNativeClick(button) {
        bypassNativeClick = true;
        try {
            button.click();
        } finally {
            bypassNativeClick = false;
        }
    }

    function closeDialog() {
        if (!activeDialog) {
            return;
        }

        var frame = activeDialog.querySelector('iframe');
        if (frame) {
            frame.src = 'about:blank';
        }

        activeDialog.remove();
        activeDialog = null;
        document.body.style.overflow = bodyOverflow;
        document.removeEventListener('keydown', onKeyDown, true);
    }

    function onKeyDown(event) {
        if (event.key === 'Escape') {
            event.preventDefault();
            closeDialog();
        }
    }

    function createButton(text, title) {
        var button = document.createElement('button');
        button.type = 'button';
        button.textContent = text;
        button.title = title || text;
        button.style.border = '0';
        button.style.borderRadius = '0.45rem';
        button.style.padding = '0.55rem 0.8rem';
        button.style.background = 'rgba(255,255,255,0.14)';
        button.style.color = '#fff';
        button.style.cursor = 'pointer';
        button.style.fontSize = '0.95rem';
        return button;
    }

    function showDialog(trailers, initialIndex) {
        closeDialog();

        var index = Math.max(0, Math.min(initialIndex || 0, trailers.length - 1));
        var overlay = document.createElement('div');
        overlay.id = 'kinopoiskWidgetTrailerDialog';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.style.position = 'fixed';
        overlay.style.inset = '0';
        overlay.style.zIndex = '100000';
        overlay.style.background = 'rgba(0,0,0,0.92)';
        overlay.style.display = 'flex';
        overlay.style.flexDirection = 'column';

        var toolbar = document.createElement('div');
        toolbar.style.display = 'flex';
        toolbar.style.alignItems = 'center';
        toolbar.style.gap = '0.5rem';
        toolbar.style.padding = '0.65rem';
        toolbar.style.background = 'rgba(18,18,24,0.96)';

        var title = document.createElement('div');
        title.style.flex = '1';
        title.style.minWidth = '0';
        title.style.overflow = 'hidden';
        title.style.textOverflow = 'ellipsis';
        title.style.whiteSpace = 'nowrap';
        title.style.color = '#fff';
        title.style.fontWeight = '600';

        var previous = createButton('‹', 'Предыдущий трейлер');
        var next = createButton('›', 'Следующий трейлер');
        var external = createButton('Открыть отдельно', 'Открыть трейлер в новой вкладке');
        var close = createButton('Закрыть', 'Закрыть трейлер');

        var content = document.createElement('div');
        content.style.position = 'relative';
        content.style.flex = '1';
        content.style.minHeight = '0';

        var frame = document.createElement('iframe');
        frame.style.position = 'absolute';
        frame.style.inset = '0';
        frame.style.width = '100%';
        frame.style.height = '100%';
        frame.style.border = '0';
        frame.style.background = '#000';
        frame.setAttribute('allow', 'autoplay; fullscreen; encrypted-media; picture-in-picture');
        frame.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
        frame.setAttribute('allowfullscreen', '');

        var hint = document.createElement('div');
        hint.style.position = 'absolute';
        hint.style.left = '50%';
        hint.style.bottom = '1rem';
        hint.style.transform = 'translateX(-50%)';
        hint.style.padding = '0.45rem 0.75rem';
        hint.style.borderRadius = '0.4rem';
        hint.style.background = 'rgba(0,0,0,0.72)';
        hint.style.color = '#fff';
        hint.style.fontSize = '0.85rem';
        hint.style.pointerEvents = 'none';
        hint.textContent = 'Если виджет заблокирован браузером, нажмите «Открыть отдельно».';

        function render() {
            var trailer = trailers[index];
            title.textContent = trailer.name + ' — КиноПоиск';
            previous.disabled = trailers.length < 2;
            next.disabled = trailers.length < 2;
            previous.style.opacity = previous.disabled ? '0.45' : '1';
            next.style.opacity = next.disabled ? '0.45' : '1';

            if (trailer.kind === 'external') {
                window.open(trailer.sourceUrl, '_blank', 'noopener,noreferrer');
                closeDialog();
                return;
            }

            frame.src = trailer.playerUrl;
        }

        previous.addEventListener('click', function () {
            index = (index - 1 + trailers.length) % trailers.length;
            render();
        });
        next.addEventListener('click', function () {
            index = (index + 1) % trailers.length;
            render();
        });
        external.addEventListener('click', function () {
            window.open(trailers[index].sourceUrl, '_blank', 'noopener,noreferrer');
        });
        close.addEventListener('click', closeDialog);
        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) {
                closeDialog();
            }
        });

        toolbar.append(previous, next, title, external, close);
        content.append(frame, hint);
        overlay.append(toolbar, content);

        bodyOverflow = document.body.style.overflow;
        document.body.style.overflow = 'hidden';
        document.body.appendChild(overlay);
        activeDialog = overlay;
        document.addEventListener('keydown', onKeyDown, true);
        close.focus();
        render();
    }

    function onTrailerClick(event) {
        var target = event.target;
        var button = target && target.closest
            ? target.closest('.btnPlayTrailer')
            : null;

        if (!button || bypassNativeClick) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation();

        fetchCurrentItem().then(function (item) {
            var trailers = getRemoteTrailers(item);
            var hasKinopoiskWidget = trailers.some(function (trailer) {
                return trailer.kind === 'kinopoisk-widget';
            });

            if (!hasKinopoiskWidget) {
                restoreNativeClick(button);
                return;
            }

            showDialog(trailers, 0);
        });
    }

    document.addEventListener('click', onTrailerClick, true);
    console.info('[КиноПоиск] Веб-плеер трейлеров КиноПоиска зарегистрирован.');
}());
