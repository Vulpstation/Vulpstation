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
    [Dependency] private readonly GameTicker _gameTicker = default!;

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
            // Don't let em spawn
            args.Handled = true;

            var feedbackMessage =
                $"[font size=14][color=#ff0000]You have tried to spawn with an illegal trait point total: {pointsTotal} points, {traitSelections} slots." +
                $"If this was a result of malicious bug abuse, you should go read the rules." +
                $"Otherwise, feel free to fix your trait selections and try again. This incident will be reported.[/color][/font]";

            _chatManager.ChatMessageToOne(
                ChatChannel.OOC,
                feedbackMessage,
                feedbackMessage,
                EntityUid.Invalid,
                false,
                args.Player.Channel);

            if (_gameTicker.LobbyEnabled)
                Timer.Spawn(TimeSpan.FromSeconds(5), () => _gameTicker.Respawn(args.Player));
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
}
