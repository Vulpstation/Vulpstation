using Content.Server.Destructible;
using Content.Server.Gatherable.Components;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Whitelist;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Gatherable;

public sealed partial class GatherableSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelistSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GatherableComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<GatherableComponent, AttackedEvent>(OnAttacked);
        InitializeProjectile();
    }

    private void OnAttacked(Entity<GatherableComponent> gatherable, ref AttackedEvent args)
    {
        if (_whitelistSystem.IsWhitelistFailOrNull(gatherable.Comp.ToolWhitelist, args.Used))
            return;

        Gather(gatherable, args.User, used: args.Used); // Vulpstation - added the used arg
    }

    private void OnActivate(Entity<GatherableComponent> gatherable, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        if (_whitelistSystem.IsWhitelistFailOrNull(gatherable.Comp.ToolWhitelist, args.User))
            return;

        Gather(args.Target, args.User, gatherable.Comp);
        args.Handled = true;
    }

    // Vulpstation - added the used arg
    public void Gather(EntityUid gatheredUid, EntityUid? gatherer = null, GatherableComponent? component = null, EntityUid? used = null)
    {
        if (!Resolve(gatheredUid, ref component))
            return;

        // Vulpstation - calculate gathering efficiency
        var efficiency = CompOrNull<GathererComponent>(used)?.Efficiency ?? 1f;
        component.Toughness -= efficiency;

        // Vulpstation - sound volume depends on gathering efficiency - UNLESS the gatherable ends up being destroyed
        var soundVolume = component.Toughness <= 0 ? 0f : (-10 + MathF.Max(0f, efficiency) * 5f);
        if (TryComp<SoundOnGatherComponent>(gatheredUid, out var soundComp))
            _audio.PlayPvs(soundComp.Sound, Transform(gatheredUid).Coordinates, audioParams: soundComp.Sound.Params.AddVolume(soundVolume));

        // Vulpstation - don't actually gather in one hit.
        if (component.Toughness > 0)
            return;

        // Complete the gathering process
        _destructible.DestroyEntity(gatheredUid);

        // Spawn the loot!
        if (component.Loot == null)
            return;

        var pos = _transform.GetMapCoordinates(gatheredUid);

        foreach (var (tag, table) in component.Loot)
        {
            if (tag != "All")
            {
                if (gatherer != null && !_tagSystem.HasTag(gatherer.Value, tag))
                    continue;
            }
            var getLoot = _proto.Index(table);
            var spawnLoot = getLoot.GetSpawns(_random);
            var spawnPos = pos.Offset(_random.NextVector2(component.GatherOffset));
            Spawn(spawnLoot[0], spawnPos);
        }
    }
}
