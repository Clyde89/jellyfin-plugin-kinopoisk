using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Формирует стабильный SHA-256 отпечаток плана франшиз и значимых параметров.
    /// </summary>
    public static class KinopoiskFranchisePlanFingerprint
    {
        /// <summary>
        /// Вычисляет стабильный отпечаток плана предварительного просмотра.
        /// </summary>
        /// <param name="plans">Планы локальных франшиз.</param>
        /// <param name="options">Параметры построения отчёта.</param>
        /// <returns>SHA-256 в нижнем регистре.</returns>
        public static string Compute(
            IEnumerable<KinopoiskFranchisePlan> plans,
            KinopoiskFranchiseReportOptions options)
        {
            ArgumentNullException.ThrowIfNull(plans);
            ArgumentNullException.ThrowIfNull(options);

            var builder = new StringBuilder();
            builder.Append("schema=1\n");
            builder.Append("includeSeries=").Append(options.IncludeSeries ? '1' : '0').Append('\n');
            builder.Append("includeSequels=").Append(options.IncludeSequels ? '1' : '0').Append('\n');
            builder.Append("includePrequels=").Append(options.IncludePrequels ? '1' : '0').Append('\n');
            builder.Append("includeRemakes=").Append(options.IncludeRemakes ? '1' : '0').Append('\n');
            builder.Append("minimumItems=")
                .Append(options.MinimumItems.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            builder.Append("suffix=")
                .Append(Escape(options.CollectionNameSuffix))
                .Append('\n');

            foreach (var plan in plans
                .Where(plan => plan is not null)
                .OrderBy(plan => plan.AnchorKinopoiskId)
                .ThenBy(plan => plan.SuggestedName, StringComparer.Ordinal))
            {
                builder.Append("plan=")
                    .Append(plan.AnchorKinopoiskId.ToString(CultureInfo.InvariantCulture))
                    .Append('|')
                    .Append(Escape(plan.SuggestedName))
                    .Append('\n');

                foreach (var item in (plan.Items ?? Array.Empty<KinopoiskFranchiseLibraryItem>())
                    .Where(item => item is not null)
                    .OrderBy(item => item.KinopoiskId)
                    .ThenBy(item => item.ItemId))
                {
                    builder.Append("item=")
                        .Append(item.KinopoiskId.ToString(CultureInfo.InvariantCulture))
                        .Append('|')
                        .Append(item.ItemId.ToString("D"))
                        .Append('|')
                        .Append(item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                        .Append('|')
                        .Append(Escape(item.Name))
                        .Append('\n');
                }

                foreach (var relationType in (plan.RelationTypes ?? Array.Empty<KinopoiskUnofficialInfo.ApiClient.FilmSequelsAndPrequelsResponseRelationType>())
                    .Distinct()
                    .OrderBy(value => value))
                {
                    builder.Append("relation=")
                        .Append(relationType)
                        .Append('\n');
                }
            }

            return Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
                .ToLowerInvariant();
        }

        private static string Escape(string value)
            => (value ?? string.Empty)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("|", "\\|", StringComparison.Ordinal);
    }
}
