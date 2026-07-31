using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Содержит итог применения одного плана франшизы.
    /// </summary>
    public sealed class KinopoiskFranchiseApplyItemResult
    {
        public int AnchorKinopoiskId { get; set; }
        public string SuggestedName { get; set; } = string.Empty;
        public Guid? CollectionId { get; set; }
        public string Action { get; set; } = string.Empty;
        public int AddedItemCount { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// Содержит сводный результат безопасного применения планов.
    /// </summary>
    public sealed class KinopoiskFranchiseApplyResult
    {
        public int PlanCount { get; set; }
        public int CreatedCollectionCount { get; set; }
        public int UpdatedCollectionCount { get; set; }
        public int UnchangedCollectionCount { get; set; }
        public int ConflictCount { get; set; }
        public int FailureCount { get; set; }
        public int AddedItemCount { get; set; }
        public IReadOnlyList<KinopoiskFranchiseApplyItemResult> Items { get; set; }
            = Array.Empty<KinopoiskFranchiseApplyItemResult>();
    }

    /// <summary>
    /// Создаёт новые помеченные коллекции и добавляет недостающие элементы без удаления и переименования.
    /// </summary>
    public sealed class KinopoiskFranchiseApplyService
    {
        private readonly IKinopoiskManagedCollectionGateway _gateway;
        private readonly ILogger<KinopoiskFranchiseApplyService> _logger;

        public KinopoiskFranchiseApplyService(
            IKinopoiskManagedCollectionGateway gateway,
            ILogger<KinopoiskFranchiseApplyService> logger)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Идемпотентно применяет планы к управляемым коллекциям.
        /// </summary>
        public async Task<KinopoiskFranchiseApplyResult> Apply(
            IEnumerable<KinopoiskFranchisePlan> plans,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plans);
            var normalizedPlans = plans
                .Where(plan => plan is not null
                    && plan.AnchorKinopoiskId > 0
                    && !string.IsNullOrWhiteSpace(plan.SuggestedName)
                    && plan.Items is not null
                    && plan.Items.Count >= 2)
                .GroupBy(plan => plan.AnchorKinopoiskId)
                .Select(group => group
                    .OrderByDescending(plan => plan.Items.Count)
                    .ThenBy(plan => plan.SuggestedName, StringComparer.Ordinal)
                    .First())
                .OrderBy(plan => plan.AnchorKinopoiskId)
                .ToArray();
            var existingCollections = await _gateway
                .GetManagedCollections(cancellationToken)
                .ConfigureAwait(false);
            var collectionsByAnchor = existingCollections
                .GroupBy(collection => collection.AnchorKinopoiskId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var itemResults = new List<KinopoiskFranchiseApplyItemResult>(normalizedPlans.Length);
            var result = new KinopoiskFranchiseApplyResult
            {
                PlanCount = normalizedPlans.Length
            };

            for (var index = 0; index < normalizedPlans.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = normalizedPlans[index];
                var itemResult = new KinopoiskFranchiseApplyItemResult
                {
                    AnchorKinopoiskId = plan.AnchorKinopoiskId,
                    SuggestedName = plan.SuggestedName
                };

                try
                {
                    collectionsByAnchor.TryGetValue(plan.AnchorKinopoiskId, out var matches);
                    if (matches is { Length: > 1 })
                    {
                        itemResult.Action = "conflict";
                        itemResult.Error = "Обнаружено несколько управляемых коллекций с одинаковым опорным Kinopoisk ID.";
                        result.ConflictCount++;
                        itemResults.Add(itemResult);
                        continue;
                    }

                    var collection = matches?.SingleOrDefault();
                    var wasCreated = false;
                    if (collection is null)
                    {
                        collection = await _gateway
                            .CreateManagedCollection(
                                plan.AnchorKinopoiskId,
                                plan.SuggestedName,
                                cancellationToken)
                            .ConfigureAwait(false);
                        wasCreated = true;
                        result.CreatedCollectionCount++;
                        collectionsByAnchor[plan.AnchorKinopoiskId] = new[] { collection };
                    }

                    itemResult.CollectionId = collection.CollectionId;
                    var desiredItemIds = plan.Items
                        .Where(item => item is not null && item.ItemId != Guid.Empty)
                        .Select(item => item.ItemId)
                        .Distinct()
                        .OrderBy(id => id)
                        .ToArray();
                    var missingItemIds = desiredItemIds
                        .Where(id => !collection.ItemIds.Contains(id))
                        .ToArray();

                    if (missingItemIds.Length > 0)
                    {
                        await _gateway
                            .AddItems(collection.CollectionId, missingItemIds, cancellationToken)
                            .ConfigureAwait(false);
                        itemResult.Action = wasCreated ? "created" : "updated";
                        itemResult.AddedItemCount = missingItemIds.Length;
                        result.AddedItemCount += missingItemIds.Length;
                        if (!wasCreated)
                            result.UpdatedCollectionCount++;

                        collection = new KinopoiskManagedCollectionSnapshot
                        {
                            CollectionId = collection.CollectionId,
                            AnchorKinopoiskId = collection.AnchorKinopoiskId,
                            Name = collection.Name,
                            ItemIds = collection.ItemIds
                                .Concat(missingItemIds)
                                .ToHashSet()
                        };
                        collectionsByAnchor[plan.AnchorKinopoiskId] = new[] { collection };
                    }
                    else
                    {
                        itemResult.Action = wasCreated ? "created" : "unchanged";
                        if (!wasCreated)
                            result.UnchangedCollectionCount++;
                    }

                    itemResults.Add(itemResult);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    itemResult.Action = "failed";
                    itemResult.Error = exception.Message;
                    result.FailureCount++;
                    itemResults.Add(itemResult);
                    _logger.LogError(
                        exception,
                        "Управляемая коллекция для Kinopoisk ID {KinopoiskId} не применена",
                        plan.AnchorKinopoiskId);
                }
                finally
                {
                    progress?.Report(normalizedPlans.Length == 0
                        ? 100
                        : ((index + 1d) / normalizedPlans.Length) * 100d);
                }
            }

            result.Items = itemResults;
            return result;
        }
    }
}
