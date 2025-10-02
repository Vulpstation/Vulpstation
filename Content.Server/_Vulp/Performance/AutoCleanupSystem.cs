using System.Numerics;
using Content.Server.Administration.Managers;
using Content.Server.Bed.Cryostorage;
using Content.Server.Chat.Managers;
using Content.Server.Database.Migrations.Sqlite;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Server.Parallax;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.GameTicking.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC;
using Content.Shared.Parallax.Biomes;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;


namespace Content.Server._Vulp.Performance;


public sealed class AutoCleanupSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly TransformSystem _xforms = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly CryostorageSystem _cryo = default!;
    [Dependency] private readonly IChatManager _chat = default!;

    private EntityQuery<BiomeSystem.BiomeIntrinsicComponent> _intrinsicQuery;

    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan LastUpdate, UpdateInterval = TimeSpan.FromSeconds(20), AdminAnnounceInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     NPC inactivity period required to fully unload it (resetting its position to its spawn point)
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan NpcDeleteTime = TimeSpan.FromMinutes(5);

    [ViewVariables(VVAccess.ReadWrite)]
    public bool Enabled = true;

    // We keep track of how many things we've deleted so far
    // Once the sum of deletions exceeds 100, send an admin message.
    [ViewVariables]
    private TimeSpan _lastAnnouncement = TimeSpan.Zero;
    [ViewVariables]
    private int _deletedGamerules, _resetNpcs, _pausedNpcs, _unpausedNpcs, _pausedCryosleepers;

    // Cached locations of players for the last update tick
    private readonly List<(MapId, Vector2)> _players = new();

    public override void Initialize()
    {
        _intrinsicQuery = GetEntityQuery<BiomeSystem.BiomeIntrinsicComponent>();

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnLevelChanged);
    }

    private void OnLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New == GameRunLevel.InRound)
        {
            Enabled = true;
            LastUpdate = _timing.CurTime;
        }
        else
            Enabled = false;
    }

    public override void Update(float frameTime)
    {
        if (!Enabled || _ticker.RunLevel != GameRunLevel.InRound)
            return;

        var curTime = _timing.CurTime;
        if (curTime - LastUpdate > TimeSpan.FromSeconds(1))
        {
            LastUpdate = curTime;
            Cleanup();
        }
    }

    public void Cleanup()
    {
        // Because I can't be fucked to fix the relevant entity systems.
        UpdatePlayerLocations();
        CleanupGamerules();
        CleanupNpcs();
        CleanupCryosleepers();

        var sum = _deletedGamerules + _resetNpcs + _pausedNpcs + _unpausedNpcs + _pausedCryosleepers;
        var dt = _timing.CurTime - _lastAnnouncement; // Don't want to spam the admin chat too much
        if (sum > 100 && dt > AdminAnnounceInterval)
        {
            _lastAnnouncement = _timing.CurTime;
            _chat.SendAdminAnnouncement(
                $"Cleanup stats over the last {dt.TotalMinutes:0.0} minutes: {_deletedGamerules} deleted gamerules," +
                $"{_resetNpcs} reset npcs, {_pausedNpcs} paused npcs, {_unpausedNpcs} unpaused npcs," +
                $"{_pausedCryosleepers} paused cryosleepers. VV the AutoCleanup system to change the config.");

            _deletedGamerules = _resetNpcs = _pausedNpcs = _unpausedNpcs = _pausedCryosleepers = 0;
        }
    }

    private void UpdatePlayerLocations()
    {
        void AddRoundEntity(EntityUid ent)
        {
            var xform = Transform(ent);
            // Ignore players on non-biome maps
            if (!HasComp<BiomeComponent>(xform.MapUid))
                return;

            _players.Add((xform.MapID, _xforms.GetWorldPosition(xform)));
        }

        var ghostQuery = GetEntityQuery<GhostComponent>();
        foreach (var player in Filter.GetAllPlayers(_playerManager))
        {
            if (player.AttachedEntity == null)
                continue;

            // Same logic as with the biome system - don't allow player ghosts to keep entities loaded
            // However, if the player has a body lying around, keep mobs around it loaded
            if (ghostQuery.TryComp(player.AttachedEntity, out var ghost))
            {
                if (_mind.TryGetMind(player, out var _, out var mindComp)
                    && mindComp.OwnedEntity != mindComp.VisitingEntity
                    && mindComp.OwnedEntity != null)
                {
                    AddRoundEntity(mindComp.OwnedEntity.Value);
                }

                continue;
            }

            AddRoundEntity(player.AttachedEntity.Value);
        }
    }

    private void CleanupGamerules()
    {
        // Just. Guh.
        var query = EntityQueryEnumerator<GameRuleComponent>();
        var activeQuery = GetEntityQuery<ActiveGameRuleComponent>();
        var endedQuery = GetEntityQuery<EndedGameRuleComponent>();

        while (query.MoveNext(out var uid, out var rule))
        {
            if (activeQuery.HasComp(uid) || !endedQuery.HasComp(uid))
                continue;

            // TODO this may not work for gamerules that don't start and don't end? Idk if I saw those before
            // Guh.
            QueueDel(uid);
            _deletedGamerules++;
        }
    }

    private void CleanupNpcs()
    {
        var query = AllEntityQuery<ActiveNPCComponent, MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var npc, out var meta, out var xform))
        {
            if (!HasComp<BiomeComponent>(xform.MapUid))
                continue;

            if (ArePlayersNearby(xform.MapID, _xforms.GetWorldPosition(xform)))
                UnCleanupNPC(uid, meta, xform);
            else
                CleanupNPC(uid, meta, xform);
        }
    }

    private void UnCleanupNPC(EntityUid uid, MetaDataComponent meta, TransformComponent xform)
    {
        if (_intrinsicQuery.TryComp(uid, out var intrinsic))
        {
            intrinsic.LastModified = _timing.CurTime;
            return; // This mob is owned by the biome system, nothing to do.
        }

        if (!meta.EntityPaused)
            return;

        var chunkIndex = _xforms.GetGridOrMapTilePosition(uid, xform);
        var chunk = (chunkIndex / SharedBiomeSystem.ChunkSize) * SharedBiomeSystem.ChunkSize;
        // Make sure the biome system hasn't paused this mob itself - in this case our intervention could hurt
        if (TryComp<BiomeComponent>(xform.MapUid, out var biome)
            && biome.PausedEntities.TryGetValue(chunk, out var paused)
            && paused.Contains(uid))
            return;

        // Unpause the entity in case it was paused earlier
        _meta.SetEntityPaused(uid, false, meta);
        _unpausedNpcs++;
    }

    private void CleanupNPC(EntityUid uid, MetaDataComponent meta, TransformComponent xform)
    {
        if (meta.EntityPaused && _intrinsicQuery.TryComp(uid, out var intrinsic))
        {
            // This mob is unloaded and owned by the biome system. If sufficient time has passed without players nearby, try to delete it
            var delta = _timing.CurTime - intrinsic.LastModified;
            if (intrinsic.LastModified == TimeSpan.Zero || delta < NpcDeleteTime)
                return;

            ResetNPC(uid, meta, xform, intrinsic);
            return;
        }

        if (!meta.EntityPaused)
        {
            // This mob is not unloaded, don't allow to delete it
            if (_intrinsicQuery.TryComp(uid, out intrinsic))
                intrinsic.LastModified = _timing.CurTime;

            // This mob is not biome-intrinsic, we can probably pause it?
            _meta.SetEntityPaused(uid, true, meta);
            _pausedNpcs++;

            return;
        }
    }

    /// <summary>
    ///     Fully resets an NPC, deleting its entity and resetting its spawn point to allow it to re-spawn.
    /// </summary>
    public void ResetNPC(
        EntityUid uid,
        MetaDataComponent meta,
        TransformComponent xform,
        BiomeSystem.BiomeIntrinsicComponent intrinsic,
        bool ignoreDead = false)
    {
        if (!ignoreDead && TryComp<MobStateComponent>(uid, out var mobState) && mobState.CurrentState is not MobState.Alive
            || intrinsic.OwnerBiome != xform.MapUid
            || !TryComp<BiomeComponent>(intrinsic.OwnerBiome, out var biome))
            return;

        // Check if the relevant part of the biome is free
        if (meta.EntityPrototype?.ID is not { } protoId)
            return;

        if (!biome.ReplacedEntities.TryGetValue(intrinsic.Chunk, out var biomeChunk)
            || !biomeChunk.TryGetValue(intrinsic.ChunkIndex, out var biomeChunkEntity)
            || (biomeChunkEntity.prototype is not null && biomeChunkEntity.prototype != protoId))
            return;

        // It's confirmed, do the deed.
        if (biome.LoadedEntities.TryGetValue(intrinsic.Chunk, out var biomeChunkEntities))
            biomeChunkEntities.Remove(uid);

        QueueDel(uid);
        biomeChunk[intrinsic.ChunkIndex] = (protoId, true);
        _resetNpcs++;
    }

    private void CleanupCryosleepers()
    {
        // Try to pause every body on the cryosleep map that is not paused
        // Because for some fucking reason the cryosleep system fails sometimes?!
        var query = EntityQueryEnumerator<CryostorageContainedComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var cryo, out var xform))
        {
            if (!_cryo.IsInPausedMap((uid, xform)))
                continue;

            _meta.SetEntityPaused(uid, true);
            _pausedCryosleepers++;

            // Sanity check. I'm already insane.
            if (!IsPaused(uid))
            {
                Log.Warning("What the fuck is wrong with this system?!");
                QueueDel(uid);

                if (TryComp<CryostorageComponent>(cryo.Cryostorage, out var cryopod))
                    cryopod.StoredPlayers.Remove(uid);
            }
        }
    }

    private bool ArePlayersNearby(MapId mapId, Vector2 pos)
    {
        var minCleanupDistance = 35; // Hardcoded for now, see if we want to make this configurable or a cvar
        var minCleanupDistanceSqr = minCleanupDistance * minCleanupDistance;
        foreach (var player in _players)
        {
            if (player.Item1 != mapId)
                continue;

            if (Vector2.DistanceSquared(player.Item2, pos) < 100 * 100)
                return true;
        }

        return false;
    }
}
