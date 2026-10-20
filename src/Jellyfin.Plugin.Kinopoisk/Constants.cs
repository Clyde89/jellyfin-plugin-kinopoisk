using System;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk
{
    public static class Constants
    {
        public const string PluginName = "КиноПоиск";
        public const string PluginDescription = "Метаданные, трейлеры и рекомендации КиноПоиска для Jellyfin. Личный список желаемого, автономная работа и интеграции с Seerr и Jellyfin Enhanced.";
        public const string ProviderId = "kinopoisk";
        public const string ProviderName = "КиноПоиск";
        public const string ProviderMetadataLanguage = "ru";
    }
}
