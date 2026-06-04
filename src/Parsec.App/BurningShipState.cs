using Parsec.Rendering.Gpu;

namespace Parsec.App;

/// <summary>
/// Mutable live state for the 3D Burning Ship, plus a <see cref="ParamSchema"/>.
/// Power is the headline morphology knob. NOTE: unlike the Mandelbulb (where 8 is
/// the money value), the burning ship's characteristic swept, windblown laminar
/// sheets live at LOW power (~2-3). At high power the r^n radial growth dominates
/// and the abs() fold degenerates into a flat-bottomed Mandelbulb look, so the
/// default is 2. It is a smooth scalar, so it also makes a lovely animation
/// target. DE fudge defaults below 1.0 because the abs() folds make the
/// scalar-derivative DE more aggressive than the plain Mandelbulb near the poles.
/// </summary>
public sealed class BurningShipState
{
    public int Iterations = 16;
    public float Power = 2.0f;
    public float Bailout = 2.0f;
    public float Fudge = 0.75f;

    public BurningShipParams ToParams() => new()
    {
        Iterations = Iterations,
        Power = Power,
        Bailout = Bailout,
        Fudge = Fudge,
        BoundRadius = 2.0f,
    };

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            new ParamDescriptor {
                Label = "Power", Group = "Form", Min = 1.4, Max = 12, Decimals = 2,
                Get = () => Power, Set = v => Power = (float)v },
            new ParamDescriptor {
                Label = "Iterations", Group = "Quality", Min = 4, Max = 64, Step = 1, Decimals = 0,
                Get = () => Iterations, Set = v => Iterations = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Bailout", Group = "Quality", Min = 1.5, Max = 4.0, Decimals = 2,
                Get = () => Bailout, Set = v => Bailout = (float)v },
            new ParamDescriptor {
                Label = "DE fudge", Group = "Quality", Min = 0.4, Max = 1.0, Decimals = 2,
                Get = () => Fudge, Set = v => Fudge = (float)v },
        },
    };
}
