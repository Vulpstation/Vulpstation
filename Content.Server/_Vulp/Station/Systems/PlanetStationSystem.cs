using Content.Server._Vulp.Station.Components;
using Content.Server.Atmos.Components;
using Content.Server.Parallax;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared._NC14.DayNightCycle;
using Content.Shared.Dataset;
using Content.Shared.Destructible.Thresholds;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural.Loot;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Vulp.Station.Systems;
public sealed partial class PlanetStationSystem : EntitySystem
{
    [Dependency] private readonly BiomeSystem _biome = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlanetStationComponent, StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(Entity<PlanetStationComponent> stationEnt, ref StationPostInitEvent args)
    {
        if (!TryComp(stationEnt, out StationDataComponent? stationData))
            return;

        var stationGrid = _station.GetLargestGrid(stationData);
        if (stationGrid == null)
            return;

        var mapId = Transform(stationGrid.Value).MapID;
        var mapUid = _mapManager.GetMapEntityIdOrThrow(mapId);

        _biome.EnsurePlanet(mapUid, _proto.Index(stationEnt.Comp.Biome), stationEnt.Comp.Seed);
        EnsureComp<GridAtmosphereComponent>(mapUid); // Pray to god the map also has a MapAtmosphereComponent

        // stolen from salvage gateway generation
        const string planetNames = "names_borer";
        var dataset = _proto.Index<DatasetPrototype>(planetNames);
        var name = $"{dataset.Values[_random.Next(dataset.Values.Count)]}-{_random.Next(10, 100)}-{(char) (65 + _random.Next(26))}";

        var station = _station.GetOwningStation(stationGrid)!.Value;

        _station.RenameStation(station, name, false, stationData);
        _station.AddGridToStation(station, mapUid, null, stationData, name);
        if (stationEnt.Comp.FtlTime is { } ftlTime)
            DoFtl(stationGrid.Value, mapUid, ftlTime);

        // add all the components
        _entityManager.AddComponents(mapUid, stationEnt.Comp.Components);

        if (!stationEnt.Comp.SpawnLoot)
            return;

        // copypasted from PlanetCommand. go ahead sue me
        foreach (var loot in _proto.EnumeratePrototypes<SalvageLootPrototype>())
        {
            if (!loot.Guaranteed)
                continue;

            for (var i = 0; i < loot.LootRules.Count; i++)
            {
                var rule = loot.LootRules[i];

                switch (rule)
                {
                    case BiomeMarkerLoot biomeLoot:
                    {
                        if (TryComp<BiomeComponent>(mapUid, out var biome))
                        {
                            _biome.AddMarkerLayer(mapUid, biome, biomeLoot.Prototype);
                        }
                    }
                        break;
                    case BiomeTemplateLoot biomeLoot:
                    {
                        if (TryComp<BiomeComponent>(mapUid, out var biome))
                        {
                            _biome.AddTemplate(mapUid, biome, "Loot", _proto.Index<BiomeTemplatePrototype>(biomeLoot.Prototype), i);
                        }
                    }
                        break;
                }
            }
        }
    }

    private void DoFtl(EntityUid grid, EntityUid map, MinMax ftlTime)
    {
        var time = _random.Next(ftlTime.Min, ftlTime.Max);
        // Hardcoding this because I cant imagine why you'd want to change this for one map only
        var target = new EntityCoordinates(map, _random.NextVector2(5f, 20f));
        var targetAngle = _random.NextAngle(-Angle.FromDegrees(10), Angle.FromDegrees(10));
        var shuttleComp = EnsureComp<ShuttleComponent>(grid);

        _shuttle.FTLToCoordinates(grid, shuttleComp, target, targetAngle, 0f, time);
    }
}
