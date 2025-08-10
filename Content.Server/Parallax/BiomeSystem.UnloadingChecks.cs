using Content.Server.Ghost.Roles.Components;
using Content.Server.NodeContainer;
using Content.Server.Storage.Components;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Fluids.Components;
using Content.Shared.Humanoid;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;


namespace Content.Server.Parallax;

// This file is part of floofstation changes
public sealed partial class BiomeSystem
{
    private void InitializeUnloadingChecks()
    {
        SubscribeLocalEvent<MobStateComponent, BiomeUnloadingEvent>(OnMobUnloading);
        SubscribeLocalEvent<TransformComponent, BiomeUnloadingEvent>(OnAnchorableUnloading);
        SubscribeLocalEvent<PuddleComponent, BiomeUnloadingEvent>(OnPuddleUnloading);
        // Base checks must always come last, so we enforce ordering like this
        // I could just broadcast the event and subscribe to the broadcast version here, but I'm afraid that can cause performance issues
        EntityManager.EventBus.SubscribeLocalEvent<MetaDataComponent, BiomeUnloadingEvent>(
            BaseUnloadingChecks, typeof(FakeEntitySubscriber), after: [typeof(BiomeSystem)]);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        EntityManager.EventBus.UnsubscribeLocalEvent<MetaDataComponent, BiomeUnloadingEvent>();
    }

    private void BaseUnloadingChecks(Entity<MetaDataComponent> ent, ref BiomeUnloadingEvent args)
    {
        // This should always be called last
        if (!args.Unload || args.Handled)
            return;

        var uid = ent.Owner;
        if ((HasComp<ContainerManagerComponent>(uid))
            || HasComp<ItemSlotsComponent>(uid)
            || HasComp<EntityStorageComponent>(uid)
            || HasComp<NodeContainerComponent>(uid)) // May be a part of a network (power, atmos) or something like AME
        {
            args.Unload = false;
            args.MarkTileModified = true;
        }
    }

    private void OnMobUnloading(Entity<MobStateComponent> ent, ref BiomeUnloadingEvent args)
    {
        if (args.IsBiomeIntrinsic)
            return; // Don't try to unload mobs, just pause them in the next phase

        var mayBePlayer =
            TryComp<MindContainerComponent>(ent, out var mindCont) && mindCont.HasMind
            || HasComp<HumanoidAppearanceComponent>(ent)
            || HasComp<GhostRoleComponent>(ent);

        // Alive mobs just get unloaded and then brought back
        // Dead mobs are deleted completely if they're not a player
        var isAlive = ent.Comp.CurrentState is MobState.Alive;
        args.Unload &= isAlive;
        args.Delete |= !isAlive && !mayBePlayer;
        args.MarkTileModified = false;
        args.Handled = true;
    }

    private void OnAnchorableUnloading(Entity<TransformComponent> ent, ref BiomeUnloadingEvent args)
    {
        if (!ent.Comp.Anchored)
        {
            // This means this is an attempt to pause this entity. Allow it.
            args.Unload = true;
            return;
        }

        // This is an anchored entity, only unload it if it's intrinsic to the biome
        args.Unload &= args.IsBiomeIntrinsic;
        args.MarkTileModified |= !args.IsBiomeIntrinsic;
    }

    private void OnPuddleUnloading(Entity<PuddleComponent> ent, ref BiomeUnloadingEvent args)
    {
        // Fuck puddles, man
        args.Unload = false;
        args.Delete = true;
        args.Handled = true;
    }

    private sealed class FakeEntitySubscriber : IEntityEventSubscriber;
}

// Vulpstation
/// <summary>
///     Raised on an entity during chunk unloading to determine if the entity needs to be unloaded, deleted, or ignored.
///     If both fields are false, the entity will remain on the map in-between unloaded chunks.
/// </summary>
[ByRefEvent]
public struct BiomeUnloadingEvent
{
    /// <summary>
    ///     If true, the entity should be deleted and then re-generated when the chunk gets loaded back.
    /// </summary>
    public bool Unload = true;

    /// <summary>
    ///     If true, the entity should be deleted and forgotten about.
    /// </summary>
    public bool Delete = false;

    /// <summary>
    ///     If true, the tile this entity was spawned from should be marked as modified.
    ///     This WILL conflict with Unload by preventing the entity from being spawned back.
    /// </summary>
    public bool MarkTileModified = false;

    public bool Handled = true;

    public readonly bool IsBiomeIntrinsic;

    public BiomeUnloadingEvent(bool isBiomeIntrinsic)
    {
        IsBiomeIntrinsic = isBiomeIntrinsic;
    }
}
