using System.Linq;
using System.Numerics;
using Content.Shared.Coordinates.Helpers;
using Content.Shared.Decals;
using Content.Shared.Parallax.Biomes;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Shared.Maps;

/// <summary>
///     Handles server-side tile manipulation like prying/deconstructing tiles.
/// </summary>
public sealed class TileSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IRobustRandom _robustRandom = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefinitionManager = default!;
    [Dependency] private readonly SharedDecalSystem _decal = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly SharedBiomeSystem _biome = default!;

    /// <summary>
    ///     Returns a weighted pick of a tile variant.
    /// </summary>
    public byte PickVariant(ContentTileDefinition tile)
    {
        return PickVariant(tile, _robustRandom.GetRandom());
    }

    /// <summary>
    ///     Returns a weighted pick of a tile variant.
    /// </summary>
    public byte PickVariant(ContentTileDefinition tile, int seed)
    {
        var rand = new System.Random(seed);
        return PickVariant(tile, rand);
    }

    /// <summary>
    ///     Returns a weighted pick of a tile variant.
    /// </summary>
    public byte PickVariant(ContentTileDefinition tile, System.Random random)
    {
        var variants = tile.PlacementVariants;

        var sum = variants.Sum();
        var accumulated = 0f;
        var rand = random.NextFloat() * sum;

        for (byte i = 0; i < variants.Length; ++i)
        {
            accumulated += variants[i];

            if (accumulated >= rand)
                return i;
        }

        // Shouldn't happen
        throw new InvalidOperationException($"Invalid weighted variantize tile pick for {tile.ID}!");
    }

    /// <summary>
    ///     Returns a tile with a weighted random variant.
    /// </summary>
    public Tile GetVariantTile(ContentTileDefinition tile, System.Random random)
    {
        return new Tile(tile.TileId, variant: PickVariant(tile, random));
    }

    /// <summary>
    ///     Returns a tile with a weighted random variant.
    /// </summary>
    public Tile GetVariantTile(ContentTileDefinition tile, int seed)
    {
        var rand = new System.Random(seed);
        return new Tile(tile.TileId, variant: PickVariant(tile, rand));
    }

    public bool PryTile(Vector2i indices, EntityUid gridId)
    {
        var grid = Comp<MapGridComponent>(gridId);
        var tileRef = _maps.GetTileRef(gridId, grid, indices);
        return PryTile(tileRef);
    }

	public bool PryTile(TileRef tileRef)
    {
        return PryTile(tileRef, false);
    }

    public bool PryTile(TileRef tileRef, bool pryPlating)
    {
        var tile = tileRef.Tile;

        if (tile.IsEmpty)
            return false;

        var tileDef = (ContentTileDefinition) _tileDefinitionManager[tile.TypeId];

        if (!tileDef.CanCrowbar)
            return false;

        return DeconstructTile(tileRef);
    }
    // Delta V
    public bool DigTile(TileRef tileRef)
    {
        var tile = tileRef.Tile;

        if (tile.IsEmpty)
            return false;

        var tileDef = (ContentTileDefinition) _tileDefinitionManager[tile.TypeId];

        if (!tileDef.CanShovel)
            return false;

        return DeconstructTile(tileRef);
    }
    // Delta V
    public bool ReplaceTile(TileRef tileref, ContentTileDefinition replacementTile)
    {
        if (!TryComp<MapGridComponent>(tileref.GridUid, out var grid))
            return false;
        return ReplaceTile(tileref, replacementTile, tileref.GridUid, grid);
    }

    public bool ReplaceTile(TileRef tileref, ContentTileDefinition replacementTile, EntityUid grid, MapGridComponent? component = null)
    {
        DebugTools.Assert(tileref.GridUid == grid);

        if (!Resolve(grid, ref component))
            return false;


        var variant = PickVariant(replacementTile);
        var decals = _decal.GetDecalsInRange(tileref.GridUid, _turf.GetTileCenter(tileref).Position, 0.5f);
        foreach (var (id, _) in decals)
        {
            _decal.RemoveDecal(tileref.GridUid, id);
        }

        _maps.SetTile(grid, component, tileref.GridIndices, new Tile(replacementTile.TileId, 0, variant));
        return true;
    }

    // Vulp
    public ContentTileDefinition GetBasestTurf(ContentTileDefinition tile) =>
        // prevent infinite loops by self-reference
        // does not prevent longer infinite loops
        tile.BaseTurf != tile.ID && _tileDefinitionManager.TryGetDefinition(tile.BaseTurf, out var baseTile)
            ? GetBasestTurf((ContentTileDefinition) baseTile)
            : tile;

    public string BasestTurfOrLatticeForGridTile(EntityUid gridUid, Vector2i indices)
    {
        if (TryComp<BiomeComponent>(gridUid, out var biome)
            && _biome.TryGetTile(indices, biome.Layers, biome.Seed, null, out var tile))
            return GetBasestTurf(tile.Value.GetContentTileDefinition(_tileDefinitionManager)).ID;
        return "Lattice";
    }

    public ITileDefinition BaseTurfForGridTile(TileRef tileRef)
    {
        var plating = _tileDefinitionManager[tileRef.Tile.GetContentTileDefinition(_tileDefinitionManager).BaseTurf];

        if (plating == _tileDefinitionManager["Lattice"])
            return _tileDefinitionManager[BasestTurfOrLatticeForGridTile(tileRef.GridUid, tileRef.GridIndices)];

        return plating;
    }

    public bool DeconstructTile(TileRef tileRef)
    {
        if (tileRef.Tile.IsEmpty)
            return false;

        var tileDef = (ContentTileDefinition) _tileDefinitionManager[tileRef.Tile.TypeId];

        if (string.IsNullOrEmpty(tileDef.BaseTurf))
            return false;

        var gridUid = tileRef.GridUid;
        var mapGrid = Comp<MapGridComponent>(gridUid);

        const float margin = 0.1f;
        var bounds = mapGrid.TileSize - margin * 2;
        var indices = tileRef.GridIndices;
        var coordinates = _maps.GridTileToLocal(gridUid, mapGrid, indices)
            .Offset(new Vector2(
                (_robustRandom.NextFloat() - 0.5f) * bounds,
                (_robustRandom.NextFloat() - 0.5f) * bounds));

        //Actually spawn the relevant tile item at the right position and give it some random offset.
        var tileItem = Spawn(tileDef.ItemDropPrototypeName, coordinates);
        Transform(tileItem).LocalRotation = _robustRandom.NextDouble() * Math.Tau;

        // Destroy any decals on the tile
        var decals = _decal.GetDecalsInRange(gridUid, coordinates.SnapToGrid(EntityManager, _mapManager).Position, 0.5f);
        foreach (var (id, _) in decals)
        {
            _decal.RemoveDecal(tileRef.GridUid, id);
        }

        // Vulp
        var plating = BaseTurfForGridTile(tileRef);

        _maps.SetTile(gridUid, mapGrid, tileRef.GridIndices, new Tile(plating.TileId));

        return true;
    }
}
