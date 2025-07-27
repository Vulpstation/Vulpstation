namespace Content.Server.Gatherable.Components;


/// <summary>
///     Vulpstation - stores the efficiency of a gathering tool (a pickaxe).
/// </summary>
[RegisterComponent]
public sealed partial class GathererComponent : Component
{
    [DataField]
    public float Efficiency = 1f;
}
