using System.Linq;
using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.StationEvents.Events;
using Content.Shared.CCVar;
using Content.Shared.GameTicking.Components;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Salvage;
using Robust.Server.GameObjects;
using Robust.Server.Maps;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;


namespace Content.Server._Vulp.StationEvents.RandomSalvageWreck;

public sealed class RandomSalvageWreckRule : StationEventSystem<RandomSalvageWreckRuleComponent>
{
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly IMapManager _mapMan = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IConfigurationManager _confMan = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly TransformSystem _xform = default!;
    [Dependency] private readonly StationSystem _stations = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;

    protected override void Started(EntityUid uid, RandomSalvageWreckRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        var stations = _gameTicker.GetSpawnableStations();
        if (stations.Count <= 0)
            return;

        // Floof - the station's TransformComponent does not actually store the MapID. In fact, it doesn't store anything and remains uninitialized.
        // To circumvent this issue, we instead use the transform component of the largest grid on the map.
        var chosenStation = _random.Pick(stations);
        if (!TryComp<StationDataComponent>(chosenStation, out var stationData)
            || _stations.GetLargestGrid(stationData) is not { } targetStation)
            return;

        var targetMapId = Transform(targetStation).MapID;
        if (!_map.MapExists(targetMapId))
            return;

        var debrisCount = _random.Next(component.DebrisCountRange.X, component.DebrisCountRange.Y);
        var protos = GetAllEligiblePrototypes();
        for (int i = 0; i < debrisCount; i++)
        {
            SpawnDebris(
                Transform(targetStation).MapUid ?? EntityUid.Invalid,
                component.DebrisDistanceRange,
                component.DebrisOffsetRange,
                protos,
                out var mapId);

            component.TemporaryMaps.Add(mapId);
        }
    }

    public List<SalvageMapPrototype> GetAllEligiblePrototypes()
    {
        return _prototypeManager
            .EnumeratePrototypes<SalvageMapPrototype>()
            .ToList(); // One day we will get something better
    }

    /// <summary>
    ///     Spawns a single random salvage wreck at a random position on the map.
    /// </summary>
    /// <param name="ftlTo">Map entity to FTL to,</param>
    /// <param name="debrisDistanceRange">Distance range to spawn the debris within.</param>
    /// <param name="debrisOffsetRange">Additional offset to add to the final debris positions.</param>
    /// <param name="candidates">List of candidate maps to choose from.</param>
    /// <param name="temporaryMapId">Created map.</param>
    public void SpawnDebris(
        EntityUid ftlTo,
        Vector2 debrisDistanceRange,
        Vector2 debrisOffsetRange,
        List<SalvageMapPrototype> candidates,
        out MapId temporaryMapId)
    {
        _map.CreateMap(out temporaryMapId);
        Log.Info($"Creating a random salvage wreck, using map ID {temporaryMapId}.");

        var toLoad = _random.Pick(candidates).MapPath;
        var ftlDestination =
            _random.NextVector2(debrisDistanceRange.X, debrisDistanceRange.Y)
            + _random.NextVector2(
                debrisOffsetRange.X,
                debrisOffsetRange.Y); // Second offset to make it feel more chaotic

        var options = new MapLoadOptions
        {
            DoMapInit = true,
            LoadMap = false
        };
        if (!_loader.TryLoad(temporaryMapId, toLoad.ToString(), out var loadedGrids, options) || loadedGrids.Count <= 0)
            return;

        // FTL each of the root grids, preserving their offsets
        foreach (var loadedGrid in loadedGrids)
        {
            if (!TryComp<MapGridComponent>(loadedGrid, out var grid) || grid.LocalAABB.Height <= 1 || grid.LocalAABB.Width <= 1)
                continue;

            var xform = Transform(loadedGrid);
            var shuttle = EnsureComp<ShuttleComponent>(loadedGrid);
            var targetCoords = new EntityCoordinates(ftlTo, xform.LocalPosition + ftlDestination);
            var ftlTime = _random.NextFloat(10f, 20f);
            // TODO maybe check FTLFree first?
            _shuttle.FTLToCoordinates(loadedGrid, shuttle, targetCoords, xform.LocalRotation, 0f, ftlTime);
        }
    }

    protected override void Ended(
        EntityUid uid,
        RandomSalvageWreckRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        foreach (var map in component.TemporaryMaps)
            if (_mapMan.MapExists(map))
                _mapMan.DeleteMap(map);
    }
}
