using System.Numerics;
using Content.Server.Administration;
using Content.Server.Decals;
using Content.Shared.Administration;
using Content.Shared.Decals;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Toolshed;


namespace Content.Server._Vulp.Station.Systems;


public sealed partial class PlanetStationSystem
{
    [Dependency] private readonly DecalSystem _decals = default!;

    /// <summary>
    ///     Tries to merge source into target.
    /// </summary>
    public void MergeGrids(EntityUid target, EntityUid source)
    {
        if (!TryComp<MapGridComponent>(source, out var sourceGrid) || !TryComp<MapGridComponent>(target, out var targetGrid))
            return;

        var original = _xforms.GetWorldPositionRotation(Transform(source));
        // Round position and rotation
        _xforms.SetWorldPositionRotation(
            source,
            original.WorldPosition.Rounded(),
            original.WorldRotation.GetCardinalDir().ToAngle());

        var sourceMat = _xforms.GetWorldMatrix(source);
        var targetInvMat = _xforms.GetInvWorldMatrix(target);

        // GridFixtureSystem fails to transfer unanchored entities
        // Faster to do an all-entity query rather than use entity lookup
        var query = AllEntityQuery<TransformComponent>();
        var detachedEntities = new List<(EntityUid uid, Vector2 worldPos, Angle worldRot)>();
        while (query.MoveNext(out var uid, out var xform))
        {
            // Only entities on this grid that are directly parented to it (not in containers)
            // Also ignore anchored entities because those will be processed by the grid fixture system
            if (xform.GridUid != source || xform.ParentUid != source || MetaData(uid).Flags.HasFlag(MetaDataFlags.InContainer))
                continue;

            // ???
            if (HasComp<MapGridComponent>(uid))
                continue;

            // TODO change this to use the cached transform matrices
            var (position, rotation) = _xforms.GetWorldPositionRotation(xform);
            _xforms.DetachEntity(uid, xform);
            detachedEntities.Add((uid, position, rotation));
        }

        // Save decals. This is necessary because the target grid may lack the tiles to put them on at this point.
        var savedDecals = new List<(Vector2i pos, Decal decal)>();
        foreach (var (idx, decal) in _decals.GetAllDecals(source))
            savedDecals.Add((idx, decal));

        // Actually do the merge. This will prepare the tiles on the target grid and delete the source grid
        _gridFixtures.Merge(target, source, Transform(source).LocalMatrix);

        // Move saved entities over
        var targetXform = Transform(target);
        foreach (var entity in detachedEntities)
        {
            var xform = Transform(entity.uid);
            _xforms.SetParent(entity.uid, xform, target, EntityManager.TransformQuery, targetXform);
            _xforms.SetWorldPositionRotation(entity.uid, entity.worldPos, entity.worldRot, xform);
        }

        // Copy saved decals
        foreach (var (idx, decal) in savedDecals)
        {
            var decalCoords = Vector2.Transform(Vector2.Transform(decal.Coordinates, sourceMat), targetInvMat);
            var resultEntCoords = new EntityCoordinates(target, decalCoords);
            _decals.TryAddDecal(decal.WithCoordinates(decalCoords), resultEntCoords, out _);
        }
    }
}

[ToolshedCommand, AdminCommand(AdminFlags.Admin)]
public sealed class MergeGridCommand : ToolshedCommand
{
    [CommandImplementation("into")]
    public void MergeSafe(
        IInvocationContext ctx,
        [PipedArgument] EntityUid gridUid,
        [CommandArgument] EntityUid target)
    {
        if (Transform(target).MapUid != target)
            throw new ArgumentException("The target grid is not a map! Use into_UNSAFE to override safety. This might cause bugs or even crash the game if you aren't careful!");

        MergeUnsafe(ctx, gridUid, target);
    }

    [CommandImplementation("into_UNSAFE")]
    public void MergeUnsafe(
        IInvocationContext ctx,
        [PipedArgument] EntityUid gridUid,
        [CommandArgument] EntityUid target)
    {
        if (!TryComp(gridUid, out MapGridComponent? grid))
            throw new ArgumentException("Piped argument (source grid) is not a grid");

        if (!TryComp(target, out MapGridComponent? targetGrid))
            throw new ArgumentException("First argument (target grid) is not a grid");

        EntityManager.System<PlanetStationSystem>().MergeGrids(target, gridUid);
    }
}
