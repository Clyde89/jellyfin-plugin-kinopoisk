using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Kinopoisk
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin Instance { get; private set; }

        public override string Name => Constants.PluginName;

        public override string Description => Constants.PluginDescription;

        public override Guid Id => Guid.Parse("33e6d249-648f-44cd-a9ce-497be06c08df");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            Configuration.Normalize();
            KinopoiskDiagnostics.Shared.AttachDiagnosticSink(KinopoiskDiagnosticFileSink.Shared);
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            if (configuration is not PluginConfiguration pluginConfiguration)
                throw new ArgumentException("Получен неподдерживаемый тип конфигурации.", nameof(configuration));

            var previousConfiguration = Configuration;
            var previousSessionId = previousConfiguration?.DiagnosticSessionId;
            var previousSessionActive = previousConfiguration?.EnableDiagnosticMode == true
                && previousConfiguration.DiagnosticSessionExpiresUtc.HasValue
                && previousConfiguration.DiagnosticSessionExpiresUtc.Value > DateTimeOffset.UtcNow;

            pluginConfiguration.Normalize();

            var newDiagnosticSession = pluginConfiguration.EnableDiagnosticMode
                && !string.IsNullOrWhiteSpace(pluginConfiguration.DiagnosticSessionId)
                && !string.Equals(
                    previousSessionId,
                    pluginConfiguration.DiagnosticSessionId,
                    StringComparison.Ordinal);
            var stoppedDiagnosticSession = previousSessionActive
                && !pluginConfiguration.EnableDiagnosticMode;

            if (stoppedDiagnosticSession)
                KinopoiskDiagnostics.Shared.StopDiagnosticSession();

            base.UpdateConfiguration(pluginConfiguration);

            if (newDiagnosticSession)
            {
                KinopoiskDiagnostics.Shared.ResetRuntimeCounters();
                KinopoiskDiagnostics.Shared.StartDiagnosticSession();
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    DisplayName = "КиноПоиск",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                    EnableInMainMenu = true,
                    MenuIcon = "settings"
                }
            };
        }
    }
}
