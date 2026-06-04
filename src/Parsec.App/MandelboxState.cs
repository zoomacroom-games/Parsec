using System.Numerics;
using Parsec.Rendering.Gpu;

namespace Parsec.App;

/// <summary>
/// Mutable live state for the classic Tom Lowe Mandelbox -- the plain box fold
/// (mode 0), NOT the abs-into-the-positive-octant "Amazing" fold that
/// <see cref="AmazingBoxState"/> selects (mode 1). Same renderer, same DE, same
/// shader (<c>mandelbox_core.glsl</c>): the only difference is the fold mode and
/// the absence of inter-fold rotation. Mode 0 + negative scale is what produces
/// the hollow "arches and corridors" look; the abs fold instead fills the space
/// into a solid, 8-fold-symmetric slab.
///
/// Distinct from <see cref="RotBoxState"/> (the rotated Mandelbox): this one
/// defaults to no rotation, the canonical un-curled form. The rotation sliders
/// are still here for tweaking, but at 0 the DE is the clean, exact box+sphere
/// fold estimator, so the marcher can step at nearly the full DE.
/// </summary>
public sealed class MandelboxState
{
    public int Iterations = 14;
    public float Scale = -1.5f;        // negative -> the classic hollow form
    public float FoldingLimit = 1.0f;
    public float MinRadius = 0.5f;
    public float FixedRadius = 1.0f;
    public float RotX = 0f;            // classic Mandelbox is un-rotated
    public float RotY = 0f;
    public float RotZ = 0f;
    public float Fudge = 0.9f;         // clean DE without rotation; raise toward 1.0 for speed

    public MandelboxParams ToParams() => new()
    {
        Iterations = Iterations,
        Mode = 0,                      // classic box fold (NOT the abs "Amazing" fold)
        Scale = Scale,
        FoldingLimit = FoldingLimit,
        MinRadius = MinRadius,
        FixedRadius = FixedRadius,
        RotationRadians = new Vector3(RotX, RotY, RotZ),
        Fudge = Fudge,
        BoundRadius = 6.0f,
    };

    private const float Deg = MathF.PI / 180f;

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            // Fold shape -- the parameters that define the attractor itself.
            new ParamDescriptor {
                Label = "Scale", Group = "Fold", Min = -3.0, Max = 2.0, Decimals = 2,
                Get = () => Scale, Set = v => Scale = (float)v },
            new ParamDescriptor {
                Label = "Min radius", Group = "Fold", Min = 0.05, Max = 1.0, Decimals = 2,
                Get = () => MinRadius, Set = v => MinRadius = (float)v },
            new ParamDescriptor {
                Label = "Fixed radius", Group = "Fold", Min = 0.5, Max = 2.0, Decimals = 2,
                Get = () => FixedRadius, Set = v => FixedRadius = (float)v },
            new ParamDescriptor {
                Label = "Folding limit", Group = "Fold", Min = 0.5, Max = 2.0, Decimals = 2,
                Get = () => FoldingLimit, Set = v => FoldingLimit = (float)v },

            // Rotation between folds -- 0 for the classic Mandelbox; nonzero curls
            // it (cf. the Rotated Mandelbox chapter). Degrees in UI, radians under.
            new ParamDescriptor {
                Label = "Rotate X", Group = "Rotation", Min = -45, Max = 45, Decimals = 0,
                Get = () => RotX / Deg, Set = v => RotX = (float)v * Deg },
            new ParamDescriptor {
                Label = "Rotate Y", Group = "Rotation", Min = -45, Max = 45, Decimals = 0,
                Get = () => RotY / Deg, Set = v => RotY = (float)v * Deg },
            new ParamDescriptor {
                Label = "Rotate Z", Group = "Rotation", Min = -45, Max = 45, Decimals = 0,
                Get = () => RotZ / Deg, Set = v => RotZ = (float)v * Deg },

            // Quality / iteration.
            new ParamDescriptor {
                Label = "Iterations", Group = "Quality", Min = 4, Max = 24, Step = 1, Decimals = 0,
                Get = () => Iterations, Set = v => Iterations = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "DE fudge", Group = "Quality", Min = 0.3, Max = 1.0, Decimals = 2,
                Get = () => Fudge, Set = v => Fudge = (float)v },
        },
    };
}
