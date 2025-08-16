using Robust.Shared;
using Robust.Shared.Configuration;


namespace Content.Shared._Vulp;


[CVarDefs]
public sealed class VulpCCVars
{
    /// <summary>
    ///     The minimum age below which connections will be denied. Less or equal to 0 to disable.
    /// </summary>
    public static readonly CVarDef<int> MinimumAccountAgeDays =
        CVarDef.Create("admin.minimum_account_age_days", 14, CVar.SERVER);

    /// <summary>
    ///     Whether to enable account age checks for local players.
    /// </summary>
    public static readonly CVarDef<bool> CheckLocalhostAccountAge =
        CVarDef.Create("admin.minimum_account_age_check_localhost", false, CVar.SERVER);

    /// <summary>
    ///     Whether to start a public vote before automatically sending the emergency shuttle.
    /// </summary>
    public static readonly CVarDef<bool> DoEvacVotes =
        CVarDef.Create("shuttle.emergency_do_evac_votes", true, CVar.SERVER);

    /// <summary>
    ///     The duration of the public vote before automatically sending the emergency shuttle.
    /// </summary>
    public static readonly CVarDef<float> EvacVoteDuration =
        CVarDef.Create("shuttle.emergency_vote_duration", 120f, CVar.SERVER);

    /// <summary>
    ///     The size of the box around a player to check for biome chunks to load.
    ///     Generally you want this to be at least as high as the pvs range.
    /// </summary>
    public static readonly CVarDef<float> BiomeLoadingRange =
        CVarDef.Create("net.biome_loading_range", 25f, CVar.SERVER);
}
