using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Interoperability",
    "CA1416:Validate platform compatibility",
    Justification = "Unix-права восстанавливаются только после успешной проверки OperatingSystem.IsWindows; nullable-состояние previousMode сохраняет результат этой проверки между операциями атомарной записи.",
    Scope = "member",
    Target = "~M:Jellyfin.Plugin.Kinopoisk.Services.KinopoiskStandaloneWebClientService.WriteTextAtomically(System.String,System.String)")]
