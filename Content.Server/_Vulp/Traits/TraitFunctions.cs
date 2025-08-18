using Content.Shared.Traits;
using Robust.Shared.Serialization.Manager;


namespace Content.Server._Vulp.Traits;


/// <summary>
///     Modifies particular fields of a component using reflection.
/// </summary>
public sealed partial class TraitModifyComponent : TraitFunction
{
    [DataField(required: true)]
    public string Component = string.Empty;

    [DataField(required: true)]
    public Dictionary<string, object?> Replacements = new();


    public override void OnPlayerSpawn(
        EntityUid mob,
        IComponentFactory factory,
        IEntityManager entityManager,
        ISerializationManager serializationManager)
    {
        if (!factory.TryGetRegistration(Component, out var registration))
            throw new ArgumentException($"TraitModifyComponent: component does not exist: {Component}");

        if (!entityManager.TryGetComponent(mob, registration.Idx, out var comp))
            return;

        foreach (var (key, value) in Replacements)
        {
            var field = registration.Type.GetField(key);
            if (field is null)
            {
                Logger.GetSawmill("TraitModifyComponent").Error($"Component {registration.Name}: field does not exist: {key}");
                continue;
            }

            field.SetValue(comp, value);
        }
    }
}
