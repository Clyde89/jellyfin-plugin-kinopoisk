using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchisePlannerTests
    {
        [Fact]
        public void ShouldBuildTransitiveFranchiseFromLocalSequelsAndPrequels()
        {
            var items = new[]
            {
                Item(1, "Матрица", 1999),
                Item(2, "Матрица: Перезагрузка", 2003),
                Item(3, "Матрица: Революция", 2003)
            };
            var relations = Relations(
                (1, Relation(2, FilmSequelsAndPrequelsResponseRelationType.SEQUEL)),
                (2, Relation(3, FilmSequelsAndPrequelsResponseRelationType.SEQUEL)));

            var plan = Assert.Single(new KinopoiskFranchisePlanner().Build(items, relations));

            Assert.Equal(1, plan.AnchorKinopoiskId);
            Assert.Equal("Матрица — коллекция", plan.SuggestedName);
            Assert.Equal(new[] { 1, 2, 3 }, plan.Items.Select(item => item.KinopoiskId));
            Assert.Equal(
                new[] { FilmSequelsAndPrequelsResponseRelationType.SEQUEL },
                plan.RelationTypes);
        }

        [Fact]
        public void ShouldIncludeOnlyItemsPresentInLocalLibrary()
        {
            var items = new[]
            {
                Item(10, "Локальный фильм", 2000),
                Item(11, "Локальный сиквел", 2002)
            };
            var relations = Relations(
                (10,
                    Relation(11, FilmSequelsAndPrequelsResponseRelationType.SEQUEL),
                    Relation(99, FilmSequelsAndPrequelsResponseRelationType.SEQUEL)));

            var plan = Assert.Single(new KinopoiskFranchisePlanner().Build(items, relations));

            Assert.Equal(new[] { 10, 11 }, plan.Items.Select(item => item.KinopoiskId));
            Assert.DoesNotContain(plan.Items, item => item.KinopoiskId == 99);
        }

        [Fact]
        public void ShouldExcludeRemakesByDefault()
        {
            var items = new[]
            {
                Item(20, "Оригинал", 1980),
                Item(21, "Ремейк", 2020)
            };
            var relations = Relations(
                (20, Relation(21, FilmSequelsAndPrequelsResponseRelationType.REMAKE)));

            var plans = new KinopoiskFranchisePlanner().Build(items, relations);

            Assert.Empty(plans);
        }

        [Fact]
        public void ShouldIncludeRemakesWhenEnabled()
        {
            var items = new[]
            {
                Item(20, "Оригинал", 1980),
                Item(21, "Ремейк", 2020)
            };
            var relations = Relations(
                (20, Relation(21, FilmSequelsAndPrequelsResponseRelationType.REMAKE)));

            var plan = Assert.Single(new KinopoiskFranchisePlanner().Build(
                items,
                relations,
                new KinopoiskFranchisePlannerOptions { IncludeRemakes = true }));

            Assert.Equal(
                new[] { FilmSequelsAndPrequelsResponseRelationType.REMAKE },
                plan.RelationTypes);
        }

        [Fact]
        public void ShouldIgnoreUnknownAndSelfRelations()
        {
            var items = new[]
            {
                Item(30, "Первый", 2001),
                Item(31, "Второй", 2002)
            };
            var relations = Relations(
                (30,
                    Relation(30, FilmSequelsAndPrequelsResponseRelationType.SEQUEL),
                    Relation(31, FilmSequelsAndPrequelsResponseRelationType.UNKNOWN)));

            Assert.Empty(new KinopoiskFranchisePlanner().Build(items, relations));
        }

        [Fact]
        public void ShouldChooseEarliestLocalFilmAsStableAnchor()
        {
            var items = new[]
            {
                Item(42, "Продолжение", 2010),
                Item(40, "Начало", 2000),
                Item(41, "Приквел", 2005)
            };
            var relations = Relations(
                (42, Relation(40, FilmSequelsAndPrequelsResponseRelationType.PREQUEL)),
                (40, Relation(41, FilmSequelsAndPrequelsResponseRelationType.SEQUEL)));

            var plan = Assert.Single(new KinopoiskFranchisePlanner().Build(items, relations));

            Assert.Equal(40, plan.AnchorKinopoiskId);
            Assert.Equal("Начало — коллекция", plan.SuggestedName);
        }

        [Fact]
        public void ShouldDeduplicateLibraryItemsWithSameKinopoiskId()
        {
            var first = Item(50, "Фильм", 2000);
            var duplicate = Item(50, "Дубликат", 2001);
            duplicate.ItemId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            var sequel = Item(51, "Сиквел", 2002);
            var relations = Relations(
                (50, Relation(51, FilmSequelsAndPrequelsResponseRelationType.SEQUEL)));

            var plan = Assert.Single(new KinopoiskFranchisePlanner().Build(
                new[] { duplicate, first, sequel },
                relations));

            Assert.Equal(2, plan.Items.Count);
            Assert.Single(plan.Items.Where(item => item.KinopoiskId == 50));
        }

        private static KinopoiskFranchiseLibraryItem Item(int kinopoiskId, string name, int year)
        {
            return new KinopoiskFranchiseLibraryItem
            {
                ItemId = Guid.Parse($"00000000-0000-0000-0000-{kinopoiskId:D12}"),
                KinopoiskId = kinopoiskId,
                Name = name,
                ProductionYear = year
            };
        }

        private static FilmSequelsAndPrequelsResponse Relation(
            int filmId,
            FilmSequelsAndPrequelsResponseRelationType relationType)
        {
            return new FilmSequelsAndPrequelsResponse
            {
                FilmId = filmId,
                NameRu = $"Фильм {filmId}",
                NameEn = $"Film {filmId}",
                PosterUrl = "https://example.test/poster.jpg",
                PosterUrlPreview = "https://example.test/poster-small.jpg",
                RelationType = relationType
            };
        }

        private static IReadOnlyDictionary<int, IReadOnlyCollection<FilmSequelsAndPrequelsResponse>> Relations(
            params (int SourceId, FilmSequelsAndPrequelsResponse[] Relations)[] values)
        {
            return values.ToDictionary(
                value => value.SourceId,
                value => (IReadOnlyCollection<FilmSequelsAndPrequelsResponse>)value.Relations);
        }
    }
}
