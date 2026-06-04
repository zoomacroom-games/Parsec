using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Rendering;
using Parsec.Rendering.Raymarching;
using SkiaSharp;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// Parameters for the Anisotropic Fold fractal -- Parsec's first numerical
/// delta-DE chapter. The map is an escape-time box-fold under an anisotropic
/// linear step: z = M*boxFold(z) + c, with
/// M = Rz(shearZ)*Ry(shearY)*diag(scale*rx, scale*ry, scale*rz). Because M
/// stretches space unequally per direction it is not a similarity, so a scalar
/// running derivative overestimates distance and tears holes; the core instead
/// finite-differences four orbits into the 3x3 Jacobian and returns |z|/||J||.
/// Validated in Python (shearfold_proto): delta-DE matches the exact matrix-DE
/// to ~1e-13; Frobenius ||J|| is a strict lower bound (hole-free).
/// </summary>
public sealed record AnisotropicParams
{
    public int Iterations { get; init; } = 10;

    /// <summary>||J|| norm: 0 = Frobenius (conservative, hole-free), 1 = sigma_max (tight).</summary>
    public int Mode { get; init; } = 0;
    /// <summary>0 = Mandelbrot (c = position), 1 = Julia (c = JuliaC).</summary>
    public int JuliaMode { get; init; } = 0;

    /// <summary>Overall escape-time scale.</summary>
    public float Scale { get; init; } = 2.0f;
    /// <summary>Per-axis stretch ratios. Non-uniform ratios = the anisotropy that forces delta-DE.</summary>
    public Vector3 Stretch { get; init; } = new(1.2f, 1.0f, 0.8f);

    /// <summary>Box-fold half-limit.</summary>
    public float FoldLimit { get; init; } = 1.0f;
    /// <summary>Shear rotation about Z (radians) -- the "lean".</summary>
    public float ShearZ { get; init; } = 0.5f;
    /// <summary>Shear rotation about Y (radians).</summary>
    public float ShearY { get; init; } = 0.0f;
    /// <summary>Escape bailout radius.</summary>
    public float Bailout { get; init; } = 12.0f;

    /// <summary>Julia constant (the additive c), used when JuliaMode == 1.</summary>
    public Vector3 JuliaC { get; init; } = Vector3.Zero;

    /// <summary>DE step fudge in (0,1]. Delta-DE is a finite difference, so a small
    /// margin (~0.9) absorbs the FD noise at the fold seams; lower it if seams sparkle.</summary>
    public float Fudge { get; init; } = 0.9f;
    /// <summary>Bounding sphere radius around the origin for the marcher fast-skip.</summary>
    public float BoundRadius { get; init; } = 5.0f;
}

/// <summary>
/// GPU raymarcher for the Anisotropic Fold delta-DE fractal. Owns only its
/// compute shader; shared buffers and the tile/AA loop live in RaymarchPipeline.
/// The compute cost is ~4x a scalar-DE chapter (the core runs four orbits per
/// pixel-step to finite-difference the Jacobian). Parameters pack into the
/// shared FoldParams slots reinterpreted by anisotropic_core.glsl.
/// </summary>
public sealed class GpuAnisotropicRenderer : IDisposable
{
    private readonly RaymarchPipeline _pipeline;
    private readonly ComputeShader _shader;
    private bool _disposed;

    public GpuAnisotropicRenderer(Gl gl, RaymarchPipeline pipeline)
    {
        _pipeline = pipeline;
        var src = ShaderLoader.LoadComposite("anisotropic_core.glsl", "raymarch_main.glsl");
        _shader = ComputeShader.FromSource(gl, src, "anisotropic");
    }

    /// <summary>Render and return the raw RGBA8 pixel buffer (one uint per pixel).</summary>
    public uint[] RenderToBuffer(
        AnisotropicParams an,
        Camera3D camera,
        int width, int height,
        RaymarchSettings settings,
        Color background, Color surface,
        Vector3 lightDirection,
        PaletteParams palette,
        int tileRows = 32,
        Action<int, int>? progress = null)
    {
        ThrowIfDisposed();

        // Pack into the shared FoldParams slots (see anisotropic_core.glsl):
        //   boxParams  = (scale, rx, ry, rz)
        //   surfParams = (foldLimit, shearZ, shearY, bailout)
        //   juliaC     = (cx, cy, cz, _)
        //   rot        = (_, _, _, fudge)
        var foldParams = new FoldParamsGpu
        {
            Iterations = an.Iterations,
            Mode = an.Mode,
            JuliaMode = an.JuliaMode,
            Pad0 = 0,
            BoxParams = new Vector4(an.Scale, an.Stretch.X, an.Stretch.Y, an.Stretch.Z),
            SurfParams = new Vector4(an.FoldLimit, an.ShearZ, an.ShearY, an.Bailout),
            JuliaCVec = new Vector4(an.JuliaC, 0f),
            Rot = new Vector4(0f, 0f, 0f, an.Fudge),
            BoundSphere = new Vector4(0, 0, 0, an.BoundRadius),
        };
        return _pipeline.Render(_shader, foldParams, camera, width, height, settings,
                                background, surface, lightDirection, palette,
                                heroSamples: settings.HeroSamples,
                                tileRows: tileRows, progress: progress);
    }

    /// <summary>Render and return an SKBitmap (for the headless/file path).</summary>
    public SKBitmap Render(
        AnisotropicParams an, Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface,
        Vector3 lightDirection, PaletteParams palette,
        int tileRows = 32, Action<int, int>? progress = null)
    {
        uint[] pixels = RenderToBuffer(an, camera, width, height, settings,
            background, surface, lightDirection, palette, tileRows, progress);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var bytes = new byte[pixels.Length * 4];
        System.Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GpuAnisotropicRenderer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
    }
}
