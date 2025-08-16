using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Shared._Vulp;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;


namespace Content.Server._Vulp.Admin;


public sealed class AccountAgeCheckerSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly IPlayerManager _playerMan = default!;
    [Dependency] private readonly IChatManager _chatMan = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IHttpClientHolder _http = default!;

    private bool _checkLocal;
    private int _minDays;
    private string _authServer = string.Empty;
    private List<INetChannel> _deniedChannels = new();

    public override void Initialize()
    {
        _netMan.Connected += (_, args) => Task.Run(() => CheckAccountAge(args.Channel));

        Subs.CVar(_cfg, VulpCCVars.CheckLocalhostAccountAge, it => _checkLocal = it, true);
        Subs.CVar(_cfg, VulpCCVars.MinimumAccountAgeDays, it => _minDays = it, true);
        Subs.CVar(_cfg, CVars.AuthServer, it => _authServer = it, true);
    }

    public override void Update(float frameTime)
    {
        // You may be screaming in terror reading this code, asking "why?!"
        // Well... it's because IoC is thread-local. And Loc.GetString used in SendAdminAlert always invoked IoC.
        lock (_deniedChannels)
        {
            foreach (var channel in _deniedChannels)
                _chatMan.SendAdminAlert($"User {channel.UserName}: failed to fetch account age. See server logs for details.");

            _deniedChannels.Clear();
        }
    }

    public async void CheckAccountAge(INetChannel channel)
    {
        try
        {
            var session = _playerMan.GetSessionByChannel(channel);
            var isLocal = AdminManager.IsLocal(session);
            if (!_checkLocal && (channel.AuthType != LoginType.LoggedIn || isLocal))
                return;

            var name = channel.UserData.UserName;
            if (isLocal && name.StartsWith("localhost@"))
                name = name["localhost@".Length..];

            var url = $"{_authServer}api/query/name?name={Uri.EscapeDataString(name)}";
            await DoChecks(session, url);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to check account age for {channel.UserName}: {e.Message}");
            lock (_deniedChannels)
                _deniedChannels.Add(channel);
        }
    }

    private async Task DoChecks(ICommonSession session, string url)
    {
        var data = await _http.Client.GetFromJsonAsync<UserDataResponse>(url);
        if (data is null)
            throw new($"Unknown error");

        var age = DateTime.UtcNow - data.CreatedTime.UtcDateTime;
        Log.Info($"User {session.Name}: account age is {age.TotalDays} days");
        if (age.TotalDays >= _minDays)
            return;

        session.Channel.Disconnect("Your SS14 account is too young.");
    }

    private sealed record UserDataResponse(string UserName, Guid UserId, DateTimeOffset CreatedTime);
}
