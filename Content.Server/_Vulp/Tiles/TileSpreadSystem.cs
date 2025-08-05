using System.Linq;
using Content.Server.Atmos.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Vulp.Tiles;

public sealed class TileSpreadSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private static Vector2i[] _adjacentTiles = [ Vector2i.Up, Vector2i.Down, Vector2i.Left, Vector2i.Right ];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TileSpreadComponent, ComponentInit>(OnComponentInit);
    }

    private void OnComponentInit(EntityUid uid, TileSpreadComponent component, ComponentInit args) =>
        component.NextUpdate = _gameTiming.CurTime + component.UpdateInterval;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<TileSpreadComponent, MapGridComponent>();
        while (query.MoveNext(out var uid, out var tileSpread, out var mapGrid))
        {
            if (tileSpread.NextUpdate > _gameTiming.CurTime)
                continue;
            tileSpread.NextUpdate += tileSpread.UpdateInterval;

            // track changed tiles so we don't count newly-spread tiles as adjacent
            var changed = new List<Vector2i>();
            foreach (var tile in _map.GetAllTiles(uid, mapGrid))
            {
                // don't spread under walls
                if (_map.GetAnchoredEntities(new(uid, mapGrid), tile.GridIndices).Any(HasComp<AirtightComponent>))
                {
                    var tileType = _tileDefs[tile.Tile.TypeId].ID;
                    var def = tileSpread.Tiles.FirstOrDefault(it => it.ID == tileType);
                    if (def == null || !def.DieUnderWalls || def.SpreadsTo.Length == 0)
                        continue;

                    // Revert the tile back to a random base tile
                    if (!_random.Prob(def.Probability))
                        continue;

                    var baseTile = _random.Pick(def.SpreadsTo);
                    _map.SetTile(uid, mapGrid, tile.GridIndices, new(_tileDefs[baseTile].TileId));

                    continue;
                }

                var chosenTile = tileSpread.Tiles
                    .Where(info => info.SpreadsTo.Contains(_tileDefs[tile.Tile.TypeId].ID))
                    .Aggregate(
                        (id: (string?) null, roll: float.PositiveInfinity),
                        (candidate, eligible) =>
                        {
                            // probability is multiplied by the number of adjacent tiles of the type we want
                            var probability = _adjacentTiles.Sum(offset =>
                            {
                                // if the adjacent tile was just changed, we shouldn't count it
                                if (changed.Contains(tile.GridIndices + offset))
                                    return 0.0f;

                                var adjacent = _map.GetTileRef(uid, mapGrid, tile.GridIndices + offset);
                                if (_tileDefs[adjacent.Tile.TypeId].ID != eligible.ID)
                                    return 0.0f;

                                return eligible.Probability;
                            });

                            var roll = _random.NextFloat();
                            // since multiple eligible tile types might be competing to spread to this one,
                            // we should give them a fair chance (weighted by spread probability)
                            if (roll < probability && roll / probability < candidate.roll)
                                return (eligible.ID, roll / probability);

                            return candidate;
                        })
                    .id;

                if (chosenTile is null)
                    continue;

                _map.SetTile(uid, mapGrid, tile.GridIndices, new(_tileDefs[chosenTile].TileId));
                changed.Add(tile.GridIndices);
            }
        }
    }
}
