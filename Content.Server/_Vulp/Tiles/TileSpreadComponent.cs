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
}
