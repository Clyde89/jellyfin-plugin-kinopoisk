#nullable enable

using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Регистрирует LocalTrailer и запускает фоновую подготовку MP4 при первом
    /// открытии карточки фильма через стандартный API Jellyfin.
    /// </summary>
    internal sealed class KinopoiskNativeTrailerLazyPrepareMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly KinopoiskNativeTrailerBridge _bridge;
        private readonly IKinopoiskNativeTrailerCacheWarmupService _warmupService;
        private readonly ILogger<KinopoiskNativeTrailerLazyPrepareMiddleware> _logger;

        public KinopoiskNativeTrailerLazyPrepareMiddleware(
            RequestDelegate next,
            IAuthorizationContext authorizationContext,
            KinopoiskNativeTrailerBridge bridge,
            IKinopoiskNativeTrailerCacheWarmupService warmupService,
            ILogger<KinopoiskNativeTrailerLazyPrepareMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _authorizationContext = authorizationContext
                ?? throw new ArgumentNullException(nameof(authorizationContext));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _warmupService = warmupService
                ?? throw new ArgumentNullException(nameof(warmupService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!ShouldPrepare(context.Request, out var movieId))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var configuration = Plugin.Instance?.Configuration;
            if (configuration?.EnableTrailers == false
                || configuration?.EnableNativeTrailerCache == false)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            try
            {
                var authorization = await _authorizationContext
                    .GetAuthorizationInfo(context)
                    .ConfigureAwait(false);
                if (!authorization.IsAuthenticated
                    || !IsClientAllowed(
                        authorization.Client,
                        configuration?.NativeTrailerCacheClientScope
                            ?? KinopoiskTrailerCacheClientScope.AllClients))
                {
                    await _next(context).ConfigureAwait(false);
                    return;
                }

                var kinopoiskId = await _bridge
                    .EnsureLazyRegistered(movieId, context.RequestAborted)
                    .ConfigureAwait(false);
                if (kinopoiskId.HasValue)
                    _warmupService.Start(kinopoiskId.Value);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Ленивая подготовка LocalTrailer для элемента {MovieId} не выполнена; запрос Jellyfin продолжен без изменения",
                    movieId);
            }

            await _next(context).ConfigureAwait(false);
        }

        internal static bool ShouldPrepare(HttpRequest request, out Guid movieId)
        {
            movieId = Guid.Empty;
            if (!HttpMethods.IsGet(request.Method))
                return false;

            var segments = request.Path.Value?
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
            if (segments.Length >= 3
                && string.Equals(segments[^3], "Items", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[^2], out movieId)
                && string.Equals(
                    segments[^1],
                    "LocalTrailers",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (segments.Length >= 2
                && string.Equals(segments[^2], "Items", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[^1], out movieId))
            {
                return true;
            }

            if (segments.Length >= 4
                && string.Equals(segments[^4], "Users", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[^3], out _)
                && string.Equals(segments[^2], "Items", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[^1], out movieId))
            {
                return true;
            }

            movieId = Guid.Empty;
            return false;
        }

        internal static bool IsClientAllowed(
            string? client,
            KinopoiskTrailerCacheClientScope scope)
            => scope == KinopoiskTrailerCacheClientScope.AllClients
                || (!string.IsNullOrWhiteSpace(client)
                    && client.Contains("Android TV", StringComparison.OrdinalIgnoreCase));
    }
}
