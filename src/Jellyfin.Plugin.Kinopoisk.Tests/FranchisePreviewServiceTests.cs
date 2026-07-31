using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchisePreviewServiceTests
    {
        [Fact]
        public async Task ShouldRequestEachUniqueLocalIdOnlyOnce()
        {
            var client = new PreviewRelationsClient();
            var service = CreateService(client);
            var duplicate = Item(1, "Дубликат", 2001);
            duplicate.ItemId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

            var result = await service.BuildPreview(new[]
            {
                Item(1, "Первый", 2000),
                duplicate,
                Item(2, "Второй", 2002)
            });

            Assert.Equal(2, result.LocalItemCount);
            Assert.Equal(2, result.ProcessedItemCount);
            Assert.Equal(0, result.FailedRequestCount);
            Assert.Equal(2, client.RequestCount);
            Assert.Single(result.Plans);
        }

        [Fact]
        public async Task ShouldContinuePreviewWhenSingleRelationsRequestFails()
        {
            var client = new PreviewRelationsClient(failingId: 2);
            var service = CreateService(client);

            var result = await service.BuildPreview(new[]
            {
                Item(1, "Первый", 2000),
                Item(2, "Второй", 2002),
                Item(3, "Третий", 2004)
            });

            Assert.Equal(3, result.ProcessedItemCount);
            Assert.Equal(1, result.FailedRequestCount);
            Assert.Equal(3, client.RequestCount);
            Assert.Single(result.Plans);
        }

        private static KinopoiskFranchisePreviewService CreateService(
            IKinopoiskRelationsApiClient client)
        {
            return new KinopoiskFranchisePreviewService(
                client,
                new KinopoiskFranchisePlanner(),
                NullLogger<KinopoiskFranchisePreviewService>.Instance);
        }

        private static KinopoiskFranchiseLibraryItem Item(int id, string name, int year)
        {
            return new KinopoiskFranchiseLibraryItem
            {
                ItemId = Guid.Parse($"00000000-0000-0000-0000-{id:D12}"),
                KinopoiskId = id,
                Name = name,
                ProductionYear = year
            };
        }

        private sealed class PreviewRelationsClient : IKinopoiskRelationsApiClient
        {
            private readonly int? _failingId;

            public PreviewRelationsClient(int? failingId = null)
            {
                _failingId = failingId;
            }

            public int RequestCount { get; private set; }

            public Task<ICollection<FilmSequelsAndPrequelsResponse>> GetRelations(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                RequestCount++;
                if (_failingId == filmId)
                    throw new HttpRequestException("Тестовая ошибка");

                ICollection<FilmSequelsAndPrequelsResponse> result = filmId switch
                {
                    1 => new[] { Relation(3, FilmSequelsAndPrequelsResponseRelationType.SEQUEL) },
                    2 => new[] { Relation(3, FilmSequelsAndPrequelsResponseRelationType.SEQUEL) },
                    _ => Array.Empty<FilmSequelsAndPrequelsResponse>()
                };
                return Task.FromResult(result);
            }

            private static FilmSequelsAndPrequelsResponse Relation(
                int filmId,
                FilmSequelsAndPrequelsResponseRelationType type)
            {
                return new FilmSequelsAndPrequelsResponse
                {
                    FilmId = filmId,
                    NameRu = $"Фильм {filmId}",
                    NameEn = $"Film {filmId}",
                    PosterUrl = "https://example.test/poster.jpg",
                    PosterUrlPreview = "https://example.test/poster-small.jpg",
                    RelationType = type
                };
            }
        }
    }
}
