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
