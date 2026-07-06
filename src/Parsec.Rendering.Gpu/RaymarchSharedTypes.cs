using System.Numerics;
using System.Runtime.InteropServices;

namespace Parsec.Rendering.Raymarching;

/// <summary>
/// Per-fractal parameters (binding 1). Layout matches the FoldParams SSBO
/// in every *_core.glsl. The semantics of BoxParams / SurfParams / Rot vary
/// by fractal -- each Gpu*Renderer documents how it packs its own values.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FoldParamsGpu
{
    public int Iterations;
    public int Mode;        // unused by most cores; reserved
    public int JuliaMode;   // unused by most cores; reserved
    public int Pad0;

    public Vector4 BoxParams;
    public Vector4 SurfParams;
    public Vector4 JuliaCVec;
    public Vector4 Rot;
    public Vector4 BoundSphere;

    // Geometric orbit traps (quaternion Julia core; other cores leave these
    // zero). TrapA = (center.xyz, radius); TrapB = (sine amp, sine freq,
    // trap DE fudge, _). The shape/solid selectors ride Mode/JuliaMode.
    public Vector4 TrapA;
    public Vector4 TrapB;

    // Spatially varying c (quaternion Julia core). CVary = (axis 0off/1x/2y/3z,
    // dc.x, dc.y, dc.z): c(p) = JuliaCVec + p[axis]*dc.xyz.
    public Vector4 CVary;

    // Octonion core only (appended; other cores declare the shorter prefix and
    // ignore these). c, p, q as octonions: Lo = components 0..3, Hi = 4..7.
    public Vector4 OctCLo;
    public Vector4 OctCHi;
    public Vector4 OctPLo;
    public Vector4 OctPHi;
    public Vector4 OctQLo;
    public Vector4 OctQHi;
}

/// <summary>
/// Camera + lighting + tile + palette + AA jitter (binding 4). Layout matches
/// the RenderParams SSBO in raymarch_main.glsl. Uploaded once per tile.
///
/// SubpixelJitter is the new field for SSAA: (0,0) for preview/single-sample,
/// Halton-jittered for multi-sample hero renders.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RenderParamsGpu
{
    public int ImageWidth, ImageHeight, RowOffset, RowCount;
    public Vector4 CamPos, CamForward, CamRight, CamUp, TanFov;
    public Vector4 LightDir, Background, Surface;
    public Vector4 MarchA, MarchB;
    public int MarchI0, MarchI1, MarchI2, MarchI3;
    public Vector4 PalBase, PalAmp, PalPhase, TrapMix;
    public Vector4 SubpixelJitter;   // (jx, jy, _, _) in [-0.5, 0.5]
    public Vector4 ReflectParams;    // (enable, maxBounces, gloss, F0)

    // Environment: procedural skybox + reflective floor plane (raymarch_main).
    // Zeroed = legacy flat background, no floor.
    public Vector4 SkyParams;        // (skyMode 0/1, sun intensity, sun sharpness, floor enable)
    public Vector4 SkyZenith;        // (zenith rgb sRGB, floor height)
    public Vector4 SkyHorizon;       // (horizon rgb sRGB, floor reflectivity)
    public Vector4 SkyGround;        // (ground rgb sRGB, floor checker scale)
    public Vector4 FloorColor;       // (floor rgb sRGB, _)

    // Object-space fractal rotation: columns of the object->world rotation R.
    // Identity by default (byte-identical output).
    public Vector4 FracRot0;
    public Vector4 FracRot1;
    public Vector4 FracRot2;
}

/// <summary>
/// A placeable emissive sphere light: rays that hit it show a luminous orb,
/// and every fractal hit receives its diffuse contribution as a point light
/// with inverse-square falloff. Color is authored sRGB in [0,1] per channel
/// (the shader decodes to linear); it tints both the visible orb and its
/// light. Public API type -- the App layer builds these;
/// <see cref="RaymarchPipeline.SetOrbs"/> packs them into the GPU layout.
/// </summary>
public readonly record struct OrbLight(
    Vector3 Position, float Radius, float Luminosity, Vector3 Color);

/// <summary>
/// GPU layout for one orb (binding 10). Matches the OrbData SSBO in
/// raymarch_main.glsl: PosRad = (center.xyz, radius), ColorLum = (rgb, luminosity).
/// The active count rides RenderParams.MarchB.z (a previously unused slot).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct OrbGpu
{
    public Vector4 PosRad;
    public Vector4 ColorLum;
}

/// <summary>
/// Parameters for the clear/finalize passes (binding 3). Tells the AA helper
/// shaders how big the image is and how many samples to average. Bindings 2/3
/// are used (not 6/7) to stay clear of the attractor core's trajectory/hash/
/// index SSBOs at bindings 6/7/8.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AAParamsGpu
{
    public int Width;
    public int Height;
    public int SampleCount;
    public int Pad0;
}

/// <summary>
/// Camera frame built once per render call. Was previously duplicated in
/// every Gpu*Renderer.cs as a private nested struct; lifted here so all the
/// renderers can share the same construction.
/// </summary>
internal readonly struct CameraFrame
{
    public readonly Vector3 Forward, Right, Up;
    public readonly float TanFovX, TanFovY;

    private CameraFrame(Vector3 f, Vector3 r, Vector3 u, float tx, float ty)
    { Forward = f; Right = r; Up = u; TanFovX = tx; TanFovY = ty; }

    public static CameraFrame Build(Camera3D cam, int w, int h)
    {
        // Use the camera's own basis (which guards the degenerate up-parallel-
        // to-forward case) instead of re-deriving it here; only the horizontal
        // tan is recomputed so it tracks the actual render dimensions.
        float tanY = MathF.Tan(cam.VerticalFovRadians * 0.5f);
        float tanX = tanY * ((float)w / h);
        return new CameraFrame(cam.Forward, cam.Right, cam.UpPrime, tanX, tanY);
    }
}
