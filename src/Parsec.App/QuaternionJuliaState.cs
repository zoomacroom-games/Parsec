using System.Numerics;
using Parsec.Rendering.Gpu;

namespace Parsec.App;

/// <summary>
/// Mutable live state for the quaternion Julia set. The headline knobs are the
/// quaternion constant c (the shape's identity), the 4D slice 'wslice' (which 3D
/// shadow of the 4D object we see), and the half-cut plane offset (sweeping it
/// slices through the solid to reveal the nested interior). All are smooth
/// scalars -- great keyframe/animation targets.
///
/// Stereographic mode swaps the flat slice for a curved (3-sphere) cut; WSlice
/// has no effect while it is on, but the half-cut plane still applies.
/// </summary>
public sealed class QuaternionJuliaState
{
    public int Iterations = 10;
    public float Cx = -0.2f, Cy = 0.8f, Cz = 0.0f, Cw = 0.0f;
    public float WSlice = 0.0f;
    public int Cut = 1;                 // 0/1 toggle
    public int CutAxis = 0;             // 0=X, 1=Y, 2=Z
    public float PlaneOffset = 0.0f;    // sweep to slice through the solid
    public float Fudge = 0.9f;

    public int Stereo = 0;              // 0/1 toggle: flat vs stereographic slice
    public float StereoK = 1.0f;        // input pre-scale (frames the wrap)
    public float StereoR = 0.8f;        // sphere radius (~boundary => separated lobes)

    // Geometric orbit traps (iq's 3D orbit traps): a shape the orbit is tested
    // against every iteration. Mode 1 (union) materializes the shape as
    // geometry repeated through the set; mode 2 (fibers) renders ONLY the trap
    // tubes -- with a sphere trap at a slowly-attracting fixed point that gives
    // orbit-streamline fiber bundles; mode 0 just drives the shell glaze.
    public int TrapShape = 0;           // 0 off, 1 sphere, 2 cylinder, 3 plane, 4 sine
    public int TrapMode = 1;            // 0 color-only, 1 union, 2 trap-only (fibers)
    public float TrapX = 0.45f, TrapY = 0.0f, TrapZ = 0.55f;
    public float TrapRadius = 0.1f;
    public float TrapWaveAmp = 0.25f;   // sine sheet only
    public float TrapWaveFreq = 4.0f;   // sine sheet only
    public float TrapFudge = 0.7f;

    public QuaternionJuliaParams ToParams() => new()
    {
        Iterations = Iterations,
        C = new Vector4(Cx, Cy, Cz, Cw),
        WSlice = WSlice,
        Cut = Cut >= 1,
        CutAxis = CutAxis,
        PlaneOffset = PlaneOffset,
        Stereo = Stereo >= 1,
        StereoK = StereoK,
        StereoR = StereoR,
        Fudge = Fudge,
        BoundRadius = 2.0f,
        TrapShape = TrapShape,
        TrapMode = TrapMode,
        TrapCenter = new Vector3(TrapX, TrapY, TrapZ),
        TrapRadius = TrapRadius,
        TrapWaveAmp = TrapWaveAmp,
        TrapWaveFreq = TrapWaveFreq,
        TrapFudge = TrapFudge,
    };

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            new ParamDescriptor {
                Label = "c.x", Group = "Constant c", Min = -1, Max = 1, Decimals = 3,
                Get = () => Cx, Set = v => Cx = (float)v },
            new ParamDescriptor {
                Label = "c.y", Group = "Constant c", Min = -1, Max = 1, Decimals = 3,
                Get = () => Cy, Set = v => Cy = (float)v },
            new ParamDescriptor {
                Label = "c.z", Group = "Constant c", Min = -1, Max = 1, Decimals = 3,
                Get = () => Cz, Set = v => Cz = (float)v },
            new ParamDescriptor {
                Label = "c.w", Group = "Constant c", Min = -1, Max = 1, Decimals = 3,
                Get = () => Cw, Set = v => Cw = (float)v },

            new ParamDescriptor {
                Label = "4D slice (w)", Group = "Slice & Cut", Min = -1, Max = 1, Decimals = 3,
                Get = () => WSlice, Set = v => WSlice = (float)v },
            new ParamDescriptor {
                Label = "Cut (0/1)", Group = "Slice & Cut", Min = 0, Max = 1, Step = 1, Decimals = 0,
                Get = () => Cut, Set = v => Cut = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Cut axis (0X 1Y 2Z)", Group = "Slice & Cut", Min = 0, Max = 2, Step = 1, Decimals = 0,
                Get = () => CutAxis, Set = v => CutAxis = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Cut plane offset", Group = "Slice & Cut", Min = -1.2, Max = 1.2, Decimals = 3,
                Get = () => PlaneOffset, Set = v => PlaneOffset = (float)v },

            // Curved slice: 1 = wrap R^3 onto a 3-sphere. R near the boundary
            // (~0.7-0.9) gives the separated-lobe view; k frames the wrap.
            new ParamDescriptor {
                Label = "Stereographic (0/1)", Group = "Stereographic", Min = 0, Max = 1, Step = 1, Decimals = 0,
                Get = () => Stereo, Set = v => Stereo = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Stereo scale k", Group = "Stereographic", Min = 0.3, Max = 3.0, Decimals = 2,
                Get = () => StereoK, Set = v => StereoK = (float)v },
            new ParamDescriptor {
                Label = "Stereo radius R", Group = "Stereographic", Min = 0.3, Max = 1.6, Decimals = 2,
                Get = () => StereoR, Set = v => StereoR = (float)v },

            // Geometric orbit traps. The default center/radius reproduce the
            // cylinder trap from iq's reference shader (shadertoy 3tsyzl).
            new ParamDescriptor {
                Label = "Trap (0off 1sph 2cyl 3pln 4sin)", Group = "Orbit trap", Min = 0, Max = 4, Step = 1, Decimals = 0,
                Get = () => TrapShape, Set = v => TrapShape = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Mode (0col 1union 2fibers)", Group = "Orbit trap", Min = 0, Max = 2, Step = 1, Decimals = 0,
                Get = () => TrapMode, Set = v => TrapMode = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Trap x", Group = "Orbit trap", Min = -1.5, Max = 1.5, Decimals = 3,
                Get = () => TrapX, Set = v => TrapX = (float)v },
            new ParamDescriptor {
                Label = "Trap y", Group = "Orbit trap", Min = -1.5, Max = 1.5, Decimals = 3,
                Get = () => TrapY, Set = v => TrapY = (float)v },
            new ParamDescriptor {
                Label = "Trap z", Group = "Orbit trap", Min = -1.5, Max = 1.5, Decimals = 3,
                Get = () => TrapZ, Set = v => TrapZ = (float)v },
            new ParamDescriptor {
                Label = "Trap radius", Group = "Orbit trap", Min = 0.02, Max = 1.0, Decimals = 3,
                Get = () => TrapRadius, Set = v => TrapRadius = (float)v },
            new ParamDescriptor {
                Label = "Wave amplitude", Group = "Orbit trap", Min = 0.0, Max = 0.8, Decimals = 3,
                Get = () => TrapWaveAmp, Set = v => TrapWaveAmp = (float)v },
            new ParamDescriptor {
                Label = "Wave frequency", Group = "Orbit trap", Min = 0.0, Max = 12.0, Decimals = 2,
                Get = () => TrapWaveFreq, Set = v => TrapWaveFreq = (float)v },
            new ParamDescriptor {
                Label = "Trap DE fudge", Group = "Orbit trap", Min = 0.3, Max = 1.0, Decimals = 2,
                Get = () => TrapFudge, Set = v => TrapFudge = (float)v },

            // Fiber-mode traps need long orbits (approach time to the trapped
            // fixed point), hence the high ceiling.
            new ParamDescriptor {
                Label = "Iterations", Group = "Quality", Min = 4, Max = 256, Step = 1, Decimals = 0,
                Get = () => Iterations, Set = v => Iterations = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "DE fudge", Group = "Quality", Min = 0.4, Max = 1.0, Decimals = 2,
                Get = () => Fudge, Set = v => Fudge = (float)v },
        },
    };
}
