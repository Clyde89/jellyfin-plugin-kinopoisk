#nullable enable

using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Kinopoisk.Playback
{
    /// <summary>
    /// Представляет нативный трейлер как локальный кэш или удалённый резерв.
    /// </summary>
    /// <remarks>
    /// Android TV 0.19.9 останавливает воспроизведение любого элемента с
    /// <see cref="LocationType.Virtual"/> до запроса PlaybackInfo. При этом
    /// служебный статический источник <see cref="MediaSourceType.Placeholder"/>
    /// нужен внутреннему DTO Jellyfin 10.11.11. Перед выбором воспроизводимого
    /// источника Jellyfin отфильтровывает Placeholder, поэтому актуальный HLS и
    /// обязательные HTTP-заголовки по-прежнему выдаёт IMediaSourceProvider
    /// плагина.
    /// </remarks>
    public sealed class KinopoiskNativeTrailerItem : Trailer
    {
        /// <inheritdoc />
        public override LocationType LocationType
            => !string.IsNullOrWhiteSpace(Path) && System.IO.File.Exists(Path)
                ? LocationType.FileSystem
                : LocationType.Remote;

        /// <inheritdoc />
        public override string GetClientTypeName()
            => BaseItemKind.Trailer.ToString();

        /// <inheritdoc />
        protected override IEnumerable<(BaseItem Item, MediaSourceType MediaSourceType)>
            GetAllItemsForMediaSources()
            => new[] { ((BaseItem)this, MediaSourceType.Placeholder) };
    }
}
