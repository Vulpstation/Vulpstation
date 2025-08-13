using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Server._Vulp.Station.Components;
using Content.Server.Atmos.Components;
using Content.Server.Parallax;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared._NC14.DayNightCycle;
using Content.Shared.Dataset;
using Content.Shared.Destructible.Thresholds;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural.Loot;
using Robust.Server.GameObjects;
using Robust.Server.Physics;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;


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
    [Dependency] private readonly GridFixtureSystem _gridFixtures = default!;
    [Dependency] private readonly TransformSystem _xforms = default!;

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

        // Finally, if ftl is disabled...
        if (stationEnt.Comp.MergeIntoPlanet)
        {
            if (stationEnt.Comp.FtlTime is not null)
                Log.Error("FTL time is set, but merge into planet is enabled. This is not possible, skipping map merge.");
            else
                MergeGrids(mapUid, stationGrid.Value);
        }
    }

    public void MergeGrids(EntityUid target, EntityUid source)
    {
        // GridFixtureSystem fails to transfer unanchored entities
        // Faster to do an all-entity query rather than use entity lookup
        var query = AllEntityQuery<TransformComponent>();
        var detachedEntities = new List<(EntityUid uid, Vector2 worldPos, Angle worldRot)>();
        while (query.MoveNext(out var uid, out var xform))
        {
            // Only entities on this grid that are directly parented to it (not in containers)
            // Also ignore anchored entities because those will be processed by the grid fixture system
            if (xform.GridUid != source || xform.ParentUid != source || xform.Anchored || MetaData(uid).Flags.HasFlag(MetaDataFlags.InContainer))
                continue;

            var (position, rotation) = _xforms.GetWorldPositionRotation(xform);
            _xforms.DetachEntity(uid, xform);
            detachedEntities.Add((uid, position, rotation));
        }

        _gridFixtures.Merge(target, source, Transform(source).LocalMatrix);

        foreach (var entity in detachedEntities)
        {
            // Yea man, I dunno why, but some entities may lack a transform
            if (!EntityManager.TransformQuery.TryComp(entity.uid, out var xform))
                continue;

            _xforms.SetParent(entity.uid, target);
            _xforms.SetWorldPositionRotation(entity.uid, entity.worldPos, entity.worldRot, xform);
        }
    }

    private void DoFtl(EntityUid grid, EntityUid map, MinMax ftlTime)
    {
        var time = _random.Next(ftlTime.Min, ftlTime.Max);
        // Hardcoding this because I cant imagine why you'd want to change this for one map only
        var target = new EntityCoordinates(map, _random.NextVector2(5f, 20f));
        var targetAngle = Angle.Zero; // A bit annoying
        var shuttleComp = EnsureComp<ShuttleComponent>(grid);

        // Running this immediately results in weird bugs
        Timer.Spawn(TimeSpan.FromSeconds(1),
            () => _shuttle.FTLToCoordinates(grid, shuttleComp, target, targetAngle, 0f, time));
    }
}
