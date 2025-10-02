using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.Parallax;
using Content.Server.Spawners.Components;
using Content.Shared.EntityTable;
using Content.Shared.GameTicking.Components;
using Content.Shared.Parallax.Biomes;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server.Spawners.EntitySystems
{
    [UsedImplicitly]
    public sealed class ConditionalSpawnerSystem : EntitySystem
    {
        [Dependency] private readonly IRobustRandom _robustRandom = default!;
        [Dependency] private readonly GameTicker _ticker = default!;
        [Dependency] private readonly EntityTableSystem _entityTable = default!;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<GameRuleStartedEvent>(OnRuleStarted);
            SubscribeLocalEvent<ConditionalSpawnerComponent, MapInitEvent>(OnCondSpawnMapInit);
            SubscribeLocalEvent<RandomSpawnerComponent, MapInitEvent>(OnRandSpawnMapInit);
            SubscribeLocalEvent<EntityTableSpawnerComponent, MapInitEvent>(OnEntityTableSpawnMapInit);
        }

        private void OnCondSpawnMapInit(EntityUid uid, ConditionalSpawnerComponent component, MapInitEvent args)
        {
            TrySpawn(uid, component);
        }

        private void OnRandSpawnMapInit(EntityUid uid, RandomSpawnerComponent component, MapInitEvent args)
        {
            Spawn(uid, component);
            if (component.DeleteSpawnerAfterSpawn)
                QueueDel(uid);
        }

        private void OnEntityTableSpawnMapInit(Entity<EntityTableSpawnerComponent> ent, ref MapInitEvent args)
        {
            Spawn(ent);
            if (ent.Comp.DeleteSpawnerAfterSpawn && !TerminatingOrDeleted(ent) && Exists(ent))
                QueueDel(ent);
        }

        private void OnRuleStarted(ref GameRuleStartedEvent args)
        {
            var query = EntityQueryEnumerator<ConditionalSpawnerComponent>();
            while (query.MoveNext(out var uid, out var spawner))
            {
                RuleStarted(uid, spawner, args);
            }
        }

        public void RuleStarted(EntityUid uid, ConditionalSpawnerComponent component, GameRuleStartedEvent obj)
        {
            if (component.GameRules.Contains(obj.RuleId))
                Spawn(uid, component);
        }

        private void TrySpawn(EntityUid uid, ConditionalSpawnerComponent component)
        {
            if (component.GameRules.Count == 0)
            {
                Spawn(uid, component);
                return;
            }

            foreach (var rule in component.GameRules)
            {
                if (!_ticker.IsGameRuleActive(rule))
                    continue;
                Spawn(uid, component);
                return;
            }
        }

        private void Spawn(EntityUid uid, ConditionalSpawnerComponent component)
        {
            if (component.Chance != 1.0f && !_robustRandom.Prob(component.Chance))
                return;

            if (component.Prototypes.Count == 0)
            {
                Log.Warning($"Prototype list in ConditionalSpawnComponent is empty! Entity: {ToPrettyString(uid)}");
                return;
            }

            if (!Deleted(uid))
            {
                var spawned = EntityManager.SpawnEntity(_robustRandom.Pick(component.Prototypes), Transform(uid).Coordinates);
                // Vulpstation
                CopyBiomeIntrinsicality((uid, null), spawned, Vector2i.Zero);
            }
        }

        private void Spawn(EntityUid uid, RandomSpawnerComponent component)
        {
            if (component.RarePrototypes.Count > 0 && (component.RareChance == 1.0f || _robustRandom.Prob(component.RareChance)))
            {
                EntityManager.SpawnEntity(_robustRandom.Pick(component.RarePrototypes), Transform(uid).Coordinates);
                return;
            }

            if (component.Chance != 1.0f && !_robustRandom.Prob(component.Chance))
                return;

            if (component.Prototypes.Count == 0)
            {
                Log.Warning($"Prototype list in RandomSpawnerComponent is empty! Entity: {ToPrettyString(uid)}");
                return;
            }

            if (Deleted(uid))
                return;

            var offset = component.Offset;
            var xOffset = _robustRandom.NextFloat(-offset, offset);
            var yOffset = _robustRandom.NextFloat(-offset, offset);

            var coordinates = Transform(uid).Coordinates.Offset(new Vector2(xOffset, yOffset));

            // Floofstation - wrapped the below in a try-catch. Fuck you heisentest.
            var picked = _robustRandom.Pick(component.Prototypes);
            try
            {
                var spawned = EntityManager.SpawnEntity(picked, coordinates);
                CopyBiomeIntrinsicality((uid, null), spawned, new Vector2(xOffset, yOffset).Floored()); // Vulpstation
            }
            catch (EntityCreationException e)
            {
                Log.Warning($"Caught an exception while trying to process a conditional spawner {ToPrettyString(uid)} of type {picked}: {e}");
            }
        }

        private void Spawn(Entity<EntityTableSpawnerComponent> ent)
        {
            if (TerminatingOrDeleted(ent) || !Exists(ent))
                return;

            var coords = Transform(ent).Coordinates;
            var biomeIntrinsic = CompOrNull<BiomeSystem.BiomeIntrinsicComponent>(ent); // Vulpstation

            var spawns = _entityTable.GetSpawns(ent.Comp.Table);
            foreach (var proto in spawns)
            {
                var xOffset = _robustRandom.NextFloat(-ent.Comp.Offset, ent.Comp.Offset);
                var yOffset = _robustRandom.NextFloat(-ent.Comp.Offset, ent.Comp.Offset);
                var trueCoords = coords.Offset(new Vector2(xOffset, yOffset));

                var spawned = Spawn(proto, trueCoords);

                // Vulpstation
                if (biomeIntrinsic is not null)
                    CopyBiomeIntrinsicality((ent, biomeIntrinsic), spawned, new Vector2(xOffset, yOffset).Floored());
            }
        }

        // Vulpstation. This is a hack, I don't want to work around this system. Just transfers BiomeIntrinsic to the spawned entity.
        private void CopyBiomeIntrinsicality(Entity<BiomeSystem.BiomeIntrinsicComponent?> spawner, EntityUid spawned, Vector2i offset)
        {
            if (spawner.Comp == null && !Resolve(spawned, ref spawner.Comp, false))
                return;

            var biomeIntrinsic = spawner.Comp!;
            var intrinsicSpawn = EnsureComp<BiomeSystem.BiomeIntrinsicComponent>(spawned);
            intrinsicSpawn.ChunkIndex = biomeIntrinsic.ChunkIndex + offset;
            intrinsicSpawn.Chunk = intrinsicSpawn.ChunkIndex / SharedBiomeSystem.ChunkSize * SharedBiomeSystem.ChunkSize;
            intrinsicSpawn.OwnerBiome = biomeIntrinsic.OwnerBiome;
            intrinsicSpawn.LastModified = biomeIntrinsic.LastModified;
        }
    }
}
