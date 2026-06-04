using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Rendering;
using Parsec.Rendering.Raymarching;
using SkiaSharp;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// Parameters for the 3D Burning Ship: the triplex z -> z^n + c power map (as in
/// the Mandelbulb) in a y-up angular convention, with an abs() fold applied to
/// every component after the +c. Power is the headline morphology knob; the
/// abs() folds give the terraced, vertically-mirror-symmetric massing.
/// </summary>
public sealed record BurningShipParams
{
    public int Iterations { get; init; } = 16;
    public float Power { get; init; } = 2.0f;
    public float Bailout { get; init; } = 2.0f;
    // Default below 1: the abs() folds make the scalar-dr DE more aggressive than
    // the plain Mandelbulb near the spherical-coordinate poles (validated), so we
    // shorten steps by default. Raise toward 1.0 for speed if the surface holds.
    public float Fudge { get; init; } = 0.75f;
    public float BoundRadius { get; init; } = 2.0f;  // low power reaches ~1.78 from origin
}

/// <summary>
/// GPU raymarcher for the 3D Burning Ship. Owns only its compute shader; shared
/// buffers and the tile/AA loop live in RaymarchPipeline. Slot map is identical
/// to the Mandelbulb's (power, bailout, fudge, bound sphere).
/// </summary>
public sealed class GpuBurningShipRenderer : IDisposable
{
    private readonly RaymarchPipeline _pipeline;
    private readonly ComputeShader _shader;
    private bool _disposed;

    public GpuBurningShipRenderer(Gl gl, RaymarchPipeline pipeline)
    {
        _pipeline = pipeline;
        var src = ShaderLoader.LoadComposite("burningship_core.glsl", "raymarch_main.glsl");
        _shader = ComputeShader.FromSource(gl, src, "burningship");
    }

    public uint[] RenderToBuffer(
        BurningShipParams bs,
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
        var foldParams = new FoldParamsGpu
        {
            Iterations = bs.Iterations, Mode = 0, JuliaMode = 0, Pad0 = 0,
            BoxParams = new Vector4(bs.Power, bs.Bailout, 0, 0),
            SurfParams = Vector4.Zero,
            JuliaCVec = Vector4.Zero,
            Rot = new Vector4(0, 0, 0, bs.Fudge),
            BoundSphere = new Vector4(0, 0, 0, bs.BoundRadius),
        };
        return _pipeline.Render(_shader, foldParams, camera, width, height, settings,
                                background, surface, lightDirection, palette,
                                heroSamples: settings.HeroSamples,
                                tileRows: tileRows, progress: progress);
    }

    public SKBitmap Render(
        BurningShipParams bs, Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface,
        Vector3 lightDirection, PaletteParams palette,
        int tileRows = 32, Action<int, int>? progress = null)
    {
        uint[] pixels = RenderToBuffer(bs, camera, width, height, settings,
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
        if (_disposed) throw new ObjectDisposedException(nameof(GpuBurningShipRenderer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
    }
}
