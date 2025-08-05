namespace Content.Server._Vulp.Temperature;


[RegisterComponent]
public sealed partial class IntrinsicEntityHeaterComponent : Component
{
    [DataField]
    public float Power = 2400;
}
