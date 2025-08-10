using System.Linq;
using Content.Server.StationEvents.Components;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Station.Components;
using Content.Shared.GameTicking.Components;
using Content.Shared.Ghost;
using Content.Shared.Storage;
using Content.Shared.Tools.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.StationEvents.Events;

public sealed class VentCrittersRule : StationEventSystem<VentCrittersRuleComponent>
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly TransformSystem _xforms = default!;
    /*
     * DO NOT COPY PASTE THIS TO MAKE YOUR MOB EVENT.
     * USE THE PROTOTYPE.
     */

    protected override void Started(EntityUid uid, VentCrittersRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var station))
        {
            return;
        }

        var locations = EntityQueryEnumerator<VentCritterSpawnLocationComponent, TransformComponent>();
        var validLocations = new List<EntityCoordinates>();
        while (locations.MoveNext(out var ventUid, out var ventSpawn, out var transform))
        {
            // Floof: do not spawn on welded vents
            if (TryComp<WeldableComponent>(ventUid, out var weldable) && weldable.IsWelded)
                continue;

            // Vulp - check if spawn is valid
            if (!CheckSpawnValid(ventUid, transform))
                continue;

            if (CompOrNull<StationMemberComponent>(transform.GridUid)?.Station == station)
            {
                validLocations.Add(transform.Coordinates);
                // Vulpstation
                var weighted = component.Entries.Select(
                    it => new EntitySpawnEntry(it) { SpawnProbability = it.SpawnProbability * ventSpawn.Weight });

                foreach (var spawn in EntitySpawnCollection.GetSpawns(weighted, RobustRandom))
                {
                    SpawnCritter(spawn, transform.Coordinates, component); // Floof - changed to delayed spawn
                }
            }
        }

        if (component.SpecialEntries.Count == 0 || validLocations.Count == 0)
        {
            return;
        }

        // guaranteed spawn
        var specialEntry = RobustRandom.Pick(component.SpecialEntries);
        var specialSpawn = RobustRandom.Pick(validLocations);
        SpawnCritter(specialEntry.PrototypeId, specialSpawn, component); // Floof - changed to delayed spawn

        foreach (var location in validLocations)
        {
            foreach (var spawn in EntitySpawnCollection.GetSpawns(component.SpecialEntries, RobustRandom))
            {
                SpawnCritter(spawn, location, component); // Floof - changed to delayed spawn
            }
        }
    }

    // Floof
    private void SpawnCritter(EntProtoId? protoId, EntityCoordinates coordinates, VentCrittersRuleComponent rule) =>
        DelayedSpawn(protoId, coordinates, rule.InitialDelay, rule.CrawlTime, rule.Popup, rule.Sound);

    // Vulp - check if there are entities nearby, don't just spawn monsters in the void
    private bool CheckSpawnValid(EntityUid ventUid, TransformComponent xform)
    {
        var mapPos = _xforms.ToMapCoordinates(xform.Coordinates);

        // Check vent event-free zones
        var query = EntityQueryEnumerator<VentEventFreeZoneComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var zone, out var zoneXform))
        {
            if (!zone.Enabled || zoneXform.MapID != mapPos.MapId)
                continue;

            var otherMapPos = _xforms.ToMapCoordinates(zoneXform.Coordinates, false);
            if ((mapPos.Position - otherMapPos.Position).LengthSquared() < zone.Radius * zone.Radius)
                return false;
        }

        // Make sure there's at least one player nearby
        foreach (var session in _playerManager.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } player || HasComp<GhostComponent>(player))
                continue;

            var otherXform = Transform(player);
            if (otherXform.MapID != mapPos.MapId)
                continue;

            var otherMapPos = _xforms.ToMapCoordinates(Transform(player).Coordinates, false);
            if ((mapPos.Position - otherMapPos.Position).LengthSquared() < 25 * 25)
                return true;
        }

        return false;
    }
}
