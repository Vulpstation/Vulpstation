using Content.Server.Temperature.Systems;
using Content.Shared.Examine;
using Content.Shared.Placeable;


namespace Content.Server._Vulp.Temperature;


public sealed class EntityHeaterSystem : EntitySystem
{
    [Dependency] private readonly TemperatureSystem _temperature = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<IntrinsicEntityHeaterComponent, ExaminedEvent>(OnExamined);
    }

    public override void Update(float deltaTime)
    {
        var query = EntityQueryEnumerator<IntrinsicEntityHeaterComponent, ItemPlacerComponent>();
        while (query.MoveNext(out var uid, out var heater, out var placer))
        {
            var energy = heater.Power * deltaTime / placer.PlacedEntities.Count;
            foreach (var ent in placer.PlacedEntities)
                _temperature.ChangeHeat(ent, energy);
        }
    }

    private void OnExamined(EntityUid uid, IntrinsicEntityHeaterComponent comp, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString("entity-heater-intrinsic-examined"));
    }
}
