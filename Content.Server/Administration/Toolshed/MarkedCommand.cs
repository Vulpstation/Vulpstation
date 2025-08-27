using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Toolshed;

namespace Content.Server.Administration.Toolshed;

[ToolshedCommand, AdminCommand(AdminFlags.Admin)] // Vulp - admin flags to fix the issue with toolshed commands not being invocable by anyone
public sealed class MarkedCommand : ToolshedCommand
{
    [CommandImplementation]
    public IEnumerable<EntityUid> Marked(IInvocationContext ctx)
    {
        var res = (IEnumerable<EntityUid>?)ctx.ReadVar("marked");
        res ??= Array.Empty<EntityUid>();
        return res;
    }
}
