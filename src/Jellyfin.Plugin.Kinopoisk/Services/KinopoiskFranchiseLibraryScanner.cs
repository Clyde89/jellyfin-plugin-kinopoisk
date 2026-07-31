using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Получает локальные фильмы и сериалы с сохранённым идентификатором КиноПоиска.
    /// </summary>
    public sealed class KinopoiskFranchiseLibraryScanner
    {
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Инициализирует сканер локальной медиатеки.
        /// </summary>
        /// <param name="libraryManager">Менеджер медиатеки Jellyfin.</param>
        public KinopoiskFranchiseLibraryScanner(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager
                ?? throw new ArgumentNullException(nameof(libraryManager));
        }

        /// <summary>
        /// Получает локальные объекты, пригодные для планирования франшиз.
        /// </summary>
        /// <param name="includeSeries">Признак включения сериалов.</param>
        /// <returns>Детерминированный список локальных объектов.</returns>
        public IReadOnlyList<KinopoiskFranchiseLibraryItem> Scan(bool includeSeries)
        {
            var itemTypes = includeSeries
                ? new[] { BaseItemKind.Movie, BaseItemKind.Series }
                : new[] { BaseItemKind.Movie };
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = itemTypes,
                IsVirtualItem = false,
                CollapseBoxSetItems = false,
                EnableTotalRecordCount = false
            });

            return MapItems(items);
        }

        /// <summary>
        /// Преобразует объекты Jellyfin в локальные элементы планировщика.
        /// </summary>
        /// <param name="items">Объекты Jellyfin.</param>
        /// <returns>Детерминированный список уникальных Kinopoisk ID.</returns>
        public static IReadOnlyList<KinopoiskFranchiseLibraryItem> MapItems(
            IEnumerable<BaseItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            return items
                .Where(item => item is not null && item.Id != Guid.Empty)
                .Select(TryMapItem)
                .Where(item => item is not null)
                .GroupBy(item => item.KinopoiskId)
                .Select(group => group
                    .OrderBy(item => item.ItemId)
                    .First())
                .OrderBy(item => item.KinopoiskId)
                .ToArray();
        }

        private static KinopoiskFranchiseLibraryItem TryMapItem(BaseItem item)
        {
            if (item.ProviderIds is null
                || !item.ProviderIds.TryGetValue(Constants.ProviderId, out var providerId)
                || !int.TryParse(
                    providerId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var kinopoiskId)
                || kinopoiskId < 1)
            {
                return null;
            }

            return new KinopoiskFranchiseLibraryItem
            {
                ItemId = item.Id,
                KinopoiskId = kinopoiskId,
                Name = item.Name?.Trim() ?? string.Empty,
                ProductionYear = item.ProductionYear
            };
        }
    }
}
