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
        args.Handled = true;
        if (args.IsBiomeIntrinsic)
        {
            args.Unload = false; // Don't try to unload mobs, just pause them in the next phase
            args.Action = BiomeUnloadingEvent.EntAction.Ignore;
            return;
        }

        var mayBePlayer =
            TryComp<MindContainerComponent>(ent, out var mindCont) && mindCont.HasMind
            || HasComp<HumanoidAppearanceComponent>(ent)
            || HasComp<GhostRoleComponent>(ent);

        // Alive mobs just get unloaded and then brought back
        // Dead mobs are deleted completely if they're not a player
        var isAlive = ent.Comp.CurrentState is MobState.Alive;
        args.Unload &= isAlive;
        args.MarkTileModified = false;
        args.Action = isAlive || mayBePlayer ? BiomeUnloadingEvent.EntAction.None : BiomeUnloadingEvent.EntAction.Delete;
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
        args.Action = BiomeUnloadingEvent.EntAction.Delete;
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
    ///     What action to take regardless of whether we are unloading or marking as modified.
    /// </summary>
    public EntAction Action = EntAction.None;

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

    public enum EntAction
    {
        None,
        /// Delete the entity and forget it. This may override the unload option.
        Delete,
        /// Forget the entity, but don't delete. Only has special effect when unloading native entities. This will override the unload option.
        Ignore
    }
}
