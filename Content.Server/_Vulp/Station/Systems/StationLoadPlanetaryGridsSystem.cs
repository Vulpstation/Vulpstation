using System.Linq;
using System.Numerics;
using Content.Server._Vulp.GameRules.PlanetGridLoad;
using Content.Server._Vulp.Station.Components;
using Content.Server.Atmos.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.Dataset;
using Content.Shared.Destructible.Thresholds;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural.Loot;
using Robust.Server.GameObjects;
using Robust.Server.Maps;
using Robust.Server.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Serilog;


namespace Content.Server._Vulp.Station.Systems;


public sealed class StationLoadPlanetaryGridsSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly GridFixtureSystem _gridFixtures = default!;
    [Dependency] private readonly TransformSystem _xforms = default!;
    [Dependency] private readonly PlanetStationSystem _planetStation = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<StationLoadPlanetaryGridsComponent, StationPostInitEvent>(OnStationPostInit, after: new[] { typeof(PlanetStationSystem) });
    }

    private void OnStationPostInit(Entity<StationLoadPlanetaryGridsComponent> stationEnt, ref StationPostInitEvent args)
    {
        if (!TryComp<StationDataComponent>(stationEnt, out var stationData))
            return;

        var mainGrid = stationData.Grids.Where(HasComp<BiomeComponent>).FirstOrDefault();
        if (!Exists(mainGrid))
        {
            Log.Error("No planet grid found for planetary station grid load.");
            return;
        }

        try
        {
            var spawns = stationEnt.Comp.Grids.Zip(
                ScatterPoints(
                    Vector2.Zero,
                    stationEnt.Comp.Grids.Select(it => it.Distance).ToArray(),
                    stationEnt.Comp.MinDistance));

            var mapId = Transform(mainGrid).MapID;
            foreach (var (grid, pos) in spawns)
            {
                var opts = new MapLoadOptions { Offset = pos };
                if (!_loader.TryLoad(mapId, grid.Path.CanonPath, out var roots, opts))
                    Log.Warning($"Failed to load grid {grid.Path}");

                if (!grid.MergeIntoPlanet || roots == null)
                    continue;

                // Current issue: this throws because entities have no transform comps
                foreach (var root in roots)
                {
                    if (!HasComp<MapGridComponent>(root))
                        continue;

                    _planetStation.MergeGrids(root, mainGrid);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Failed to load planetary grids", e);
        }
    }

    /// <summary>
    ///     Tries to scatter the specified number of points around the center point, ensuring that they are at least minDistance apart.
    /// </summary>
    public IEnumerable<Vector2> ScatterPoints(Vector2 center, MinMax[] ranges, float minDistance)
    {
        // Step 1. Scatter points, #0 is the center
        var gridPositions = new Vector2[ranges.Length + 1];
        gridPositions[0] = center;

        for (var i = 1; i < gridPositions.Length; i++)
        {
            var def = ranges[i - 1];
            gridPositions[i] = _random.NextVector2(def.Min, def.Max);
        }

        // Step 2. Try to move the points away from each other until they are at least minDistance apart or until we hit the iteration limit
        var moveStep = 5f;
        for (var iter = 0; iter < 100; iter++)
        {
            var anyMoved = false;
            for (var i = 0; i < gridPositions.Length; i++)
            {
                for (var j = i + 1; j < gridPositions.Length; j++)
                {
                    var dist = Vector2.Distance(gridPositions[i], gridPositions[j]);
                    if (dist >= minDistance)
                        continue;

                    var delta = (gridPositions[i] - gridPositions[j]).Normalized();
                    gridPositions[j] -= delta * moveStep;
                    gridPositions[i] += delta * moveStep;
                    anyMoved = true;
                }
            }

            gridPositions[0] = center;
            if (!anyMoved)
                break;
        }

        return gridPositions.TakeLast(ranges.Length);
    }
}
