using Content.Server._Vulp.Station.Systems;
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
    public Color MapLightColor = Color.Black;

    [DataField]
    public bool SpawnLoot = true;

    // components to add to the map post-planetization
    [DataField]
    public ComponentRegistry Components;
}
