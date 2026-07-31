using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchiseRelationTypeTests
    {
        [Fact]
        public void ShouldPreserveSequelAndPrequelTypesForSamePair()
        {
            var items = new[]
            {
                new KinopoiskFranchiseLibraryItem
                {
                    ItemId = Guid.Parse("00000000-0000-0000-0000-000000000060"),
                    KinopoiskId = 60,
                    Name = "Первый фильм",
                    ProductionYear = 2000
                },
                new KinopoiskFranchiseLibraryItem
                {
                    ItemId = Guid.Parse("00000000-0000-0000-0000-000000000061"),
                    KinopoiskId = 61,
                    Name = "Продолжение",
                    ProductionYear = 2002
                }
            };
            var relations = new Dictionary<int, IReadOnlyCollection<FilmSequelsAndPrequelsResponse>>
            {
                [60] = new[]
                {
                    Relation(61, FilmSequelsAndPrequelsResponseRelationType.SEQUEL)
                },
                [61] = new[]
                {
                    Relation(60, FilmSequelsAndPrequelsResponseRelationType.PREQUEL)
                }
            };

            var plan = Assert.Single(new KinopoiskFranchisePlanner().Build(items, relations));

            Assert.Equal(
                new[]
                {
                    FilmSequelsAndPrequelsResponseRelationType.SEQUEL,
                    FilmSequelsAndPrequelsResponseRelationType.PREQUEL
                }.OrderBy(value => value),
                plan.RelationTypes);
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
                NameOriginal = $"Film {filmId}",
                PosterUrl = "https://example.test/poster.jpg",
                PosterUrlPreview = "https://example.test/poster-small.jpg",
                RelationType = relationType
            };
        }
    }
}
