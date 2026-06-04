using System.Numerics;
using Parsec.Rendering.Gpu;

namespace Parsec.App;

/// <summary>
/// Mutable live state for the Anisotropic Fold fractal (Parsec's first delta-DE
/// chapter), plus a <see cref="ParamSchema"/>. Mirrors <see cref="MandalayState"/>;
/// the rendering side consumes an immutable <see cref="AnisotropicParams"/> snapshot.
///
/// Knob guide:
///   - Stretch X/Y/Z are the anisotropy. Equal values collapse to an ordinary
///     (scalar-DE-able) fold; the more unequal they are, the more the object
///     leans and skews -- and the more delta-DE earns its keep.
///   - Shear Z/Y rotate the stretch axes off the world axes (the "lean").
///   - Norm: Frobenius (0) is conservative and guaranteed hole-free; sigma_max
///     (1) is the tight DE (faster, crisper) but can sparkle at fold seams.
///   - DE fudge ~0.9 absorbs finite-difference noise at the seams.
/// </summary>
public sealed class AnisotropicState
{
    public int Iterations = 10;

    // 0 = Frobenius ||J|| (safe), 1 = sigma_max ||J|| (tight)
    public int Mode = 0;
    // 0 = Mandelbrot (c = position), 1 = Julia (c = JuliaC)
    public int JuliaMode = 0;

    public float Scale = 2.0f;
    public float StretchX = 1.2f, StretchY = 1.0f, StretchZ = 0.8f;

    public float FoldLimit = 1.0f;
    public float ShearZ = 0.5f;   // radians
    public float ShearY = 0.0f;   // radians
    public float Bailout = 12.0f;

    public float JuliaCx = 0.0f, JuliaCy = 0.0f, JuliaCz = 0.0f;

    public float Fudge = 0.9f;
    public float BoundRadius = 5.0f;

    public AnisotropicParams ToParams() => new()
    {
        Iterations = Iterations,
        Mode = Mode,
        JuliaMode = JuliaMode,
        Scale = Scale,
        Stretch = new Vector3(StretchX, StretchY, StretchZ),
        FoldLimit = FoldLimit,
        ShearZ = ShearZ,
        ShearY = ShearY,
        Bailout = Bailout,
        JuliaC = new Vector3(JuliaCx, JuliaCy, JuliaCz),
        Fudge = Fudge,
        BoundRadius = BoundRadius,
    };

    private const float Deg = MathF.PI / 180f;

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            // Form.
            new ParamDescriptor {
                Label = "Scale", Group = "Form", Min = -3.0, Max = 3.0, Decimals = 2,
                Get = () => Scale, Set = v => Scale = (float)v },
            new ParamDescriptor {
                Label = "Fold limit", Group = "Form", Min = 0.3, Max = 2.0, Decimals = 2,
                Get = () => FoldLimit, Set = v => FoldLimit = (float)v },

            // Anisotropy -- the whole reason this chapter needs delta-DE.
            new ParamDescriptor {
                Label = "Stretch X", Group = "Anisotropy", Min = 0.3, Max = 2.0, Decimals = 2,
                Get = () => StretchX, Set = v => StretchX = (float)v },
            new ParamDescriptor {
                Label = "Stretch Y", Group = "Anisotropy", Min = 0.3, Max = 2.0, Decimals = 2,
                Get = () => StretchY, Set = v => StretchY = (float)v },
            new ParamDescriptor {
                Label = "Stretch Z", Group = "Anisotropy", Min = 0.3, Max = 2.0, Decimals = 2,
                Get = () => StretchZ, Set = v => StretchZ = (float)v },

            // Shear (the lean) -- shown in degrees.
            new ParamDescriptor {
                Label = "Shear Z", Group = "Shear", Min = -90, Max = 90, Decimals = 0,
                Get = () => ShearZ / Deg, Set = v => ShearZ = (float)v * Deg },
            new ParamDescriptor {
                Label = "Shear Y", Group = "Shear", Min = -90, Max = 90, Decimals = 0,
                Get = () => ShearY / Deg, Set = v => ShearY = (float)v * Deg },

            // Modes.
            new ParamDescriptor {
                Label = "Norm: Frob/sigma (0/1)", Group = "Modes", Min = 0, Max = 1, Step = 1, Decimals = 0,
                Get = () => Mode, Set = v => Mode = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Julia (0/1)", Group = "Modes", Min = 0, Max = 1, Step = 1, Decimals = 0,
                Get = () => JuliaMode, Set = v => JuliaMode = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Julia C x", Group = "Julia constant", Min = -2.0, Max = 2.0, Decimals = 2,
                Get = () => JuliaCx, Set = v => JuliaCx = (float)v },
            new ParamDescriptor {
                Label = "Julia C y", Group = "Julia constant", Min = -2.0, Max = 2.0, Decimals = 2,
                Get = () => JuliaCy, Set = v => JuliaCy = (float)v },
            new ParamDescriptor {
                Label = "Julia C z", Group = "Julia constant", Min = -2.0, Max = 2.0, Decimals = 2,
                Get = () => JuliaCz, Set = v => JuliaCz = (float)v },

            // Quality.
            new ParamDescriptor {
                Label = "Iterations", Group = "Quality", Min = 4, Max = 20, Step = 1, Decimals = 0,
                Get = () => Iterations, Set = v => Iterations = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Bailout", Group = "Quality", Min = 4, Max = 30, Decimals = 1,
                Get = () => Bailout, Set = v => Bailout = (float)v },
            new ParamDescriptor {
                Label = "DE fudge", Group = "Quality", Min = 0.2, Max = 1.0, Decimals = 2,
                Get = () => Fudge, Set = v => Fudge = (float)v },
            new ParamDescriptor {
                Label = "Bound radius", Group = "Quality", Min = 2.0, Max = 10.0, Decimals = 1,
                Get = () => BoundRadius, Set = v => BoundRadius = (float)v },
        },
    };
}
