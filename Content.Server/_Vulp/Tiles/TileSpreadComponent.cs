using Content.Shared.Maps;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;


namespace Content.Server._Vulp.Tiles;

[RegisterComponent, Access(typeof(TileSpreadSystem))]
[AutoGenerateComponentPause]
public sealed partial class TileSpreadComponent : Component
{
    /// <summary>
    /// The next time that tiles will try to spread.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    [AutoPausedField]
    public TimeSpan NextUpdate;

    /// <summary>
    /// How often to try and spread.
    /// </summary>
    [DataField]
    public TimeSpan UpdateInterval = TimeSpan.FromSeconds(10);

    [DataField(required: true)]
    public TileSpreadInfo[] Tiles;
}

[DataDefinition]
public sealed partial class TileSpreadInfo
{
    /// <summary>
    /// ID of the tile that will spread.
    /// </summary>
    [DataField("id", required: true)]
    public ProtoId<ContentTileDefinition> ID;

    /// <summary>
    /// IDs of the tiles it can spread to.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<ContentTileDefinition>[] SpreadsTo;

    /// <summary>
    /// Probability that it will spread (multiplied by number of tiles adjacent).
    ///
    /// I'd generally advise decreasing the UpdateInterval rather than increasing this, as it will look weird when
    /// multiple tiles are spreading at exactly the same time.
    /// </summary>
    [DataField]
    public float Probability = 0.01f;

    /// <summary>
    /// If true, the tiles listed in <see cref="SpreadsTo"/> will revert to <see cref="ID"/> when they are covered by a wall.
    /// </summary>
    [DataField]
    public bool DieUnderWalls = false;

    /// <summary>
    /// If <see cref="DieUnderWalls"/> is true, this is the probability that a tile will die when a wall is placed over it.
    /// </summary>
    [DataField]
    public float DeathProbability = 0.1f;

}
