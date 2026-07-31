using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchiseLibraryScannerTests
    {
        [Fact]
        public void ShouldMapOnlyItemsWithValidKinopoiskId()
        {
            var movie = Movie(1, "Фильм", 2000, "100");
            var series = Series(2, "Сериал", 2001, "200");
            var missingProviderId = Movie(3, "Без ID", 2002, null);
            var invalidProviderId = Movie(4, "Ошибочный ID", 2003, "not-a-number");
            var zeroProviderId = Movie(5, "Нулевой ID", 2004, "0");

            var result = KinopoiskFranchiseLibraryScanner.MapItems(new BaseItem[]
            {
                movie,
                series,
                missingProviderId,
                invalidProviderId,
                zeroProviderId
            });

            Assert.Equal(new[] { 100, 200 }, result.Select(item => item.KinopoiskId));
            Assert.Equal(new[] { movie.Id, series.Id }, result.Select(item => item.ItemId));
        }

        [Fact]
        public void ShouldDeduplicateKinopoiskIdDeterministically()
        {
            var laterId = Movie(
                Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                "Поздний дубликат",
                2001,
                "300");
            var earlierId = Movie(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "Основной объект",
                2000,
                "300");

            var result = KinopoiskFranchiseLibraryScanner.MapItems(new BaseItem[]
            {
                laterId,
                earlierId
            });

            var item = Assert.Single(result);
            Assert.Equal(earlierId.Id, item.ItemId);
            Assert.Equal("Основной объект", item.Name);
            Assert.Equal(2000, item.ProductionYear);
        }

        [Fact]
        public void ShouldReturnItemsOrderedByKinopoiskId()
        {
            var result = KinopoiskFranchiseLibraryScanner.MapItems(new BaseItem[]
            {
                Movie(10, "Третий", 2003, "900"),
                Movie(11, "Первый", 2001, "100"),
                Series(12, "Второй", 2002, "500")
            });

            Assert.Equal(new[] { 100, 500, 900 }, result.Select(item => item.KinopoiskId));
        }

        private static Movie Movie(
            int id,
            string name,
            int year,
            string kinopoiskId)
            => Movie(
                Guid.Parse($"00000000-0000-0000-0000-{id:D12}"),
                name,
                year,
                kinopoiskId);

        private static Movie Movie(
            Guid id,
            string name,
            int year,
            string kinopoiskId)
        {
            var item = new Movie
            {
                Id = id,
                Name = name,
                ProductionYear = year
            };
            SetProviderId(item, kinopoiskId);
            return item;
        }

        private static Series Series(
            int id,
            string name,
            int year,
            string kinopoiskId)
        {
            var item = new Series
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{id:D12}"),
                Name = name,
                ProductionYear = year
            };
            SetProviderId(item, kinopoiskId);
            return item;
        }

        private static void SetProviderId(BaseItem item, string kinopoiskId)
        {
            if (kinopoiskId is not null)
                item.ProviderIds[Constants.ProviderId] = kinopoiskId;
        }
    }
}
