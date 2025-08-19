using Content.Server._Vulp.Station.Systems;
using Content.Shared.Destructible.Thresholds;
using Content.Shared.Parallax.Biomes;
using Robust.Shared.Prototypes;

namespace Content.Server._Vulp.Station.Components;

/// <summary>
/// Does all the things to make a usable planet-station.
/// </summary>
[RegisterComponent, Access(typeof(PlanetStationSystem))]
public sealed partial class PlanetStationComponent : Component
{
    [DataField(required: true)]
    public ProtoId<BiomeTemplatePrototype> Biome = "Continental";

    // If null, it's random
    [DataField]
    public int? Seed = null;

    [DataField]
    public bool SpawnLoot = true;

    [DataField(required: true), AlwaysPushInheritance]
    public ComponentRegistry Components = default!;

    /// If true, the station grid will be irreversibly merged into the planet upon spawning.
    /// This is incompatible with FTL.
    [DataField]
    public bool MergeIntoPlanet = false;

    /// Time range between the spawn of the station and its arrival on the planet, in seconds. If null, skips FTL.
    [DataField]
    public MinMax? FtlTime = null;
}
