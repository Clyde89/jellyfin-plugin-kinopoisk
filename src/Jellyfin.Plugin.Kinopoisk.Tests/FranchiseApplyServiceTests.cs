using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchiseApplyServiceTests
    {
        [Fact]
        public async Task ShouldCreateMarkedCollectionAndAddAllDesiredItems()
        {
            var gateway = new FakeGateway();
            var service = CreateService(gateway);
            var plan = Plan(100, "Матрица — коллекция", Item(1, 100), Item(2, 101));

            var result = await service.Apply(new[] { plan });

            Assert.Equal(1, result.CreatedCollectionCount);
            Assert.Equal(0, result.UpdatedCollectionCount);
            Assert.Equal(2, result.AddedItemCount);
            Assert.Single(gateway.Created);
            Assert.Equal(100, gateway.Created[0].AnchorKinopoiskId);
            Assert.Equal("Матрица — коллекция", gateway.Created[0].Name);
            Assert.Single(gateway.Added);
            Assert.Equal(new[] { ItemId(1), ItemId(2) }, gateway.Added[0].ItemIds);
            Assert.Equal("created", Assert.Single(result.Items).Action);
        }

        [Fact]
        public async Task ShouldAddOnlyMissingItemsWithoutRenamingExistingCollection()
        {
            var existing = Snapshot(
                200,
                "Ручное название управляемой коллекции",
                ItemId(1));
            var gateway = new FakeGateway(existing);
            var service = CreateService(gateway);
            var plan = Plan(200, "Новое предлагаемое название", Item(1, 200), Item(2, 201));

            var result = await service.Apply(new[] { plan });

            Assert.Empty(gateway.Created);
            var addition = Assert.Single(gateway.Added);
            Assert.Equal(new[] { ItemId(2) }, addition.ItemIds);
            Assert.Equal("Ручное название управляемой коллекции", existing.Name);
            Assert.Equal(1, result.UpdatedCollectionCount);
            Assert.Equal(1, result.AddedItemCount);
            Assert.Equal("updated", Assert.Single(result.Items).Action);
        }

        [Fact]
        public async Task ShouldRemainIdempotentWhenAllItemsAlreadyExist()
        {
            var gateway = new FakeGateway(Snapshot(300, "Готовая коллекция", ItemId(1), ItemId(2)));
            var service = CreateService(gateway);

            var first = await service.Apply(new[]
            {
                Plan(300, "Другое имя", Item(1, 300), Item(2, 301))
            });
            var second = await service.Apply(new[]
            {
                Plan(300, "Другое имя", Item(1, 300), Item(2, 301))
            });

            Assert.Empty(gateway.Created);
            Assert.Empty(gateway.Added);
            Assert.Equal(1, first.UnchangedCollectionCount);
            Assert.Equal(1, second.UnchangedCollectionCount);
            Assert.Equal("unchanged", Assert.Single(second.Items).Action);
        }

        [Fact]
        public async Task ShouldBlockDuplicateManagedCollectionsForSameAnchor()
        {
            var gateway = new FakeGateway(
                Snapshot(400, "Первая", ItemId(1)),
                Snapshot(400, "Вторая", ItemId(2)));
            var service = CreateService(gateway);

            var result = await service.Apply(new[]
            {
                Plan(400, "Предлагаемая", Item(1, 400), Item(2, 401))
            });

            Assert.Empty(gateway.Created);
            Assert.Empty(gateway.Added);
            Assert.Equal(1, result.ConflictCount);
            Assert.Equal("conflict", Assert.Single(result.Items).Action);
        }

        [Fact]
        public async Task ShouldBlockCreationWhenAnyCollectionUsesSuggestedName()
        {
            var gateway = new FakeGateway();
            gateway.ExistingCollectionNames.Add("Матрица — коллекция");
            var service = CreateService(gateway);

            var result = await service.Apply(new[]
            {
                Plan(450, "матрица — КОЛЛЕКЦИЯ", Item(1, 450), Item(2, 451))
            });

            Assert.Empty(gateway.Created);
            Assert.Empty(gateway.Added);
            Assert.Equal(1, result.ConflictCount);
            var item = Assert.Single(result.Items);
            Assert.Equal("conflict", item.Action);
            Assert.Contains("уже существует", item.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ShouldContinueAfterSinglePlanFailure()
        {
            var gateway = new FakeGateway { FailingAnchorId = 500 };
            var service = CreateService(gateway);

            var result = await service.Apply(new[]
            {
                Plan(500, "Ошибка", Item(1, 500), Item(2, 501)),
                Plan(600, "Успех", Item(3, 600), Item(4, 601))
            });

            Assert.Equal(1, result.FailureCount);
            Assert.Equal(1, result.CreatedCollectionCount);
            Assert.Equal(2, result.AddedItemCount);
            var failed = Assert.Single(result.Items.Where(item => item.AnchorKinopoiskId == 500));
            Assert.Equal("failed", failed.Action);
            Assert.DoesNotContain("Тестовая ошибка создания", failed.Error, StringComparison.Ordinal);
            Assert.Contains("Подробности сохранены в журнале", failed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(result.Items, item => item.AnchorKinopoiskId == 600 && item.Action == "created");
        }

        private static KinopoiskFranchiseApplyService CreateService(FakeGateway gateway)
            => new(
                gateway,
                NullLogger<KinopoiskFranchiseApplyService>.Instance);

        private static KinopoiskFranchisePlan Plan(
            int anchorId,
            string name,
            params KinopoiskFranchiseLibraryItem[] items)
            => new()
            {
                AnchorKinopoiskId = anchorId,
                SuggestedName = name,
                Items = items
            };

        private static KinopoiskFranchiseLibraryItem Item(
            int localId,
            int kinopoiskId)
            => new()
            {
                ItemId = ItemId(localId),
                KinopoiskId = kinopoiskId,
                Name = $"Фильм {kinopoiskId}",
                ProductionYear = 2000 + localId
            };

        private static Guid ItemId(int value)
            => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

        private static KinopoiskManagedCollectionSnapshot Snapshot(
            int anchorId,
            string name,
            params Guid[] itemIds)
            => new()
            {
                CollectionId = Guid.NewGuid(),
                AnchorKinopoiskId = anchorId,
                Name = name,
                ItemIds = itemIds.ToHashSet()
            };

        private sealed class FakeGateway : IKinopoiskManagedCollectionGateway
        {
            private readonly List<KinopoiskManagedCollectionSnapshot> _collections;

            public FakeGateway(params KinopoiskManagedCollectionSnapshot[] collections)
            {
                _collections = collections.ToList();
                ExistingCollectionNames = new HashSet<string>(
                    _collections.Select(collection => collection.Name),
                    StringComparer.OrdinalIgnoreCase);
            }

            public int? FailingAnchorId { get; set; }

            public HashSet<string> ExistingCollectionNames { get; }

            public List<KinopoiskManagedCollectionSnapshot> Created { get; } = new();

            public List<(Guid CollectionId, IReadOnlyCollection<Guid> ItemIds)> Added { get; } = new();

            public Task<IReadOnlyList<KinopoiskManagedCollectionSnapshot>> GetManagedCollections(
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<KinopoiskManagedCollectionSnapshot>>(_collections.ToArray());

            public Task<bool> CollectionNameExists(
                string name,
                CancellationToken cancellationToken = default)
                => Task.FromResult(ExistingCollectionNames.Contains(name));

            public Task<KinopoiskManagedCollectionSnapshot> CreateManagedCollection(
                int anchorKinopoiskId,
                string name,
                CancellationToken cancellationToken = default)
            {
                if (FailingAnchorId == anchorKinopoiskId)
                    throw new InvalidOperationException("Тестовая ошибка создания");

                var snapshot = new KinopoiskManagedCollectionSnapshot
                {
                    CollectionId = Guid.NewGuid(),
                    AnchorKinopoiskId = anchorKinopoiskId,
                    Name = name,
                    ItemIds = new HashSet<Guid>()
                };
                Created.Add(snapshot);
                _collections.Add(snapshot);
                ExistingCollectionNames.Add(name);
                return Task.FromResult(snapshot);
            }

            public Task AddItems(
                Guid collectionId,
                IReadOnlyCollection<Guid> itemIds,
                CancellationToken cancellationToken = default)
            {
                Added.Add((collectionId, itemIds.ToArray()));
                return Task.CompletedTask;
            }
        }
    }
}
