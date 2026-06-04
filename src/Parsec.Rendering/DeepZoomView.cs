using System.Numerics;

namespace Parsec.Rendering.DeepZoom;

/// <summary>
/// The 2D deep-zoom view model -- the replacement for the 3D camera in
/// Mandelbrot mode. Plain serializable data (so the deferred zoom-video keyframe
/// support stays cheap): a high-precision center as decimal strings, a double
/// half-height radius, and an iteration cap. Pan is applied at full precision via
/// <see cref="BinaryFixed"/>; zoom just scales the radius. Required fixed-point
/// bit count scales with depth.
/// </summary>
public sealed class DeepZoomView
{
    /// <summary>View center real part, arbitrary-precision decimal string.</summary>
    public string CenterRe { get; set; } = "-0.5";
    /// <summary>View center imaginary part, arbitrary-precision decimal string.</summary>
    public string CenterIm { get; set; } = "0.0";
    /// <summary>Half-height of the view in complex units (the zoom level).</summary>
    public double Radius { get; set; } = 1.5;
    /// <summary>Iteration floor (and the minimum reference-orbit length). The
    /// effective cap scales above this with depth -- see IterationsForDepth().</summary>
    public int MaxIterations { get; set; } = 2000;

    /// <summary>Which 2D escape-time formula the deep-zoom pipeline runs.
    /// 0 = Mandelbrot (Z^2+C), 1 = Prospector real-2D map, 2 = Julia (Z^2+kappa,
    /// dynamical plane), 3 = Burning Ship. Serialized with the view so zoom-video
    /// keyframes capture it.</summary>
    public int Formula { get; set; } = 0;

    /// <summary>Julia constant kappa (formula 2 only). The dynamical plane fixes
    /// kappa and lets the pixel be the seed; these are O(1) so doubles are plenty
    /// (the high-precision quantity is the zoom CENTER, i.e. the seed, not kappa).
    /// Keyframeable -- sweeping kappa morphs the Julia set for animations.</summary>
    public double KappaRe { get; set; } = -0.8;
    public double KappaIm { get; set; } = 0.156;

    /// <summary>Reset the center/radius to a sensible whole-set home for the
    /// current <see cref="Formula"/>. Called when the formula is switched, since
    /// each set lives in a different region (the Mandelbrot home shows nothing of
    /// the Burning Ship, etc.). Leaves kappa untouched -- that is the user's dial.</summary>
    public void ApplyFormulaHome()
    {
        switch (Formula)
        {
            case 1: CenterRe = "0.0";  CenterIm = "0.0";  Radius = 2.5; break;  // Prospector
            case 2: CenterRe = "0.0";  CenterIm = "0.0";  Radius = 1.5; break;  // Julia (seed plane)
            case 3: CenterRe = "-0.5"; CenterIm = "-0.5"; Radius = 1.5; break;  // Burning Ship
            default: CenterRe = "-0.5"; CenterIm = "0.0"; Radius = 1.5; break;  // Mandelbrot
        }
    }

    /// <summary>Iteration cap scaled with zoom depth. Boundary dwell times grow
    /// with the number of e-foldings, so a fixed cap starves detail past ~1e-15
    /// (filaments that need thousands of iterations get tagged in-set). This
    /// scales the cap with depth, keeping MaxIterations as a floor. The
    /// coefficients are a location-independent heuristic -- some spots want
    /// more, some less -- but they track the boundary far better than a constant.</summary>
    public int IterationsForDepth()
    {
        double zoom = Math.Max(0.0, -Math.Log10(Radius));     // decimal e-foldings in
        return Math.Max(MaxIterations, 1000 + (int)(1000.0 * zoom));
    }

    /// <summary>Fixed-point fractional bits needed at the current depth, with margin.</summary>
    public int PrecisionBits()
    {
        int depthDigits = Math.Max(15, (int)Math.Ceiling(-Math.Log10(Radius)) + 4);
        return ReferenceOrbit.RecommendedPrecisionBits(depthDigits);
    }

    /// <summary>Deepest radius the renderer supports on the fast fp64 path.
    /// Below ~1e-148 fp64 dz^2 underflows; the validated floatexp fallback is
    /// correct but, at the ~150k iterations that depth already demands, 3-5x too
    /// slow per iteration to be usable. We cap here -- still ~147 orders of zoom,
    /// comfortably "deep zoom". Raise this (toward 1e-148) only if floatexp gains
    /// a scaled-double fast path. One-line knob.</summary>
    public const double MinRadius = 1e-147;

    /// <summary>At or above this radius the pixel coordinate (center + offset)
    /// is comfortably representable in fp64, so the pipeline iterates each pixel
    /// DIRECTLY in double precision -- exact, and the only reliable path for the
    /// Burning Ship, whose abs map makes perturbation unstable when the delta is
    /// large (wide views). Below this, perturbation takes over (validated clean
    /// at r &lt;= 1e-6). The two regimes overlap, so the exact handoff is not
    /// delicate; this sits well inside both.</summary>
    public const double DirectRadius = 1e-6;

    /// <summary>True when the shallow direct-fp64 path should be used.</summary>
    public bool UseDirectPath => Radius > DirectRadius;

    /// <summary>Multiply the zoom radius (factor &lt; 1 zooms in).</summary>
    public void ZoomBy(double factor)
        => Radius = Math.Clamp(Radius * factor, MinRadius, 4.0);

    /// <summary>Zoom by <paramref name="factor"/> while keeping the complex point
    /// currently under the given pixel fixed on screen (zoom toward cursor). The
    /// center shifts by (1-factor)*offset, where offset is the complex vector from
    /// the view center to the cursor; the per-step pan also keeps the center's
    /// precision in step with the deepening zoom.</summary>
    public void ZoomTowardPixel(double factor, double pixelX, double pixelY, int width, int height)
    {
        double spacing = SpacingFor(height);
        double offRe = (pixelX - width / 2.0) * spacing;
        double offIm = -(pixelY - height / 2.0) * spacing;
        PanComplex((1.0 - factor) * offRe, (1.0 - factor) * offIm);
        ZoomBy(factor);
    }

    /// <summary>Shift the center by a complex offset (in complex units), at full
    /// precision -- the offset is a double but is placed at the correct binary
    /// position in the P-bit center, so deep pans don't lose precision.</summary>
    public void PanComplex(double dRe, double dIm)
    {
        int P = PrecisionBits();
        int digits = (int)Math.Ceiling(P * 0.3010299957) + 2;
        BigInteger cre = BinaryFixed.FromDecimal(CenterRe, P) + RoundToFixed(dRe, P);
        BigInteger cim = BinaryFixed.FromDecimal(CenterIm, P) + RoundToFixed(dIm, P);
        CenterRe = BinaryFixed.ToDecimal(cre, P, digits);
        CenterIm = BinaryFixed.ToDecimal(cim, P, digits);
    }

    /// <summary>Drag-pan by a pixel delta given the current resolution. Dragging
    /// the image right moves the view left (content follows the cursor).</summary>
    public void PanPixels(double dxPixels, double dyPixels, int height)
    {
        double spacing = (2.0 * Radius) / height;
        PanComplex(-dxPixels * spacing, dyPixels * spacing);
    }

    /// <summary>Complex-units-per-pixel at a given output height.</summary>
    public double SpacingFor(int height) => (2.0 * Radius) / height;

    // round(v * 2^P) as BigInteger, exact for the value's 53 mantissa bits.
    // (v*2^P stays ~ pixels * 2^margin well under 2^53 because P tracks depth,
    // so ScaleB then round-to-integer is exact.)
    private static BigInteger RoundToFixed(double v, int P)
        => v == 0.0 ? BigInteger.Zero : new BigInteger(Math.Round(Math.ScaleB(v, P)));
}
