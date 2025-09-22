using System.Collections.Generic;
using System.Linq;
using Content.Server._Vulp.GameRules.PlanetGridLoad;
using Content.Server._Vulp.Station.Systems;
using Content.Shared.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;


namespace Content.IntegrationTests.Tests._Vulp;

[TestFixture]
[TestOf(typeof(StationLoadPlanetaryGridsSystem))]
public sealed class PlanetaryGridsTest
{
    [Test]
    public async Task AllPlanetaryGridsLoadableTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var compFactory = server.ResolveDependency<IComponentFactory>();
        var mapLoader = entMan.System<MapLoaderSystem>();
        var mapSystem = entMan.System<SharedMapSystem>();
        var mapMan = server.ResolveDependency<IMapManager>();

        var compKey = compFactory.GetRegistration(typeof(StationLoadPlanetaryGridsComponent)).Name;
        var planetaryGrids = protoMan.EnumeratePrototypes<EntityPrototype>()
            .Where(it => it.Components.ContainsKey(compKey))
            .Select(it => (it.ID, Comp: it.Components[compKey].Component as StationLoadPlanetaryGridsComponent))
            .SelectMany(it => it.Comp.Grids.Select(grid => (it.ID, grid)));

        var maps = new List<MapId>();
        foreach (var (protoId, grid) in planetaryGrids)
        {
            Assert.That(grid.Distance.Min >= 0 && grid.Distance.Max >= 0,
                $"Station prototype {protoId} specifies grid {grid.Path} with negative distance");
            Assert.That(grid.Distance.Min <= grid.Distance.Max,
                $"Station prototype {protoId} specifies grid {grid.Path} with distance min > max");

            mapSystem.CreateMap(out var mapId);
            var success = mapLoader.TryLoad(mapId, grid.Path.CanonPath, out var roots);

            Assert.That(success, $"Failed to load grid {grid.Path}");
            Assert.That(roots != null && roots.Count > 0, $"Grid {grid.Path} did not load any entities");
            Assert.That(roots!.Any(it => entMan.HasComponent<MapGridComponent>(it)),
                $"Grid {grid.Path} did not load any grids?");

            maps.Add(mapId);
        }

        await server.WaitRunTicks(3);
        await server.WaitIdleAsync();

        foreach (var mapId in maps)
            mapMan.DeleteMap(mapId);

        await pair.CleanReturnAsync();
    }
}
