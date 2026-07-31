using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Содержит результат предварительного построения франшиз.
    /// </summary>
    public sealed class KinopoiskFranchisePreviewResult
    {
        /// <summary>
        /// Получает или задаёт количество уникальных локальных объектов.
        /// </summary>
        public int LocalItemCount { get; set; }

        /// <summary>
        /// Получает или задаёт количество обработанных идентификаторов.
        /// </summary>
        public int ProcessedItemCount { get; set; }

        /// <summary>
        /// Получает или задаёт количество неудачных запросов связей.
        /// </summary>
        public int FailedRequestCount { get; set; }

        /// <summary>
        /// Получает или задаёт подготовленные планы коллекций.
        /// </summary>
        public IReadOnlyList<KinopoiskFranchisePlan> Plans { get; set; }
            = Array.Empty<KinopoiskFranchisePlan>();
    }

    /// <summary>
    /// Подготавливает read-only план франшиз без изменения медиатеки Jellyfin.
    /// </summary>
    public sealed class KinopoiskFranchisePreviewService
    {
        private readonly IKinopoiskRelationsApiClient _relationsApiClient;
        private readonly KinopoiskFranchisePlanner _planner;
        private readonly ILogger<KinopoiskFranchisePreviewService> _logger;

        /// <summary>
        /// Инициализирует сервис предварительного просмотра.
        /// </summary>
        public KinopoiskFranchisePreviewService(
            IKinopoiskRelationsApiClient relationsApiClient,
            KinopoiskFranchisePlanner planner,
            ILogger<KinopoiskFranchisePreviewService> logger)
        {
            _relationsApiClient = relationsApiClient
                ?? throw new ArgumentNullException(nameof(relationsApiClient));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Последовательно загружает связи локальных объектов и строит read-only план.
        /// </summary>
        public async Task<KinopoiskFranchisePreviewResult> BuildPreview(
            IEnumerable<KinopoiskFranchiseLibraryItem> libraryItems,
            KinopoiskFranchisePlannerOptions options = null,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(libraryItems);
            var items = libraryItems
                .Where(item => item is not null && item.ItemId != Guid.Empty && item.KinopoiskId > 0)
                .GroupBy(item => item.KinopoiskId)
                .Select(group => group.First())
                .OrderBy(item => item.KinopoiskId)
                .ToArray();
            var relations = new Dictionary<int, IReadOnlyCollection<FilmSequelsAndPrequelsResponse>>();
            var failedRequests = 0;

            for (var index = 0; index < items.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[index];
                try
                {
                    var result = await _relationsApiClient
                        .GetRelations(item.KinopoiskId, cancellationToken)
                        .ConfigureAwait(false);
                    relations[item.KinopoiskId] = result?
                        .Where(relation => relation is not null)
                        .ToArray()
                        ?? Array.Empty<FilmSequelsAndPrequelsResponse>();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedRequests++;
                    _logger.LogWarning(
                        exception,
                        "Связи Kinopoisk ID {KinopoiskId} не включены в предварительный план",
                        item.KinopoiskId);
                }

                progress?.Report(items.Length == 0
                    ? 100
                    : ((index + 1d) / items.Length) * 100d);
            }

            return new KinopoiskFranchisePreviewResult
            {
                LocalItemCount = items.Length,
                ProcessedItemCount = items.Length,
                FailedRequestCount = failedRequests,
                Plans = _planner.Build(items, relations, options)
            };
        }
    }
}
