using System.Numerics;
using Parsec.Rendering.Gpu;

namespace Parsec.App;

/// <summary>
/// Mutable live state for the Orbit Hybrid prototype (KIFS + Mandelbox composed
/// into one orbit), plus a <see cref="ParamSchema"/>. The schema exposes both
/// formulas' knobs in one panel plus the hybridization controls (the per-formula
/// step counts that define the schedule) -- the prototype stand-in for the
/// eventual generic "Formula 2" dropdown + second panel.
///
/// Knob guide:
///   - KIFS steps / Mbox steps are the schedule: e.g. 1 and 2 means one KIFS
///     iteration then two Mandelbox iterations, repeating. The first iterations
///     dominate the overall shape, so the order and counts matter a lot.
///   - Mbox scale is the structural workhorse; negative values (the folding-box
///     inversion) give the richest sets -- default is -1.5.
///   - Box-fold limit is the magnitude cap that keeps the composed orbit bounded;
///     without it (as in the rejected Mandelbulb+KIFS pairing) the hybrid diverges.
/// </summary>
public sealed class OrbitHybridState
{
    public int Iterations = 16;

    public int KifsCount = 1;   // KIFS steps per schedule cycle
    public int MboxCount = 2;   // Mandelbox steps per schedule cycle

    public float KifsScale = 1.6f;
    public float MboxScale = -1.5f;

    public float MinRadius = 0.5f;
    public float FixedRadius = 1.0f;

    public float PostRotX = 0.2f, PostRotY = 0.1f, PostRotZ = 0.0f;  // radians

    public float BoxFoldLimit = 1.0f;
    public float Bailout = 30.0f;

    public float Fudge = 1.0f;
    public float BoundRadius = 16.0f;

    public OrbitHybridParams ToParams() => new()
    {
        Iterations = Iterations,
        KifsCount = KifsCount,
        MboxCount = MboxCount,
        KifsScale = KifsScale,
        MboxScale = MboxScale,
        MinRadius = MinRadius,
        FixedRadius = FixedRadius,
        PostRot = new Vector3(PostRotX, PostRotY, PostRotZ),
        BoxFoldLimit = BoxFoldLimit,
        Bailout = Bailout,
        Fudge = Fudge,
        BoundRadius = BoundRadius,
    };

    private const float Deg = MathF.PI / 180f;

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            // Hybridization (the schedule) -- the prototype's "Formula 2" controls.
            new ParamDescriptor {
                Label = "KIFS steps", Group = "Schedule", Min = 0, Max = 6, Step = 1, Decimals = 0,
                Get = () => KifsCount, Set = v => KifsCount = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Mandelbox steps", Group = "Schedule", Min = 0, Max = 6, Step = 1, Decimals = 0,
                Get = () => MboxCount, Set = v => MboxCount = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Iterations", Group = "Schedule", Min = 4, Max = 28, Step = 1, Decimals = 0,
                Get = () => Iterations, Set = v => Iterations = (int)Math.Round(v) },

            // KIFS formula.
            new ParamDescriptor {
                Label = "KIFS scale", Group = "KIFS", Min = -3.0, Max = 3.0, Decimals = 2,
                Get = () => KifsScale, Set = v => KifsScale = (float)v },
            new ParamDescriptor {
                Label = "Curl X", Group = "KIFS", Min = -90, Max = 90, Decimals = 0,
                Get = () => PostRotX / Deg, Set = v => PostRotX = (float)v * Deg },
            new ParamDescriptor {
                Label = "Curl Y", Group = "KIFS", Min = -90, Max = 90, Decimals = 0,
                Get = () => PostRotY / Deg, Set = v => PostRotY = (float)v * Deg },
            new ParamDescriptor {
                Label = "Curl Z", Group = "KIFS", Min = -90, Max = 90, Decimals = 0,
                Get = () => PostRotZ / Deg, Set = v => PostRotZ = (float)v * Deg },

            // Mandelbox formula.
            new ParamDescriptor {
                Label = "Mbox scale", Group = "Mandelbox", Min = -3.0, Max = 3.0, Decimals = 2,
                Get = () => MboxScale, Set = v => MboxScale = (float)v },
            new ParamDescriptor {
                Label = "Box fold limit", Group = "Mandelbox", Min = 0.3, Max = 2.0, Decimals = 2,
                Get = () => BoxFoldLimit, Set = v => BoxFoldLimit = (float)v },

            // Shared fold + quality.
            new ParamDescriptor {
                Label = "Min radius", Group = "Shared fold", Min = 0.0, Max = 1.5, Decimals = 2,
                Get = () => MinRadius, Set = v => MinRadius = (float)v },
            new ParamDescriptor {
                Label = "Fixed radius", Group = "Shared fold", Min = 0.3, Max = 2.0, Decimals = 2,
                Get = () => FixedRadius, Set = v => FixedRadius = (float)v },
            new ParamDescriptor {
                Label = "Bailout", Group = "Quality", Min = 6, Max = 60, Decimals = 1,
                Get = () => Bailout, Set = v => Bailout = (float)v },
            new ParamDescriptor {
                Label = "DE fudge", Group = "Quality", Min = 0.3, Max = 1.5, Decimals = 2,
                Get = () => Fudge, Set = v => Fudge = (float)v },
            new ParamDescriptor {
                Label = "Bound radius", Group = "Quality", Min = 2.0, Max = 10.0, Decimals = 1,
                Get = () => BoundRadius, Set = v => BoundRadius = (float)v },
        },
    };
}
