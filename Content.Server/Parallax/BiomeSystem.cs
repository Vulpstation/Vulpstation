using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Atmos;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._Vulp;
using Content.Shared.Atmos;
using Content.Shared.Construction.Components;
using Content.Shared.Decals;
using Content.Shared.Ghost;
using Content.Shared.Gravity;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Layers;
using Content.Shared.Parallax.Biomes.Markers;
using Microsoft.Extensions.ObjectPool;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Collections;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Threading;
using Robust.Shared.Utility;
using ChunkIndicesEnumerator = Robust.Shared.Map.Enumerators.ChunkIndicesEnumerator;

namespace Content.Server.Parallax;

public sealed partial class BiomeSystem : SharedBiomeSystem
{
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IConsoleHost _console = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IParallelManager _parallel = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly DecalSystem _decals = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShuttleSystem _shuttles = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDef = default!; // Vulpstation
    [Dependency] private readonly EntityLookupSystem _lookup = default!; // Vulpstation
    [Dependency] private readonly MetaDataSystem _meta = default!; // Vulpstation

    private EntityQuery<BiomeComponent> _biomeQuery;
    private EntityQuery<FixturesComponent> _fixturesQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<GhostComponent> _ghostQuery; // Vulpstation
    private EntityQuery<GridAtmosphereComponent> _gridAtmosQuery; // Vulpstation
    private EntityQuery<MapAtmosphereComponent> _mapAtmosQuery; // Vulpstation
    private EntityQuery<MetaDataComponent> _metaQuery; // Vulpstation

    private readonly HashSet<EntityUid> _handledEntities = new();
    private const float DefaultLoadRange = 16f;
    private float _loadRange = DefaultLoadRange;

    private List<(Vector2i, Tile)> _tiles = new();

    private ObjectPool<HashSet<Vector2i>> _tilePool =
        new DefaultObjectPool<HashSet<Vector2i>>(new SetPolicy<Vector2i>(), 256);

    private ObjectPool<Dictionary<Vector2i, GasMixture>> _gasPool = // Vulpstation
        new DefaultObjectPool<Dictionary<Vector2i, GasMixture>>(new DictPolicy<Vector2i, GasMixture>(), 256);

    /// <summary>
    /// Load area for chunks containing tiles, decals etc.
    /// </summary>
    private Box2 _loadArea = new(-DefaultLoadRange, -DefaultLoadRange, DefaultLoadRange, DefaultLoadRange);

    /// <summary>
    /// Stores the chunks active for this tick temporarily.
    /// </summary>
    private readonly Dictionary<BiomeComponent, HashSet<Vector2i>> _activeChunks = new();

    private readonly Dictionary<BiomeComponent,
        Dictionary<string, HashSet<Vector2i>>> _markerChunks = new();

    public override void Initialize()
    {
        base.Initialize();
        Log.Level = LogLevel.Debug;
        _biomeQuery = GetEntityQuery<BiomeComponent>();
        _fixturesQuery = GetEntityQuery<FixturesComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _ghostQuery = GetEntityQuery<GhostComponent>(); // Vulpstation
        _mapAtmosQuery = GetEntityQuery<MapAtmosphereComponent>(); // Vulpstation
        _gridAtmosQuery = GetEntityQuery<GridAtmosphereComponent>(); // Vulpstation
        _metaQuery = GetEntityQuery<MetaDataComponent>(); // Vulpstation
        SubscribeLocalEvent<BiomeComponent, MapInitEvent>(OnBiomeMapInit);
        SubscribeLocalEvent<FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<ShuttleFlattenEvent>(OnShuttleFlatten);
        Subs.CVar(_configManager, VulpCCVars.BiomeLoadingRange, SetLoadRange, true);
        InitializeCommands();
        InitializeUnloadingChecks(); // Vulpstation
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(ProtoReload);
    }

    private void ProtoReload(PrototypesReloadedEventArgs obj)
    {
        if (!obj.ByType.TryGetValue(typeof(BiomeTemplatePrototype), out var reloads))
            return;

        var query = AllEntityQuery<BiomeComponent>();

        while (query.MoveNext(out var uid, out var biome))
        {
            if (biome.Template == null || !reloads.Modified.TryGetValue(biome.Template, out var proto))
                continue;

            SetTemplate(uid, biome, (BiomeTemplatePrototype) proto);
        }
    }

    private void SetLoadRange(float obj)
    {
        // Round it up
        _loadRange = MathF.Ceiling(obj / ChunkSize) * ChunkSize;
        _loadArea = Box2.CentredAroundZero(Vector2.Create(_loadRange)); // Vulpstation - fix ugly math that doubled the size of the box
    }

    private void OnBiomeMapInit(EntityUid uid, BiomeComponent component, MapInitEvent args)
    {
        if (component.Seed == -1)
        {
            SetSeed(uid, component, _random.Next());
        }

        var xform = Transform(uid);
        var mapId = xform.MapID;

        if (mapId != MapId.Nullspace && TryComp(uid, out MapGridComponent? mapGrid))
        {
            var setTiles = new List<(Vector2i Index, Tile tile)>();

            foreach (var grid in _mapManager.GetAllMapGrids(mapId))
            {
                var gridUid = grid.Owner;

                if (!_fixturesQuery.TryGetComponent(gridUid, out var fixtures))
                    continue;

                // Don't want shuttles flying around now do we.
                _shuttles.Disable(gridUid);
                var pTransform = _physics.GetPhysicsTransform(gridUid);

                foreach (var fixture in fixtures.Fixtures.Values)
                {
                    for (var i = 0; i < fixture.Shape.ChildCount; i++)
                    {
                        var aabb = fixture.Shape.ComputeAABB(pTransform, i);

                        setTiles.Clear();
                        ReserveTiles(uid, aabb, setTiles);
                    }
                }
            }
        }
    }

    public void SetSeed(EntityUid uid, BiomeComponent component, int seed, bool dirty = true)
    {
        component.Seed = seed;

        if (dirty)
            Dirty(uid, component);
    }

    public void ClearTemplate(EntityUid uid, BiomeComponent component, bool dirty = true)
    {
        component.Layers.Clear();
        component.Template = null;

        if (dirty)
            Dirty(uid, component);
    }

    /// <summary>
    /// Sets the <see cref="BiomeComponent.Template"/> and refreshes layers.
    /// </summary>
    public void SetTemplate(EntityUid uid, BiomeComponent component, BiomeTemplatePrototype template, bool dirty = true)
    {
        component.Layers.Clear();
        component.Template = template.ID;

        foreach (var layer in template.Layers)
        {
            component.Layers.Add(layer);
        }

        if (dirty)
            Dirty(uid, component);
    }

    /// <summary>
    /// Adds the specified layer at the specified marker if it exists.
    /// </summary>
    public void AddLayer(EntityUid uid, BiomeComponent component, string id, IBiomeLayer addedLayer, int seedOffset = 0)
    {
        for (var i = 0; i < component.Layers.Count; i++)
        {
            var layer = component.Layers[i];

            if (layer is not BiomeDummyLayer dummy || dummy.ID != id)
                continue;

            addedLayer.Noise.SetSeed(addedLayer.Noise.GetSeed() + seedOffset);
            component.Layers.Insert(i, addedLayer);
            break;
        }

        Dirty(uid, component);
    }

    public void AddMarkerLayer(EntityUid uid, BiomeComponent component, string marker)
    {
        component.MarkerLayers.Add(marker);
        Dirty(uid, component);
    }

    /// <summary>
    /// Adds the specified template at the specified marker if it exists, withour overriding every layer.
    /// </summary>
    public void AddTemplate(EntityUid uid, BiomeComponent component, string id, BiomeTemplatePrototype template, int seedOffset = 0)
    {
        for (var i = 0; i < component.Layers.Count; i++)
        {
            var layer = component.Layers[i];

            if (layer is not BiomeDummyLayer dummy || dummy.ID != id)
                continue;

            for (var j = template.Layers.Count - 1; j >= 0; j--)
            {
                var addedLayer = template.Layers[j];
                addedLayer.Noise.SetSeed(addedLayer.Noise.GetSeed() + seedOffset);
                component.Layers.Insert(i, addedLayer);
            }

            break;
        }

        Dirty(uid, component);
    }

    private void OnFTLStarted(ref FTLStartedEvent ev)
    {
        var targetMap = ev.TargetCoordinates.ToMap(EntityManager, _transform);
        var targetMapUid = _mapManager.GetMapEntityId(targetMap.MapId);

        if (!TryComp<BiomeComponent>(targetMapUid, out var biome))
            return;

        var preloadArea = new Vector2(32f, 32f);
        var targetArea = new Box2(targetMap.Position - preloadArea, targetMap.Position + preloadArea);
        Preload(targetMapUid, biome, targetArea);
    }

    private void OnShuttleFlatten(ref ShuttleFlattenEvent ev)
    {
        if (!TryComp<BiomeComponent>(ev.MapUid, out var biome) ||
            !TryComp<MapGridComponent>(ev.MapUid, out var grid))
        {
            return;
        }

        var tiles = new List<(Vector2i Index, Tile Tile)>();

        foreach (var aabb in ev.AABBs)
        {
            for (var x = Math.Floor(aabb.Left); x <= Math.Ceiling(aabb.Right); x++)
            {
                for (var y = Math.Floor(aabb.Bottom); y <= Math.Ceiling(aabb.Top); y++)
                {
                    var index = new Vector2i((int) x, (int) y);
                    var chunk = SharedMapSystem.GetChunkIndices(index, ChunkSize);

                    var mod = biome.ModifiedTiles.GetOrNew(chunk * ChunkSize);

                    if (!mod.Add(index) || !TryGetBiomeTile(index, biome.Layers, biome.Seed, grid, out var tile))
                        continue;

                    // If we flag it as modified then the tile is never set so need to do it ourselves.
                    tiles.Add((index, tile.Value));
                }
            }
        }

        _mapSystem.SetTiles(ev.MapUid, grid, tiles);
    }

    /// <summary>
    /// Preloads biome for the specified area.
    /// </summary>
    public void Preload(EntityUid uid, BiomeComponent component, Box2 area)
    {
        var markers = component.MarkerLayers;
        var goobers = _markerChunks.GetOrNew(component);

        foreach (var layer in markers)
        {
            var proto = ProtoManager.Index(layer);
            var enumerator = new ChunkIndicesEnumerator(area, proto.Size);

            while (enumerator.MoveNext(out var chunk))
            {
                var chunkOrigin = chunk * proto.Size;
                var layerChunks = goobers.GetOrNew(proto.ID);
                layerChunks.Add(chunkOrigin.Value);
            }
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var biomes = AllEntityQuery<BiomeComponent>();

        while (biomes.MoveNext(out var biome))
        {
            _activeChunks[biome] = _tilePool.Get(); // Vulpstation - will those *** ever stop using Dictionary.Add?!
            _markerChunks.GetOrNew(biome);
        }

        // Get chunks in range
        foreach (var pSession in Filter.GetAllPlayers(_playerManager))
        {
            // Vulp - don't allow ghosts to generate terrain, except admin ghosts
            if (_ghostQuery.TryComp(pSession.AttachedEntity, out var ghost) && !ghost.CanGhostInteract)
                continue;

            if (_xformQuery.TryGetComponent(pSession.AttachedEntity, out var xform) &&
                _handledEntities.Add(pSession.AttachedEntity.Value) &&
                 _biomeQuery.TryGetComponent(xform.MapUid, out var biome) &&
                biome.Enabled)
            {
                var worldPos = _transform.GetWorldPosition(xform);
                AddChunksInRange(biome, worldPos);

                foreach (var layer in biome.MarkerLayers)
                {
                    var layerProto = ProtoManager.Index(layer);
                    AddMarkerChunksInRange(biome, worldPos, layerProto);
                }
            }

            foreach (var viewer in pSession.ViewSubscriptions)
            {
                if (!_handledEntities.Add(viewer) ||
                    _ghostQuery.TryComp(viewer, out ghost) && !ghost.CanGhostInteract || // Vulp - don't allow ghosts to generate terrain, except admin ghosts
                    !_xformQuery.TryGetComponent(viewer, out xform) ||
                    !_biomeQuery.TryGetComponent(xform.MapUid, out biome) ||
                    !biome.Enabled)
                {
                    continue;
                }

                var worldPos = _transform.GetWorldPosition(xform);
                AddChunksInRange(biome, worldPos);

                foreach (var layer in biome.MarkerLayers)
                {
                    var layerProto = ProtoManager.Index(layer);
                    AddMarkerChunksInRange(biome, worldPos, layerProto);
                }
            }
        }

        var loadBiomes = AllEntityQuery<BiomeComponent, MapGridComponent>();

        while (loadBiomes.MoveNext(out var gridUid, out var biome, out var grid))
        {
            if (!biome.Enabled)
                continue;

            // Load new chunks
            LoadChunks(biome, gridUid, grid, biome.Seed);
            // Unload old chunks
            UnloadChunks(biome, gridUid, grid, biome.Seed);
        }

        _handledEntities.Clear();

        foreach (var tiles in _activeChunks.Values)
        {
            _tilePool.Return(tiles);
        }

        _activeChunks.Clear();
        _markerChunks.Clear();
    }

    private void AddChunksInRange(BiomeComponent biome, Vector2 worldPos)
    {
        var enumerator = new ChunkIndicesEnumerator(_loadArea.Translated(worldPos), ChunkSize);

        while (enumerator.MoveNext(out var chunkOrigin))
        {
            _activeChunks[biome].Add(chunkOrigin.Value * ChunkSize);
        }
    }

    private void AddMarkerChunksInRange(BiomeComponent biome, Vector2 worldPos, IBiomeMarkerLayer layer)
    {
        // Offset the load area so it's centralised.
        var loadArea = new Box2(0, 0, layer.Size, layer.Size);
        var halfLayer = new Vector2(layer.Size / 2f);

        var enumerator = new ChunkIndicesEnumerator(loadArea.Translated(worldPos - halfLayer), layer.Size);

        while (enumerator.MoveNext(out var chunkOrigin))
        {
            var lay = _markerChunks[biome].GetOrNew(layer.ID);
            lay.Add(chunkOrigin.Value * layer.Size);
        }
    }

    #region Load

    /// <summary>
    /// Loads all of the chunks for a particular biome, as well as handle any marker chunks.
    /// </summary>
    private void LoadChunks(
        BiomeComponent component,
        EntityUid gridUid,
        MapGridComponent grid,
        int seed)
    {

        BuildMarkerChunks(component, gridUid, grid, seed);

        var active = _activeChunks[component];

        foreach (var chunk in active)
        {
            LoadChunkMarkers(component, gridUid, grid, chunk, seed);

            if (!component.LoadedChunks.Add(chunk))
                continue;

            // Load NOW!
            LoadChunk(component, gridUid, grid, chunk, seed);
        }
    }

    /// <summary>
    /// Goes through all marker chunks that haven't been calculated, then calculates what spawns there are and
    /// allocates them to the relevant actual chunks in the biome (marker chunks may be many times larger than biome chunks).
    /// </summary>
    private void BuildMarkerChunks(BiomeComponent component, EntityUid gridUid, MapGridComponent grid, int seed)
    {
        var markers = _markerChunks[component];
        var loadedMarkers = component.LoadedMarkers;
        var idx = 0;

        foreach (var (layer, chunks) in markers)
        {
            // I know dictionary ordering isn't guaranteed but I just need something to differentiate seeds.
            idx++;
            var localIdx = idx;

            Parallel.ForEach(chunks, new ParallelOptions() { MaxDegreeOfParallelism = _parallel.ParallelProcessCount }, chunk =>
            {
                if (loadedMarkers.TryGetValue(layer, out var mobChunks) && mobChunks.Contains(chunk))
                    return;

                var forced = component.ForcedMarkerLayers.Contains(layer);

                // Make a temporary version and copy back in later.
                var pending = new Dictionary<Vector2i, Dictionary<string, List<Vector2i>>>();

                // Essentially get the seed + work out a buffer to adjacent chunks so we don't
                // inadvertantly spawn too many near the edges.
                var layerProto = ProtoManager.Index<BiomeMarkerLayerPrototype>(layer);
                var markerSeed = seed + chunk.X * ChunkSize + chunk.Y + localIdx;
                var rand = new Random(markerSeed);
                var buffer = (int) (layerProto.Radius / 2f);
                var bounds = new Box2i(chunk + buffer, chunk + layerProto.Size - buffer);
                var count = (int) (bounds.Area / (layerProto.Radius * layerProto.Radius));
                count = Math.Min(count, layerProto.MaxCount);

                GetMarkerNodes(gridUid, component, grid, layerProto, forced, bounds, count, rand,
                    out var spawnSet, out var existing);

                // Forcing markers to spawn so delete any that were found to be in the way.
                if (forced && existing.Count > 0)
                {
                    // Lock something so we can delete these safely.
                    lock (component.PendingMarkers)
                    {
                        foreach (var ent in existing)
                        {
                            Del(ent);
                        }
                    }
                }

                foreach (var node in spawnSet.Keys)
                {
                    var chunkOrigin = SharedMapSystem.GetChunkIndices(node, ChunkSize) * ChunkSize;

                    if (!pending.TryGetValue(chunkOrigin, out var pendingMarkers))
                    {
                        pendingMarkers = new Dictionary<string, List<Vector2i>>();
                        pending[chunkOrigin] = pendingMarkers;
                    }

                    if (!pendingMarkers.TryGetValue(layer, out var layerMarkers))
                    {
                        layerMarkers = new List<Vector2i>();
                        pendingMarkers[layer] = layerMarkers;
                    }

                    layerMarkers.Add(node);
                }

                lock (loadedMarkers)
                {
                    if (!loadedMarkers.TryGetValue(layer, out var lockMobChunks))
                    {
                        lockMobChunks = new HashSet<Vector2i>();
                        loadedMarkers[layer] = lockMobChunks;
                    }

                    lockMobChunks.Add(chunk);

                    foreach (var (chunkOrigin, layers) in pending)
                    {
                        if (!component.PendingMarkers.TryGetValue(chunkOrigin, out var lockMarkers))
                        {
                            lockMarkers = new Dictionary<string, List<Vector2i>>();
                            component.PendingMarkers[chunkOrigin] = lockMarkers;
                        }

                        foreach (var (lockLayer, nodes) in layers)
                        {
                            lockMarkers[lockLayer] = nodes;
                        }
                    }
                }
            });
        }

        component.ForcedMarkerLayers.Clear();
    }

    /// <summary>
    /// Gets the marker nodes for the specified area.
    /// </summary>
    /// <param name="emptyTiles">Should we include empty tiles when determine markers (e.g. if they are yet to be loaded)</param>
    public void GetMarkerNodes(
        EntityUid gridUid,
        BiomeComponent biome,
        MapGridComponent grid,
        BiomeMarkerLayerPrototype layerProto,
        bool forced,
        Box2i bounds,
        int count,
        Random rand,
        out Dictionary<Vector2i, string?> spawnSet,
        out HashSet<EntityUid> existingEnts,
        bool emptyTiles = true)
    {
        DebugTools.Assert(count > 0);
        var remainingTiles = _tilePool.Get();
        var nodeEntities = new Dictionary<Vector2i, EntityUid?>();
        var nodeMask = new Dictionary<Vector2i, string?>();

        // Okay so originally we picked a random tile and BFS outwards
        // the problem is if you somehow get a cooked frontier then it might drop entire veins
        // hence we'll grab all valid tiles up front and use that as possible seeds.
        // It's hella more expensive but stops issues.
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            for (var y = bounds.Bottom; y < bounds.Top; y++)
            {
                var node = new Vector2i(x, y);

                // Empty tile, skip if relevant.
                if (!emptyTiles && (!_mapSystem.TryGetTile(grid, node, out var tile) || tile.IsEmpty))
                    continue;

                // Check if it's a valid spawn, if so then use it.
                var enumerator = _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, node);
                enumerator.MoveNext(out var existing);

                if (!forced && existing != null)
                    continue;

                // Check if mask matches // anything blocking.
                TryGetEntity(node, biome, grid, out var proto);

                // If there's an existing entity and it doesn't match the mask then skip.
                if (layerProto.EntityMask.Count > 0 &&
                    (proto == null ||
                     !layerProto.EntityMask.ContainsKey(proto)))
                {
                    continue;
                }

                // If it's just a flat spawn then just check for anything blocking.
                if (proto != null && layerProto.Prototype != null)
                {
                    continue;
                }

                DebugTools.Assert(layerProto.EntityMask.Count == 0 || !string.IsNullOrEmpty(proto));
                remainingTiles.Add(node);
                nodeEntities.Add(node, existing);
                nodeMask.Add(node, proto);
            }
        }

        var frontier = new ValueList<Vector2i>(32);
        // TODO: Need poisson but crashes whenever I use moony's due to inputs or smth idk
        // Get the total amount of groups to spawn across the entire chunk.
        // We treat a null entity mask as requiring nothing else on the tile

        spawnSet = new Dictionary<Vector2i, string?>();
        existingEnts = new HashSet<EntityUid>();

        // Iterate the group counts and pathfind out each group.
        for (var i = 0; i < count; i++)
        {
            var groupSize = rand.Next(layerProto.MinGroupSize, layerProto.MaxGroupSize + 1);

            // While we have remaining tiles keep iterating
            while (groupSize >= 0 && remainingTiles.Count > 0)
            {
                var startNode = rand.PickAndTake(remainingTiles);
                frontier.Clear();
                frontier.Add(startNode);

                // This essentially may lead to a vein being split in multiple areas but the count matters more than position.
                while (frontier.Count > 0 && groupSize >= 0)
                {
                    // Need to pick a random index so we don't just get straight lines of ores.
                    var frontierIndex = rand.Next(frontier.Count);
                    var node = frontier[frontierIndex];
                    frontier.RemoveSwap(frontierIndex);
                    remainingTiles.Remove(node);

                    // Add neighbors if they're valid, worst case we add no more and pick another random seed tile.
                    for (var x = -1; x <= 1; x++)
                    {
                        for (var y = -1; y <= 1; y++)
                        {
                            if (x != 0 && y != 0)
                                continue;

                            var neighbor = new Vector2i(node.X + x, node.Y + y);

                            if (frontier.Contains(neighbor) || !remainingTiles.Contains(neighbor))
                                continue;

                            frontier.Add(neighbor);
                        }
                    }

                    // Tile valid salad so add it.
                    var mask = nodeMask[node];
                    spawnSet.Add(node, mask);
                    groupSize--;

                    if (nodeEntities.TryGetValue(node, out var existing))
                    {
                        Del(existing);
                    }
                }
            }

            if (groupSize > 0)
            {
                Log.Warning($"Found remaining group size for ore veins!");
            }
        }

        _tilePool.Return(remainingTiles);
    }

    /// <summary>
    /// Loads the pre-deteremined marker nodes for a particular chunk.
    /// This is calculated in <see cref="BuildMarkerChunks"/>
    /// </summary>
    /// <remarks>
    /// Note that the marker chunks do not correspond to this chunk.
    /// </remarks>
    private void LoadChunkMarkers(
        BiomeComponent component,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i chunk,
        int seed)
    {
        // Load any pending marker tiles first.
        if (!component.PendingMarkers.TryGetValue(chunk, out var layers))
            return;

        // This needs to be done separately in case we try to add a marker layer and want to force it on existing
        // loaded chunks.
        component.ModifiedTiles.TryGetValue(chunk, out var modified);
        modified ??= _tilePool.Get();

        foreach (var (layer, nodes) in layers)
        {
            var layerProto = ProtoManager.Index<BiomeMarkerLayerPrototype>(layer);

            foreach (var node in nodes)
            {
                if (modified.Contains(node))
                    continue;

                // Need to ensure the tile under it has loaded for anchoring.
                if (TryGetBiomeTile(node, component.Layers, seed, grid, out var tile))
                {
                    _mapSystem.SetTile(gridUid, grid, node, tile.Value);
                }

                string? prototype;

                if (TryGetEntity(node, component, grid, out var proto) &&
                    layerProto.EntityMask.TryGetValue(proto, out var maskedProto))
                {
                    prototype = maskedProto;
                }
                else
                {
                    prototype = layerProto.Prototype;
                }

                // If it is a ghost role then purge it
                // TODO: This is *kind* of a bandaid but natural mobs spawns needs a lot more work.
                // Ideally we'd just have ghost role and non-ghost role variants for some stuff.
                // var uid = EntityManager.CreateEntityUninitialized(prototype, _mapSystem.GridTileToLocal(gridUid, grid, node));
                // RemComp<GhostTakeoverAvailableComponent>(uid);
                // RemComp<GhostRoleComponent>(uid);
                // EntityManager.InitializeAndStartEntity(uid);
                // modified.Add(node);

                // Vulpstation - don't do any of the above bullshit, just save the entity for later
                var chunkIdx = (node / (float) ChunkSize).Floored() * ChunkSize;
                component.ReplacedEntities.GetOrNew(chunkIdx)[node] = (prototype, true);
            }
        }

        if (modified.Count == 0)
            _tilePool.Return(modified);

        component.PendingMarkers.Remove(chunk);
    }

    /// <summary>
    /// Loads a particular queued chunk for a biome.
    /// </summary>
    private void LoadChunk(
        BiomeComponent component,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i chunk,
        int seed)
    {
        var replacedTiles = component.ReplacedTiles.GetValueOrDefault(chunk);
        component.ModifiedTiles.TryGetValue(chunk, out var modified);
        modified ??= _tilePool.Get();
        _tiles.Clear();

        // Set tiles first
        for (var x = 0; x < ChunkSize; x++)
        {
            for (var y = 0; y < ChunkSize; y++)
            {
                var indices = new Vector2i(x + chunk.X, y + chunk.Y);

                // Pass in null so we don't try to get the tileref.
                if (modified.Contains(indices))
                    continue;

                // If there's existing data then don't overwrite it.
                if (_mapSystem.TryGetTileRef(gridUid, grid, indices, out var tileRef) && !tileRef.Tile.IsEmpty)
                    continue;

                // Vulpstation - rewritten
                var biomeTile =
                    replacedTiles != null && replacedTiles.TryGetValue(indices, out var biomeTile1) ? biomeTile1
                    : TryGetBiomeTile(indices, component.Layers, seed, grid, out var biomeTile2) ? biomeTile2.Value
                    : Tile.Empty;

                if (biomeTile.IsEmpty)
                    continue;

                _tiles.Add((indices, biomeTile));
            }
        }

        _mapSystem.SetTiles(gridUid, grid, _tiles);
        _tiles.Clear();

        // Now do entities
        var replacedEntities = component.ReplacedEntities.GetValueOrDefault(chunk); // Vulpstation
        var loadedEntities = component.LoadedEntities.GetOrNew(chunk); // Vulpstation - GetOrNew instead of making a new one each time
        component.LoadedEntities[chunk] = loadedEntities;

        var oldChunkAtmos = component.ModifiedAtmos.GetValueOrDefault(chunk); // Vulpstation
        var gridAtmos = _gridAtmosQuery.CompOrNull(gridUid); // Vulpstation
        var defaultMix = _mapAtmosQuery.CompOrNull(gridUid)?.Mixture; // Vulpstation
        for (var x = 0; x < ChunkSize; x++)
        {
            for (var y = 0; y < ChunkSize; y++)
            {
                var indices = new Vector2i(x + chunk.X, y + chunk.Y);

                // Vulpstation - set tile atmos first. This has to be done AFTER tiles are placed.
                if (gridAtmos != null && oldChunkAtmos != null && oldChunkAtmos.TryGetValue(indices, out var oldMix))
                    _atmos.GetOrNewTile(gridUid, gridAtmos, indices)?.Air?.CopyFrom(oldMix);
                else if (gridAtmos != null && defaultMix != null)
                    _atmos.GetOrNewTile(gridUid, gridAtmos, indices)?.Air?.CopyFrom(defaultMix);

                if (modified.Contains(indices))
                    continue;

                // Don't mess with anything that's potentially anchored.
                var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, indices);

                if (anchored.MoveNext(out _))
                    continue;

                // Vulpstation - rewritten
                string? entPrototype = null;
                bool isNative = true;
                if (replacedEntities != null && replacedEntities.TryGetValue(indices, out var replaced))
                {
                    if (replaced.prototype == null)
                        continue; // The entity was deleted or forgotten.

                    isNative = replaced.isNative; // This is NOT the originally generated entity
                    entPrototype = replaced.prototype;
                }
                else if (!TryGetEntity(indices, component, grid, out entPrototype))
                    continue;

                var ent = Spawn(entPrototype, _mapSystem.GridTileToLocal(gridUid, grid, indices));

                // At least for now unless we do lookups or smth, only work with anchoring.
                if (_xformQuery.TryGetComponent(ent, out var xform) && !xform.Anchored)
                {
                    _transform.AnchorEntity(ent, xform, gridUid, grid, indices);
                }

                // Even if we don't save the entity, it will still be picked up later during anchored entity unloading/pausing
                // It just will undergo less permissive checks
                if (isNative)
                    loadedEntities.Add(ent, indices);
            }
        }

        // Vulpstation - unpause anything that has been paused
        var pausedEntities = component.PausedEntities.GetValueOrDefault(chunk);
        if (pausedEntities != null)
            foreach (var ent in pausedEntities)
            {
                if (!_metaQuery.TryComp(ent, out var meta) || TerminatingOrDeleted(ent, meta))
                    continue;

                _meta.SetEntityPaused(ent, false, meta);
            }

        // Decals
        var loadedDecals = new Dictionary<uint, Vector2i>();
        component.LoadedDecals.Add(chunk, loadedDecals);

        for (var x = 0; x < ChunkSize; x++)
        {
            for (var y = 0; y < ChunkSize; y++)
            {
                var indices = new Vector2i(x + chunk.X, y + chunk.Y);

                if (modified.Contains(indices))
                    continue;

                // Don't mess with anything that's potentially anchored.
                var anchored = _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, indices);

                if (anchored.MoveNext(out _) || !TryGetDecals(indices, component.Layers, seed, grid, out var decals))
                    continue;

                foreach (var decal in decals)
                {
                    if (!_decals.TryAddDecal(decal.ID, new EntityCoordinates(gridUid, decal.Position), out var dec))
                        continue;

                    loadedDecals.Add(dec, indices);
                }
            }
        }

        if (modified.Count == 0)
        {
            _tilePool.Return(modified);
            component.ModifiedTiles.Remove(chunk);
        }
        else
        {
            component.ModifiedTiles[chunk] = modified;
        }
    }

    #endregion

    #region Unload

    /// <summary>
    /// Handles all of the queued chunk unloads for a particular biome.
    /// </summary>
    private void UnloadChunks(BiomeComponent component, EntityUid gridUid, MapGridComponent grid, int seed)
    {
        var active = _activeChunks[component];
        List<(Vector2i, Tile)>? tiles = null;

        foreach (var chunk in component.LoadedChunks)
        {
            if (active.Contains(chunk) || !component.LoadedChunks.Remove(chunk))
                continue;

            // Unload NOW!
            tiles ??= new List<(Vector2i, Tile)>(ChunkSize * ChunkSize);
            UnloadChunk(component, gridUid, grid, chunk, seed, tiles);
        }
    }

    /// <summary>
    /// Unloads a specific biome chunk.
    /// </summary>
    private void UnloadChunk(BiomeComponent component, EntityUid gridUid, MapGridComponent grid, Vector2i chunk, int seed, List<(Vector2i, Tile)> tiles)
    {
        // Reverse order to loading
        component.ModifiedTiles.TryGetValue(chunk, out var modified);
        modified ??= new HashSet<Vector2i>();
        // Vulp - instead of marking those tiles as modified and keeping them, just keep note that entities on them don't need to be regenerated
        // We don't pool this one because pretty much every chunk will contain ores that do not need to be replaced
        var replacedEntities = component.ReplacedEntities.GetOrNew(chunk);
        var replacedTiles = component.ReplacedTiles.GetOrNew(chunk);
        replacedTiles.Clear(); // Do NOT clear replacedEntities, it's a persistent dict

        // Delete decals
        foreach (var (dec, indices) in component.LoadedDecals[chunk])
        {
            // If we couldn't remove it then flag the tile to never be touched.
            if (!_decals.RemoveDecal(gridUid, dec))
            {
                modified.Add(indices);
            }
        }

        component.LoadedDecals.Remove(chunk);

        // Delete entities
        // Ideally any entities that aren't modified just get deleted and re-generated later
        // This is because if we want to save the map (e.g. persistent server) it makes the file much smaller
        // and also if the map is enormous will make stuff like physics broadphase much faster
        var xformQuery = GetEntityQuery<TransformComponent>();
        foreach (var (ent, tile) in component.LoadedEntities[chunk])
        {
            if (Deleted(ent) || !xformQuery.TryGetComponent(ent, out var xform))
            {
                replacedEntities[tile] = (null, true);
                continue;
            }

            // Vulpstation - instead, we raise an event. Also, we assume anything in here is native.
            var ev = new BiomeUnloadingEvent(true);
            RaiseLocalEvent(ent, ref ev);

            if (ev.MarkTileModified)
                modified.Add(tile);
            if (ev.Unload || ev.Delete)
            {
                replacedEntities[tile] = (ev.Delete ? null : MetaData(ent).EntityPrototype?.ID, true);
                QueueDel(ent);
            }
        }

        // Unset tiles (if the data is custom)

        // Vulpstation section
        var atmos = _gridAtmosQuery.CompOrNull(gridUid);
        var mapAtmos = atmos == null ? null : _mapAtmosQuery.CompOrNull(gridUid);
        var defaultMix = mapAtmos?.Mixture;
        if (!component.ModifiedAtmos.TryGetValue(chunk, out var atmosChunk))
        {
            atmosChunk = _gasPool.Get();
            component.ModifiedAtmos[chunk] = atmosChunk;
        }
        atmosChunk.Clear();
        // Vulpstation section end

        for (var x = 0; x < ChunkSize; x++)
        {
            for (var y = 0; y < ChunkSize; y++)
            {
                var indices = new Vector2i(x + chunk.X, y + chunk.Y);

                // Vulpstation - serialize atmos first. This does not care if the tile itself was modified, only the atmos.
                // We don't want to serialize default map atmos because that can change e.g. with the weatheraaa
                if (defaultMix != null
                    && _atmos.GetTileMixture((gridUid, atmos, null), (gridUid, mapAtmos), indices) is { } currentMix
                    && !defaultMix.ApproximatelyEqual(currentMix))
                    atmosChunk[indices] = currentMix;

                if (modified.Contains(indices))
                    continue;

                // Don't mess with anything that's potentially anchored.
                var anchored = grid.GetAnchoredEntitiesEnumerator(indices);
                var anyReplacements = replacedEntities.TryGetValue(indices, out var oldReplaced) && oldReplaced.prototype != null;
                var replacedEntity = EntityUid.Invalid;

                // Vulpstation - we can only unload 1 entity. More is not supported & is very dangerous.
                while (anchored.MoveNext(out var ent))
                {
                    if (component.LoadedEntities[chunk].ContainsKey(ent.Value))
                        continue;

                    var ev = new BiomeUnloadingEvent(false);
                    RaiseLocalEvent(ent.Value, ref ev);

                    // This is guaranteed to not be a naturally generated entity, so we don't have to mark deletions
                    if (ev.Delete)
                        QueueDel(ent.Value);
                    else if (ev.Unload)
                    {
                        if (anyReplacements)
                        {
                            // Alas, more than one entity is being replaced here. We can't do anything about it.
                            modified.Add(indices);
                            continue;
                        }

                        anyReplacements = true;
                        replacedEntity = ent.Value;
                    }
                    else if (ev.MarkTileModified)
                        modified.Add(indices);
                }

                if (modified.Contains(indices))
                    continue;

                // Vulpstation - don't mark the tile as modified unless necessary, we don't want to have atmos and tilespread process it.
                if (_mapSystem.TryGetTileRef(gridUid, grid, indices, out var tileRef)
                    && (!TryGetBiomeTile(indices, component.Layers, seed, null, out var biomeTile) || biomeTile.Value != tileRef.Tile))
                {
                    // To do: maybe introduce a special field for this? Idfk. We need a way to distinguish planet tiles and this works.
                    if (((ContentTileDefinition) _tileDef[tileRef.Tile.TypeId]).RegenerateAtmos != 0)
                        replacedTiles[indices] = tileRef.Tile; // This is a planet tile, don't preserve it. Any atmos on it will slowly return to normal anyway.
                    else
                    {
                        modified.Add(indices); // This is a man-made tile, do preserve it, because it can contain atmos.
                        continue;
                    }
                }

                // Now that we made sure only 1 entity is being replaced and the tile is not kept, we can delete it.
                if (replacedEntity != EntityUid.Invalid)
                {
                    replacedEntities[indices] = (MetaData(replacedEntity).EntityPrototype?.ID, false);
                    Del(replacedEntity);
                }

                tiles.Add((indices, Tile.Empty));
            }
        }

        // Vulpstation - pause anything that is left behind. Chunk coords are represented by the bottom left corner.
        var pausedEntities = component.PausedEntities.GetOrNew(chunk);
        var chunkBounds = new Box2(chunk.X, chunk.Y, chunk.X + ChunkSize, chunk.Y + ChunkSize);
        foreach (var ent in _lookup.GetEntitiesIntersecting(gridUid, chunkBounds, LookupFlags.All))
        {
            // My current theory is that the method above return EUID 0 for queuedly-deleted entities
            if (ent is not { Valid: true })
                continue;

            // TODO hardcoding the check for ghosts is suboptimal
            if (!_metaQuery.TryComp(ent, out var meta) || TerminatingOrDeleted(ent, meta) || Paused(ent, meta) || _ghostQuery.HasComp(ent))
                continue;

            // TODO this is terrible - test if storing a HashSet of modified entities is better
            // var xform = Transform(ent);
            // if (xform.Anchored)
            // {
            //     var tilePos = _mapSystem.LocalToTile(gridUid, grid, xform.Coordinates);
            //     if (replacedEntities.ContainsKey(tilePos) || modified.Contains(tilePos))
            //         continue;
            // }

            var ev = new BiomeUnloadingEvent(false);
            RaiseLocalEvent(ent, ref ev);

            // UNLESS the system wants us to unload the entity, don't pause it
            if (!ev.Unload || ev.MarkTileModified || ev.Delete)
            {
                if (ev.Delete)
                    QueueDel(ent);
                continue;
            }

            pausedEntities.Add(ent);
            _meta.SetEntityPaused(ent, true, meta);
        }

        grid.SetTiles(tiles);
        tiles.Clear();
        component.LoadedChunks.Remove(chunk);
        component.LoadedEntities.Remove(chunk);

        if (modified.Count == 0)
        {
            component.ModifiedTiles.Remove(chunk);
        }
        else
        {
            component.ModifiedTiles[chunk] = modified;
        }

        // Vulpstation
        if (atmosChunk.Count == 0)
        {
            component.ModifiedAtmos.Remove(chunk);
            _gasPool.Return(atmosChunk);
        }
        // Vulpstation
        if (replacedEntities.Count == 0)
            component.ReplacedEntities.Remove(chunk);
        if (replacedTiles.Count == 0)
            component.ReplacedTiles.Remove(chunk);
    }

    #endregion

    /// <summary>
    /// Creates a simple planet setup for a map.
    /// </summary>
    public void EnsurePlanet(EntityUid mapUid, BiomeTemplatePrototype biomeTemplate, int? seed = null, MetaDataComponent? metadata = null, Color? mapLight = null)
    {
        if (!Resolve(mapUid, ref metadata))
            return;

        EnsureComp<MapGridComponent>(mapUid);
        var biome = (BiomeComponent) EntityManager.ComponentFactory.GetComponent(typeof(BiomeComponent));
        seed ??= _random.Next();
        SetSeed(mapUid, biome, seed.Value, false);
        SetTemplate(mapUid, biome, biomeTemplate, false);
        AddComp(mapUid, biome, true);
        Dirty(mapUid, biome, metadata);

        var gravity = EnsureComp<GravityComponent>(mapUid);
        gravity.Enabled = true;
        gravity.Inherent = true;
        Dirty(mapUid, gravity, metadata);

        // Day lighting
        // Daylight: #D8B059
        // Midday: #E6CB8B
        // Moonlight: #2b3143
        // Lava: #A34931

        var light = EnsureComp<MapLightComponent>(mapUid);
        light.AmbientLightColor = mapLight ?? Color.FromHex("#D8B059");
        Dirty(mapUid, light, metadata);

        var moles = new float[Atmospherics.AdjustedNumberOfGases];
        moles[(int) Gas.Oxygen] = 21.824779f;
        moles[(int) Gas.Nitrogen] = 82.10312f;

        var mixture = new GasMixture(moles, Atmospherics.T20C);

        _atmos.SetMapAtmosphere(mapUid, false, mixture);
    }

    /// <summary>
    /// Sets the specified tiles as relevant and marks them as modified.
    /// </summary>
    public void ReserveTiles(EntityUid mapUid, Box2 bounds, List<(Vector2i Index, Tile Tile)> tiles, BiomeComponent? biome = null, MapGridComponent? mapGrid = null)
    {
        if (!Resolve(mapUid, ref biome, ref mapGrid, false))
            return;

        foreach (var tileSet in _mapSystem.GetLocalTilesIntersecting(mapUid, mapGrid, bounds, false))
        {
            Vector2i chunkOrigin;
            HashSet<Vector2i> modified;

            // Existing, ignore
            if (_mapSystem.TryGetTileRef(mapUid, mapGrid, tileSet.GridIndices, out var existingRef) && !existingRef.Tile.IsEmpty)
            {
                chunkOrigin = SharedMapSystem.GetChunkIndices(tileSet.GridIndices, ChunkSize) * ChunkSize;
                modified = biome.ModifiedTiles.GetOrNew(chunkOrigin);
                modified.Add(tileSet.GridIndices);
                continue;
            }

            if (!TryGetBiomeTile(tileSet.GridIndices, biome.Layers, biome.Seed, mapGrid, out var tile))
            {
                continue;
            }

            chunkOrigin = SharedMapSystem.GetChunkIndices(tileSet.GridIndices, ChunkSize) * ChunkSize;
            modified = biome.ModifiedTiles.GetOrNew(chunkOrigin);
            modified.Add(tileSet.GridIndices);
            tiles.Add((tileSet.GridIndices, tile.Value));
        }

        _mapSystem.SetTiles(mapUid, mapGrid, tiles);
    }
}
