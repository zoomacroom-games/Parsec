using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Rendering;
using Parsec.Rendering.Raymarching;
using SkiaSharp;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// Parameters for the Orbit Hybrid prototype -- two formulas (KIFS + Mandelbox)
/// composed into ONE escape-time orbit, sharing a single z and running
/// derivative dr, with the active formula chosen each iteration by a repeating
/// schedule (kifsCount KIFS steps, then mboxCount Mandelbox steps, cycled).
///
/// This is the "new shape from composition" hybrid, the prototype that decides
/// whether the full all-cores orbit-hybrid refactor is worth doing. The pairing
/// is deliberately KIFS+Mandelbox (not Mandelbulb+KIFS, which a parameter study
/// proved degenerate -- no magnitude-capping fold, so every orbit diverges).
/// Mandelbox's box fold supplies the cap that keeps the composed orbit bounded.
/// Validated in Python: bounded fraction up to ~0.61; scalar dr stays a
/// conservative (hole-free) derivative, so the cheap |z|/dr DE is used.
/// </summary>
public sealed record OrbitHybridParams
{
    public int Iterations { get; init; } = 16;

    /// <summary>KIFS steps per schedule cycle.</summary>
    public int KifsCount { get; init; } = 1;
    /// <summary>Mandelbox steps per schedule cycle.</summary>
    public int MboxCount { get; init; } = 2;

    public float KifsScale { get; init; } = 1.6f;
    public float MboxScale { get; init; } = -1.5f;

    /// <summary>Shared sphere-fold inner radius.</summary>
    public float MinRadius { get; init; } = 0.5f;
    /// <summary>Shared sphere-fold outer (fixed) radius.</summary>
    public float FixedRadius { get; init; } = 1.0f;

    /// <summary>KIFS post-fold rotation (Euler radians) -- the curl generator.</summary>
    public Vector3 PostRot { get; init; } = new(0.2f, 0.1f, 0.0f);

    /// <summary>Mandelbox box-fold half-limit (the magnitude cap that bounds the hybrid).</summary>
    public float BoxFoldLimit { get; init; } = 1.0f;
    /// <summary>Escape bailout. Larger than a single-fold chapter wants, to allow the
    /// inter-formula excursions the composed orbit relies on.</summary>
    public float Bailout { get; init; } = 30.0f;

    /// <summary>DE step fudge. dr already over-estimates (~1.7x) so 1.0 is safe;
    /// can be pushed above 1 to trade safety margin for speed.</summary>
    public float Fudge { get; init; } = 1.0f;
    public float BoundRadius { get; init; } = 4.0f;
}

/// <summary>
/// GPU raymarcher for the Orbit Hybrid prototype. Owns only its compute shader;
/// shared buffers and the tile/AA loop live in RaymarchPipeline. Because both
/// formulas' parameters fit inside the shared FoldParams slots (reinterpreted by
/// orbithybrid_core.glsl), this reuses the standard pipeline path unchanged --
/// no new buffer or pipeline was needed for the prototype.
/// </summary>
public sealed class GpuOrbitHybridRenderer : IDisposable
{
    private readonly RaymarchPipeline _pipeline;
    private readonly ComputeShader _shader;
    private bool _disposed;

    public GpuOrbitHybridRenderer(Gl gl, RaymarchPipeline pipeline)
    {
        _pipeline = pipeline;
        var src = ShaderLoader.LoadComposite("orbithybrid_core.glsl", "raymarch_main.glsl");
        _shader = ComputeShader.FromSource(gl, src, "orbithybrid");
    }

    public uint[] RenderToBuffer(
        OrbitHybridParams oh,
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

        // Pack into the shared FoldParams slots (see orbithybrid_core.glsl):
        //   ints: iterations, kifsCount (mode), mboxCount (juliaMode)
        //   boxParams  = (kifsScale, mboxScale, minRadius, fixedRadius)
        //   surfParams = (postRot.xyz, bailout)
        //   juliaC     = (boxFoldLimit, _, _, _)
        var foldParams = new FoldParamsGpu
        {
            Iterations = oh.Iterations,
            Mode = oh.KifsCount,
            JuliaMode = oh.MboxCount,
            Pad0 = 0,
            BoxParams = new Vector4(oh.KifsScale, oh.MboxScale, oh.MinRadius, oh.FixedRadius),
            SurfParams = new Vector4(oh.PostRot.X, oh.PostRot.Y, oh.PostRot.Z, oh.Bailout),
            JuliaCVec = new Vector4(oh.BoxFoldLimit, 0f, 0f, 0f),
            Rot = new Vector4(0f, 0f, 0f, oh.Fudge),
            BoundSphere = new Vector4(0, 0, 0, oh.BoundRadius),
        };
        return _pipeline.Render(_shader, foldParams, camera, width, height, settings,
                                background, surface, lightDirection, palette,
                                heroSamples: settings.HeroSamples,
                                tileRows: tileRows, progress: progress);
    }

    public SKBitmap Render(
        OrbitHybridParams oh, Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface,
        Vector3 lightDirection, PaletteParams palette,
        int tileRows = 32, Action<int, int>? progress = null)
    {
        uint[] pixels = RenderToBuffer(oh, camera, width, height, settings,
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
        if (_disposed) throw new ObjectDisposedException(nameof(GpuOrbitHybridRenderer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
    }
}
