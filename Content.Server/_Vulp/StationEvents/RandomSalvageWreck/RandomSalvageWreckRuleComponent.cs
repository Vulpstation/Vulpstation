using System.Numerics;
using Content.Server.StationEvents.Events;
using Robust.Shared.Audio;
using Robust.Shared.Map;


namespace Content.Server._Vulp.StationEvents.RandomSalvageWreck;

[RegisterComponent]
public sealed partial class RandomSalvageWreckRuleComponent : Component
{
    [DataField]
    public Vector2i DebrisCountRange = new(1, 2);

    [DataField]
    public Vector2 DebrisDistanceRange = new(750f, 1250f), DebrisOffsetRange = new(50f, 100f);

    [NonSerialized, ViewVariables]
    public List<MapId> TemporaryMaps = new();

    [NonSerialized, ViewVariables]
    public MapId? TargetMap;

    [DataField]
    public SoundSpecifier EndSound = new SoundCollectionSpecifier("ExplosionFar", AudioParams.Default.WithVolume(5f));
}
