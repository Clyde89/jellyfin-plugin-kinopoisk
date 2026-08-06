namespace Jellyfin.Plugin.Kinopoisk.Configuration
{
    public enum KinopoiskDiagnosticLevel
    {
        Basic = 0,
        Detailed = 1,
        Trace = 2
    }

    public static class DiagnosticLogLevel
    {
        public const KinopoiskDiagnosticLevel Basic = KinopoiskDiagnosticLevel.Basic;

        public const KinopoiskDiagnosticLevel Detailed = KinopoiskDiagnosticLevel.Detailed;

        public const KinopoiskDiagnosticLevel Trace = KinopoiskDiagnosticLevel.Trace;
    }
}
