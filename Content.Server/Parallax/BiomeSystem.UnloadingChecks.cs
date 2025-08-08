using Content.Server.NodeContainer;
using Content.Server.Storage.Components;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Fluids.Components;
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
        SubscribeLocalEvent<AnchorableComponent, BiomeUnloadingEvent>(OnAnchorableUnloading);
        SubscribeLocalEvent<PuddleComponent, BiomeUnloadingEvent>(OnPuddleUnloading);
        // Note: non-anchored entities are not currently eligible for unloading, so we don't need to worry about them
        // Put new subscriptions ABOVE this line. Base unloading checks should always be last as they are the fallback scenario.
        SubscribeLocalEvent<MetaDataComponent, BiomeUnloadingEvent>(BaseUnloadingChecks);
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
        // Alive mobs just get unloaded and then brought back
        // Dead mobs are deleted completely
        var isAlive = ent.Comp.CurrentState is MobState.Alive;
        args.Unload &= isAlive;
        args.Delete |= !isAlive;
        args.Handled = true;
    }

    private void OnAnchorableUnloading(Entity<AnchorableComponent> ent, ref BiomeUnloadingEvent args)
    {
        args.Unload &= args.IsSameTile && Transform(ent).Anchored;
        args.MarkTileModified |= !args.IsSameTile;
    }

    private void OnPuddleUnloading(Entity<PuddleComponent> ent, ref BiomeUnloadingEvent args)
    {
        // Fuck puddles, man
        args.Unload = false;
        args.Delete = true;
        args.Handled = true;
    }
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

    public readonly bool IsSameTile;

    public BiomeUnloadingEvent(bool isSameTile)
    {
        IsSameTile = isSameTile;
    }
}
