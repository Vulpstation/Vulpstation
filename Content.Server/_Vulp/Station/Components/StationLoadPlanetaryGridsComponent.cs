using Content.Shared.Destructible.Thresholds;
using Robust.Shared.Utility;


namespace Content.Server._Vulp.GameRules.PlanetGridLoad;

/// <summary>
///     Loads the specified grid and merges it onto the planetary surface when this station is spawned.
/// </summary>
[RegisterComponent]
public sealed partial class StationLoadPlanetaryGridsComponent : Component
{
    /// <summary>
    ///     List of grids to load.
    /// </summary>
    [DataField]
    public List<LoadEntry> Grids = new();

    /// <summary>
    ///     Minimum distance between loaded grids.
    /// </summary>
    [DataField]
    public float MinDistance = 30f;

    // TODO add support for choosing from multiple grids
    [DataDefinition]
    public partial struct LoadEntry
    {
        [DataField]
        public ResPath Path = default!;

        [DataField]
        public MinMax Distance = default!;

        [DataField]
        public bool MergeIntoPlanet = true;

        public LoadEntry() {}
    }
}
