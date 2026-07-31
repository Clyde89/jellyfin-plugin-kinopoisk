using System;
using System.Collections.Generic;
using System.Linq;
using KinopoiskUnofficialInfo.ApiClient;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Содержит локальный объект медиатеки, участвующий в построении франшизы.
    /// </summary>
    public sealed class KinopoiskFranchiseLibraryItem
    {
        /// <summary>
        /// Получает или задаёт идентификатор объекта Jellyfin.
        /// </summary>
        public Guid ItemId { get; set; }

        /// <summary>
        /// Получает или задаёт идентификатор КиноПоиска.
        /// </summary>
        public int KinopoiskId { get; set; }

        /// <summary>
        /// Получает или задаёт название объекта.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Получает или задаёт год выпуска.
        /// </summary>
        public int? ProductionYear { get; set; }
    }

    /// <summary>
    /// Содержит параметры построения плана франшиз.
    /// </summary>
    public sealed class KinopoiskFranchisePlannerOptions
    {
        /// <summary>
        /// Получает или задаёт признак учёта сиквелов.
        /// </summary>
        public bool IncludeSequels { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак учёта приквелов.
        /// </summary>
        public bool IncludePrequels { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак учёта ремейков.
        /// </summary>
        public bool IncludeRemakes { get; set; }

        /// <summary>
        /// Получает или задаёт минимальное количество локальных элементов во франшизе.
        /// </summary>
        public int MinimumItems { get; set; } = 2;

        /// <summary>
        /// Получает или задаёт суффикс предлагаемого названия коллекции.
        /// </summary>
        public string CollectionNameSuffix { get; set; } = " — коллекция";
    }

    /// <summary>
    /// Содержит запланированную локальную коллекцию франшизы.
    /// </summary>
    public sealed class KinopoiskFranchisePlan
    {
        /// <summary>
        /// Получает или задаёт стабильный идентификатор опорного фильма КиноПоиска.
        /// </summary>
        public int AnchorKinopoiskId { get; set; }

        /// <summary>
        /// Получает или задаёт предлагаемое название коллекции.
        /// </summary>
        public string SuggestedName { get; set; } = string.Empty;

        /// <summary>
        /// Получает или задаёт локальные элементы коллекции.
        /// </summary>
        public IReadOnlyList<KinopoiskFranchiseLibraryItem> Items { get; set; }
            = Array.Empty<KinopoiskFranchiseLibraryItem>();

        /// <summary>
        /// Получает или задаёт типы связей, обнаруженные внутри коллекции.
        /// </summary>
        public IReadOnlyList<FilmSequelsAndPrequelsResponseRelationType> RelationTypes { get; set; }
            = Array.Empty<FilmSequelsAndPrequelsResponseRelationType>();
    }

    /// <summary>
    /// Строит детерминированный план франшиз только из объектов локальной медиатеки.
    /// </summary>
    public sealed class KinopoiskFranchisePlanner
    {
        /// <summary>
        /// Строит план локальных коллекций по связям КиноПоиска.
        /// </summary>
        /// <param name="libraryItems">Локальные объекты медиатеки.</param>
        /// <param name="relationsByFilmId">Связи, сгруппированные по исходному Kinopoisk ID.</param>
        /// <param name="options">Параметры построения.</param>
        /// <returns>Детерминированный список планируемых коллекций.</returns>
        public IReadOnlyList<KinopoiskFranchisePlan> Build(
            IEnumerable<KinopoiskFranchiseLibraryItem> libraryItems,
            IReadOnlyDictionary<int, IReadOnlyCollection<FilmSequelsAndPrequelsResponse>> relationsByFilmId,
            KinopoiskFranchisePlannerOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(libraryItems);
            ArgumentNullException.ThrowIfNull(relationsByFilmId);
            options ??= new KinopoiskFranchisePlannerOptions();

            var minimumItems = Math.Clamp(options.MinimumItems, 2, 1000);
            var localItems = libraryItems
                .Where(item => item is not null && item.ItemId != Guid.Empty && item.KinopoiskId > 0)
                .GroupBy(item => item.KinopoiskId)
                .Select(group => group
                    .OrderBy(item => item.ItemId)
                    .First())
                .ToDictionary(item => item.KinopoiskId);
            if (localItems.Count < minimumItems)
                return Array.Empty<KinopoiskFranchisePlan>();

            var disjointSet = new DisjointSet(localItems.Keys);
            var relationTypesByPair = new Dictionary<(int Left, int Right), FilmSequelsAndPrequelsResponseRelationType>();

            foreach (var source in relationsByFilmId.OrderBy(item => item.Key))
            {
                if (!localItems.ContainsKey(source.Key) || source.Value is null)
                    continue;

                foreach (var relation in source.Value
                    .Where(relation => relation is not null)
                    .OrderBy(relation => relation.FilmId))
                {
                    if (relation.FilmId < 1
                        || relation.FilmId == source.Key
                        || !localItems.ContainsKey(relation.FilmId)
                        || !IsAllowed(relation.RelationType, options))
                    {
                        continue;
                    }

                    disjointSet.Union(source.Key, relation.FilmId);
                    var pair = NormalizePair(source.Key, relation.FilmId);
                    relationTypesByPair.TryAdd(pair, relation.RelationType);
                }
            }

            var relationTypesByRoot = new Dictionary<int, HashSet<FilmSequelsAndPrequelsResponseRelationType>>();
            foreach (var pair in relationTypesByPair)
            {
                var root = disjointSet.Find(pair.Key.Left);
                if (!relationTypesByRoot.TryGetValue(root, out var relationTypes))
                {
                    relationTypes = new HashSet<FilmSequelsAndPrequelsResponseRelationType>();
                    relationTypesByRoot[root] = relationTypes;
                }

                relationTypes.Add(pair.Value);
            }

            return localItems.Values
                .GroupBy(item => disjointSet.Find(item.KinopoiskId))
                .Where(group => group.Count() >= minimumItems)
                .Select(group => CreatePlan(
                    group,
                    relationTypesByRoot.TryGetValue(group.Key, out var relationTypes)
                        ? relationTypes
                        : Array.Empty<FilmSequelsAndPrequelsResponseRelationType>(),
                    options.CollectionNameSuffix))
                .Where(plan => plan.RelationTypes.Count > 0)
                .OrderBy(plan => plan.SuggestedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plan => plan.AnchorKinopoiskId)
                .ToArray();
        }

        private static KinopoiskFranchisePlan CreatePlan(
            IEnumerable<KinopoiskFranchiseLibraryItem> sourceItems,
            IEnumerable<FilmSequelsAndPrequelsResponseRelationType> relationTypes,
            string suffix)
        {
            var items = sourceItems
                .OrderBy(item => item.ProductionYear ?? int.MaxValue)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.KinopoiskId)
                .ToArray();
            var anchor = items[0];
            var baseName = string.IsNullOrWhiteSpace(anchor.Name)
                ? $"КиноПоиск {anchor.KinopoiskId}"
                : anchor.Name.Trim();
            var normalizedSuffix = suffix?.TrimEnd() ?? string.Empty;

            return new KinopoiskFranchisePlan
            {
                AnchorKinopoiskId = anchor.KinopoiskId,
                SuggestedName = baseName + normalizedSuffix,
                Items = items,
                RelationTypes = relationTypes
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray()
            };
        }

        private static bool IsAllowed(
            FilmSequelsAndPrequelsResponseRelationType relationType,
            KinopoiskFranchisePlannerOptions options)
        {
            return relationType switch
            {
                FilmSequelsAndPrequelsResponseRelationType.SEQUEL => options.IncludeSequels,
                FilmSequelsAndPrequelsResponseRelationType.PREQUEL => options.IncludePrequels,
                FilmSequelsAndPrequelsResponseRelationType.REMAKE => options.IncludeRemakes,
                _ => false
            };
        }

        private static (int Left, int Right) NormalizePair(int left, int right)
            => left < right ? (left, right) : (right, left);

        private sealed class DisjointSet
        {
            private readonly Dictionary<int, int> _parents;

            public DisjointSet(IEnumerable<int> values)
            {
                _parents = values.Distinct().ToDictionary(value => value, value => value);
            }

            public int Find(int value)
            {
                var parent = _parents[value];
                if (parent == value)
                    return value;

                _parents[value] = Find(parent);
                return _parents[value];
            }

            public void Union(int left, int right)
            {
                var leftRoot = Find(left);
                var rightRoot = Find(right);
                if (leftRoot == rightRoot)
                    return;

                var root = Math.Min(leftRoot, rightRoot);
                var child = Math.Max(leftRoot, rightRoot);
                _parents[child] = root;
            }
        }
    }
}
