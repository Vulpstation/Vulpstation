using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Systems;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Ghost;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Customization.Systems;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.Players;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server.Traits;

public sealed class TraitSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly CharacterRequirementsSystem _characterRequirements = default!;
    [Dependency] private readonly PlayTimeTrackingManager _playTimeTracking = default!;
    [Dependency] private readonly IConfigurationManager _configuration = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly AdminSystem _adminSystem = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly GameTicker _players = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBeforeSpawn); // Vulpstation - moved the anticheat to before spawn
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    // Vulpstation
    private void OnBeforeSpawn(PlayerBeforeSpawnEvent args)
    {
        var pointsTotal = _configuration.GetCVar(CCVars.GameTraitsDefaultPoints);
        var traitSelections = _configuration.GetCVar(CCVars.GameTraitsMax);

        if (args.JobId is not null && !_prototype.TryIndex<JobPrototype>(args.JobId, out var jobPrototype)
            && jobPrototype is not null && !jobPrototype.ApplyTraits)
            return;

        // We are kinda doing double work here, but I guess it's cheaper than spawning the character in and then erasing it
        var sortedTraits = new List<TraitPrototype>();
        foreach (var traitId in args.Profile.TraitPreferences)
            if (_prototype.TryIndex<TraitPrototype>(traitId, out var traitPrototype))
                sortedTraits.Add(traitPrototype);
        sortedTraits.Sort();


        foreach (var traitPrototype in sortedTraits) // Floof - changed to use the sorted list
        {
            // Moved converting to prototypes to above loop in order to sort before applying them. End Floof modifications.
            if (!_characterRequirements.CheckRequirementsValid(
                traitPrototype.Requirements,
                _prototype.Index<JobPrototype>(args.JobId ?? _prototype.EnumeratePrototypes<JobPrototype>().First().ID),
                args.Profile, _playTimeTracking.GetTrackerTimes(args.Player), args.Player.ContentData()?.Whitelisted ?? false, traitPrototype,
                EntityManager, _prototype, _configuration,
                out _))
                continue;

            // To check for cheaters. :FaridaBirb.png:
            pointsTotal += traitPrototype.Points;
            --traitSelections;
        }

        if (pointsTotal < 0 || traitSelections < 0)
        {
            args.Handled = true;
            if (_players.LobbyEnabled)
                _players.Respawn(args.Player);
        }
    }

    // When the player is spawned in, add all trait components selected during character creation
    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (args.JobId is not null && !_prototype.TryIndex<JobPrototype>(args.JobId, out var jobPrototype)
            && jobPrototype is not null && !jobPrototype.ApplyTraits)
            return;

        var sortedTraits = new List<TraitPrototype>();
        foreach (var traitId in args.Profile.TraitPreferences)
        {
            if (_prototype.TryIndex<TraitPrototype>(traitId, out var traitPrototype))
            {
                sortedTraits.Add(traitPrototype);
            }
            else
            {
                DebugTools.Assert($"No trait found with ID {traitId}!");
                // return; // Vulpstation - don't return here
            }
        }

        sortedTraits.Sort();
        // End Floof

        foreach (var traitPrototype in sortedTraits) // Floof - changed to use the sorted list
        {
            // Moved converting to prototypes to above loop in order to sort before applying them. End Floof modifications.
            if (!_characterRequirements.CheckRequirementsValid(
                traitPrototype.Requirements,
                _prototype.Index<JobPrototype>(args.JobId ?? _prototype.EnumeratePrototypes<JobPrototype>().First().ID),
                args.Profile, _playTimeTracking.GetTrackerTimes(args.Player), args.Player.ContentData()?.Whitelisted ?? false, traitPrototype,
                EntityManager, _prototype, _configuration,
                out _))
                continue;

            AddTrait(args.Mob, traitPrototype);
        }
    }

    /// <summary>
    ///     Adds a single Trait Prototype to an Entity.
    /// </summary>
    public void AddTrait(EntityUid uid, TraitPrototype traitPrototype)
    {
        foreach (var function in traitPrototype.Functions)
            function.OnPlayerSpawn(uid, _componentFactory, EntityManager, _serialization);
    }

    /// <summary>
    ///     On a non-cheating client, it's not possible to save a character with a negative number of traits. This can however
    ///     trigger incorrectly if a character was saved, and then at a later point in time an admin changes the traits Cvars to reduce the points.
    ///     Or if the points costs of traits is increased.
    /// </summary>
    private void PunishCheater(EntityUid uid)
    {
        _adminLog.Add(LogType.AdminMessage, LogImpact.High,
            $"{ToPrettyString(uid):entity} attempted to spawn with an invalid trait list. This might be a mistake, or they might be cheating");

        if (!_configuration.GetCVar(CCVars.TraitsPunishCheaters)
            || !_playerManager.TryGetSessionByEntity(uid, out var targetPlayer))
            return;

        // Vulpstation
        _chatManager.SendAdminAlert($"Player {ToPrettyString(uid):entity} spawned with an invalid trait list and got erased.");

        // For maximum comedic effect, this is plenty of time for the cheater to get on station and start interacting with people.
        // Vulpstation - no
        var timeToDestroy = 1f;

        Timer.Spawn(TimeSpan.FromSeconds(timeToDestroy), () => VaporizeCheater(targetPlayer));
    }

    /// <summary>
    ///     https://www.youtube.com/watch?v=X2QMN0a_TrA
    /// </summary>
    private void VaporizeCheater (Robust.Shared.Player.ICommonSession targetPlayer)
    {
        _adminSystem.Erase(targetPlayer);

        var feedbackMessage = $"[font size=24][color=#ff0000]{"You have spawned in with an illegal trait point total. If this was a result of malicious bug abuse, you should go read the rules. Otherwise, feel free to click 'Return To Lobby', and fix your trait selections. This incident will be reported."}[/color][/font]";
        _chatManager.ChatMessageToOne(
            ChatChannel.Emotes,
            feedbackMessage,
            feedbackMessage,
            EntityUid.Invalid,
            false,
            targetPlayer.Channel);

        // Vulpstation - make sure the ghost can return to lobby by settings their death time
        if (TryComp<GhostComponent>(targetPlayer.AttachedEntity, out var ghost))
            _ghosts.SetTimeOfDeath(targetPlayer.AttachedEntity.Value, TimeSpan.Zero, ghost);
    }
}
