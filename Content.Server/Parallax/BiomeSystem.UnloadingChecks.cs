using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Fluids.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;


namespace Content.Server.Parallax;

// This file is part of floofstation changes
public sealed partial class BiomeSystem
{
    private void InitializeUnloadingChecks()
    {
        SubscribeLocalEvent<MetaDataComponent, BiomeUnloadingEvent>(BaseUnloadingChecks);
        SubscribeLocalEvent<MobStateComponent, BiomeUnloadingEvent>(OnMobUnloading);
        SubscribeLocalEvent<AnchorableComponent, BiomeUnloadingEvent>(OnAnchorableUnloading);
        SubscribeLocalEvent<PuddleComponent, BiomeUnloadingEvent>(OnPuddleUnloading);
    }

    private void BaseUnloadingChecks(Entity<MetaDataComponent> ent, ref BiomeUnloadingEvent args)
    {
        if (!args.Unload)
            return;

        var uid = ent.Owner;
        if (EntityManager.ComponentCount(uid) > 20)
            args.Unload = false; // Just fuck saving that
        else if (HasComp<ContainerManagerComponent>(uid))
            args.Unload = false; // Yeah no that'd require map serilization at least
        else if (HasComp<ItemSlotsComponent>(uid))
            args.Unload = false;
    }

    private void OnMobUnloading(Entity<MobStateComponent> ent, ref BiomeUnloadingEvent args)
    {
        // Alive mobs just gen unloaded and then brought back
        // Dead mobs are deleted completely
        var isAlive = ent.Comp.CurrentState is MobState.Alive;
        args.Unload &= isAlive;
        args.Delete |= !isAlive;
    }

    private void OnAnchorableUnloading(Entity<AnchorableComponent> ent, ref BiomeUnloadingEvent args)
    {
        args.Unload &= args.IsSameTile && Transform(ent).Anchored;
        args.MarkTileModified |= !args.IsSameTile;
    }

    private void OnPuddleUnloading(Entity<PuddleComponent> ent, ref BiomeUnloadingEvent args)
    {
        // Fuck puddles, man
        args.Delete = true;
    }
}

// Vulpstation
/// <summary>
///     Raised on an entity during chunk unloading to determine if the entity needs to be unloaded, deleted, or ignored.
///     If both fields are false, the entity will remain on the map in-between unloaded chunks.
/// </summary>
[ByRefEvent]
public sealed class BiomeUnloadingEvent
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

    public bool IsSameTile;
}
