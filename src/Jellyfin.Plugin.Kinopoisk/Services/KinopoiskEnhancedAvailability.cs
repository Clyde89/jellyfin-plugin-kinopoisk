#nullable enable

using System;
using System.Linq;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services;

/// <summary>Определяет доступность необязательного расширения без HTTP-запросов к нему.</summary>
public sealed class KinopoiskEnhancedAvailability
{
    public static readonly Guid EnhancedId = Guid.Parse("f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b");
    private readonly IPluginManager _plugins;
    private readonly ILogger<KinopoiskEnhancedAvailability> _logger;
    private readonly object _sync = new();
    private string? _previousState;

    public KinopoiskEnhancedAvailability(IPluginManager plugins, ILogger<KinopoiskEnhancedAvailability> logger)
    {
        _plugins = plugins;
        _logger = logger;
    }

    public KinopoiskEnhancedState Read()
    {
        lock (_sync)
        {
            string state;
            try
            {
                var matches = _plugins.Plugins.Where(plugin => plugin.Id == EnhancedId).ToArray();
                state = matches.Length == 0 ? "missing"
                    : matches.Any(plugin => plugin.IsEnabledAndSupported && plugin.Instance is not null) ? "available"
                    : matches.Any(plugin => plugin.IsEnabledAndSupported) ? "pending" : "disabled";
            }
            catch (Exception)
            {
                // Не раскрывались пути, параметры расширения или данные пользователя.
                state = "unknown";
            }
            if (_previousState != state)
            {
                _previousState = state;
                _logger.LogInformation("Состояние интеграции Jellyfin Enhanced изменено: {State}", state);
            }
            return new KinopoiskEnhancedState(state == "available", state);
        }
    }
}

public sealed record KinopoiskEnhancedState(bool Available, string State);
