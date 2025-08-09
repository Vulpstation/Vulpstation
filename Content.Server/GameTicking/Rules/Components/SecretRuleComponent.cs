using Content.Shared.Random;
using Robust.Shared.Prototypes;


namespace Content.Server.GameTicking.Rules.Components;

[RegisterComponent, Access(typeof(SecretRuleSystem))]
public sealed partial class SecretRuleComponent : Component
{
    /// <summary>
    /// The gamerules that get added by secret.
    /// </summary>
    [DataField("additionalGameRules")]
    public HashSet<EntityUid> AdditionalGameRules = new();

    // Vulpstation
    [DataField("pool")]
    public ProtoId<WeightedRandomPrototype> Pool = "SecretBasic";
}
