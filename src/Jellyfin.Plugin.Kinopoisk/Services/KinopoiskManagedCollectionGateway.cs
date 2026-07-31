using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Содержит снимок коллекции, управляемой плагином КиноПоиска.
    /// </summary>
    public sealed class KinopoiskManagedCollectionSnapshot
    {
        public Guid CollectionId { get; set; }
        public int AnchorKinopoiskId { get; set; }
        public string Name { get; set; } = string.Empty;
        public IReadOnlySet<Guid> ItemIds { get; set; } = new HashSet<Guid>();
    }

    /// <summary>
    /// Предоставляет минимальный набор операций над управляемыми коллекциями.
    /// </summary>
    public interface IKinopoiskManagedCollectionGateway
    {
        Task<IReadOnlyList<KinopoiskManagedCollectionSnapshot>> GetManagedCollections(
            CancellationToken cancellationToken = default);

        Task<KinopoiskManagedCollectionSnapshot> CreateManagedCollection(
            int anchorKinopoiskId,
            string name,
            CancellationToken cancellationToken = default);

        Task AddItems(
            Guid collectionId,
            IReadOnlyCollection<Guid> itemIds,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Выполняет только создание и дополнение коллекций с собственным маркером плагина.
    /// </summary>
    public sealed class KinopoiskManagedCollectionGateway : IKinopoiskManagedCollectionGateway
    {
        /// <summary>
        /// Идентификатор маркера управляемой коллекции.
        /// </summary>
        public const string ManagedProviderId = "KinopoiskFranchise";

        /// <summary>
        /// Идентификатор версии схемы маркера.
        /// </summary>
        public const string ManagedSchemaProviderId = "KinopoiskFranchiseSchema";

        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;

        public KinopoiskManagedCollectionGateway(
            ILibraryManager libraryManager,
            ICollectionManager collectionManager)
        {
            _libraryManager = libraryManager
                ?? throw new ArgumentNullException(nameof(libraryManager));
            _collectionManager = collectionManager
                ?? throw new ArgumentNullException(nameof(collectionManager));
        }

        public Task<IReadOnlyList<KinopoiskManagedCollectionSnapshot>> GetManagedCollections(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collections = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                    CollapseBoxSetItems = false,
                    Recursive = true,
                    EnableTotalRecordCount = false
                })
                .OfType<BoxSet>()
                .Select(TryMap)
                .Where(item => item is not null)
                .OrderBy(item => item.AnchorKinopoiskId)
                .ThenBy(item => item.CollectionId)
                .ToArray();

            return Task.FromResult<IReadOnlyList<KinopoiskManagedCollectionSnapshot>>(collections);
        }

        public async Task<KinopoiskManagedCollectionSnapshot> CreateManagedCollection(
            int anchorKinopoiskId,
            string name,
            CancellationToken cancellationToken = default)
        {
            if (anchorKinopoiskId < 1)
                throw new ArgumentOutOfRangeException(nameof(anchorKinopoiskId));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Название коллекции не должно быть пустым.", nameof(name));
            cancellationToken.ThrowIfCancellationRequested();

            var collection = await _collectionManager.CreateCollectionAsync(
                    new CollectionCreationOptions
                    {
                        Name = name.Trim(),
                        IsLocked = false,
                        ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [ManagedProviderId] = anchorKinopoiskId.ToString(CultureInfo.InvariantCulture),
                            [ManagedSchemaProviderId] = "1"
                        }
                    })
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return Map(collection, anchorKinopoiskId);
        }

        public async Task AddItems(
            Guid collectionId,
            IReadOnlyCollection<Guid> itemIds,
            CancellationToken cancellationToken = default)
        {
            if (collectionId == Guid.Empty)
                throw new ArgumentException("Идентификатор коллекции не должен быть пустым.", nameof(collectionId));
            ArgumentNullException.ThrowIfNull(itemIds);
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedIds = itemIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (normalizedIds.Length == 0)
                return;

            await _collectionManager
                .AddToCollectionAsync(collectionId, normalizedIds)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static KinopoiskManagedCollectionSnapshot TryMap(BoxSet boxSet)
        {
            if (boxSet?.ProviderIds is null
                || !boxSet.ProviderIds.TryGetValue(ManagedProviderId, out var anchorValue)
                || !int.TryParse(
                    anchorValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var anchorKinopoiskId)
                || anchorKinopoiskId < 1
                || !boxSet.ProviderIds.TryGetValue(ManagedSchemaProviderId, out var schema)
                || !string.Equals(schema, "1", StringComparison.Ordinal))
            {
                return null;
            }

            return Map(boxSet, anchorKinopoiskId);
        }

        private static KinopoiskManagedCollectionSnapshot Map(
            BoxSet boxSet,
            int anchorKinopoiskId)
        {
            return new KinopoiskManagedCollectionSnapshot
            {
                CollectionId = boxSet.Id,
                AnchorKinopoiskId = anchorKinopoiskId,
                Name = boxSet.Name?.Trim() ?? string.Empty,
                ItemIds = boxSet.GetLinkedChildren()
                    .Where(item => item is not null && item.Id != Guid.Empty)
                    .Select(item => item.Id)
                    .ToHashSet()
            };
        }
    }
}
