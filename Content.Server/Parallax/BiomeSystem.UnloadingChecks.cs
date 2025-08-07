using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;


namespace Content.Server.Parallax;

// This file is part of floofstation changes
public sealed partial class BiomeSystem
{
    private void InitializeUnloadingChecks()
    {
        SubscribeLocalEvent<BiomeUnloadingEvent>(BaseUnloadingChecks);
        SubscribeLocalEvent<MobStateComponent, BiomeUnloadingEvent>(OnMobUnloading);
        SubscribeLocalEvent<AnchorableComponent, BiomeUnloadingEvent>(OnAnchorableUnloading);
    }

    private void BaseUnloadingChecks(EntityUid uid, BiomeUnloadingEvent args)
    {
        var savable = true;
        if (EntityManager.ComponentCount(uid) > 20)
            savable = false; // Just fuck saving that
        else if (HasComp<ContainerManagerComponent>(uid))
            savable = false; // Yeah no that'd require map serilization at least
        else if (HasComp<ItemSlotsComponent>(uid))
            savable = false;

        args.Unload = args.Unload && savable;
        // Anchorable entities will be set to have their tile marked as modified below
        // Unanchored entities and items can be just left on the ground
    }

    private void OnMobUnloading(Entity<MobStateComponent> ent, ref BiomeUnloadingEvent args)
    {
        // Alive mobs just gen unloaded and then brought back
        // Dead mobs are deleted completely
        var isAlive = ent.Comp.CurrentState is MobState.Alive;
        args.Unload = isAlive;
        args.Delete = !isAlive;
    }

    private void OnAnchorableUnloading(Entity<AnchorableComponent> ent, ref BiomeUnloadingEvent args)
    {
        args.Unload = args.IsSameTile && Transform(ent).Anchored;
        args.MarkTileModified = !args.IsSameTile;
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
