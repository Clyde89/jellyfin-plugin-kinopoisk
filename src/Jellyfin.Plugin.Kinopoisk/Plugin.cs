using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kinopoisk.Configuration;
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

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            Configuration.Normalize();
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            if (configuration is not PluginConfiguration pluginConfiguration)
                throw new ArgumentException("Получен неподдерживаемый тип конфигурации.", nameof(configuration));

            pluginConfiguration.Normalize();
            base.UpdateConfiguration(pluginConfiguration);
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
                }
            };
        }
    }
}
