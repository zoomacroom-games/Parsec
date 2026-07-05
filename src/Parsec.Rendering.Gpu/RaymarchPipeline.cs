using System.Numerics;
using Parsec.Rendering;
using Parsec.Rendering.Gpu;

namespace Parsec.Rendering.Raymarching;

/// <summary>
/// Shared raymarch pipeline. Owns the SSBOs and shaders that every Gpu*Renderer
/// needs identically -- the accumulator buffer, the packed-pixel output buffer,
/// the fold-params and render-params buffers, plus the small clear/finalize
/// compute shaders that drive super-sampling.
///
/// Lifecycle: one instance per GL context. FractalView constructs the pipeline
/// in OnOpenGlInit and passes it to every renderer's constructor; renderers
/// own only their per-fractal compute shader.
///
/// Render flow per call:
///   1. Allocate / resize buffers for this image size.
///   2. Clear the vec4 accumulator buffer to zero (one dispatch).
///   3. For each AA sample (1 for preview, N for hero):
///        Compute Halton-jittered sub-pixel offset.
///        For each tile:
///          Upload RenderParams (with jitter), dispatch fractalShader.
///          fractalShader reads RenderParams + FoldParams, writes accumulated
///          vec4 colors into the accumulator buffer.
///   4. Finalize: divide accumulator by sampleCount, clamp, sRGB-encode, pack
///      to RGBA8 (one dispatch). The accumulator holds linear light; encoding
///      here (once, after averaging) keeps SSAA averaging linear-correct.
///   5. Download and return the uint[] image.
///
/// SSAA = "supersampling antialiasing." sampleCount=1 reproduces the old single-
/// ray-per-pixel behavior exactly; sampleCount=4/9/16 fires N jittered rays per
/// pixel and averages them, giving smooth edges and resolving fine fractal detail
/// that aliases into noise at 1x.
///
/// SSBO binding map (chosen to avoid the attractor core's 6/7/8):
///   1 = FoldParams (per-fractal core)      4 = RenderParams (raymarch_main)
///   2 = packed RGBA8 output (finalize)     5 = vec4 accumulator (raymarch_main)
///   3 = AAParams (clear + finalize)        6/7/8 = attractor trajectory/hash/idx
///   0/9 = affine-IFS maps + query (de_core, bound by GpuRaymarchingRenderer)
///   10 = orb lights (raymarch_main; count rides RenderParams.MarchB.z)
/// </summary>
public sealed class RaymarchPipeline : IDisposable
{
    /// <summary>Maximum placeable orb lights per scene (matches the fixed loop
    /// bound the shader uses and the slot count uploaded every render).</summary>
    public const int MaxOrbs = 8;

    private readonly Gl _gl;
    private readonly StorageBuffer<FoldParamsGpu> _foldBuffer;
    private readonly StorageBuffer<RenderParamsGpu> _renderBuffer;
    private readonly StorageBuffer<Vector4> _accumBuffer;
    private readonly StorageBuffer<uint> _imageBuffer;
    private readonly StorageBuffer<AAParamsGpu> _aaParamsBuffer;
    private readonly StorageBuffer<OrbGpu> _orbBuffer;
    private readonly OrbGpu[] _orbData = new OrbGpu[MaxOrbs];
    private int _orbCount;
    private readonly ComputeShader _clearShader;
    private readonly ComputeShader _finalizeShader;
    private bool _disposed;

    public RaymarchPipeline(Gl gl)
    {
        _gl = gl;
        _foldBuffer = new StorageBuffer<FoldParamsGpu>(gl);
        _renderBuffer = new StorageBuffer<RenderParamsGpu>(gl);
        _accumBuffer = new StorageBuffer<Vector4>(gl);
        _imageBuffer = new StorageBuffer<uint>(gl);
        _aaParamsBuffer = new StorageBuffer<AAParamsGpu>(gl);
        _orbBuffer = new StorageBuffer<OrbGpu>(gl);
        _clearShader = ComputeShader.FromSource(gl, ClearShaderSource, "aa_clear");
        _finalizeShader = ComputeShader.FromSource(gl, FinalizeShaderSource, "aa_finalize");
    }

    /// <summary>
    /// Set the placeable orb lights for subsequent renders. Pass null or an
    /// empty list to clear. Orbs beyond <see cref="MaxOrbs"/> are ignored.
    /// State persists across Render calls until changed, so the host sets it
    /// once per frame (or never, for orb-less callers like the CLI presets).
    /// </summary>
    public void SetOrbs(IReadOnlyList<OrbLight>? orbs)
    {
        ThrowIfDisposed();
        _orbCount = Math.Min(orbs?.Count ?? 0, MaxOrbs);
        for (int i = 0; i < MaxOrbs; i++)
        {
            if (orbs != null && i < _orbCount)
            {
                var o = orbs[i];
                _orbData[i] = new OrbGpu
                {
                    PosRad = new Vector4(o.Position, MathF.Max(0f, o.Radius)),
                    ColorLum = new Vector4(
                        Vector3.Clamp(o.Color, Vector3.Zero, Vector3.One),
                        MathF.Max(0f, o.Luminosity)),
                };
            }
            else
            {
                _orbData[i] = default;
            }
        }
    }

    /// <summary>
    /// Run the fractal shader N times (N = heroSamples) with Halton-jittered
    /// sub-pixel offsets, accumulating into a vec4 buffer, then finalize to
    /// packed RGBA8 and download. The fractal shader is whatever
    /// ComputeShader the calling Gpu*Renderer built from its *_core.glsl +
    /// raymarch_main.glsl composite source.
    /// </summary>
    internal uint[] Render(
        ComputeShader fractalShader,
        FoldParamsGpu foldParams,
        Camera3D camera,
        int width, int height,
        RaymarchSettings settings,
        Color background, Color surface,
        Vector3 lightDirection,
        PaletteParams palette,
        int heroSamples,
        int tileRows,
        Action<int, int>? progress)
    {
        ThrowIfDisposed();
        int samples = Math.Max(1, heroSamples);
        int pixelCount = width * height;

        // Progressive accumulation: SampleOffset > 0 means the accumulator
        // already holds that many samples of this exact scene at this exact
        // resolution, so skip the clear and continue the sample sequence. A
        // (re)allocation invalidates the old contents, so it forces a fresh
        // start regardless of what the caller asked for.
        int sampleOffset = Math.Max(0, settings.SampleOffset);
        bool reallocated = _accumBuffer.Allocate(pixelCount);
        _imageBuffer.Allocate(pixelCount);
        if (reallocated) sampleOffset = 0;

        // Upload params that are constant across the whole render.
        _foldBuffer.Upload(new[] { foldParams });
        _aaParamsBuffer.Upload(new[] { new AAParamsGpu {
            Width = width, Height = height,
            SampleCount = sampleOffset + samples, Pad0 = 0,
        }});

        // Clear the accumulator (only when starting fresh).
        if (sampleOffset == 0)
        {
            _accumBuffer.BindBase(5);
            _aaParamsBuffer.BindBase(3);
            _clearShader.Use();
            _clearShader.Dispatch((width + 7) / 8, (height + 7) / 8);
            _gl.MemoryBarrier(GlConst.ShaderStorageBarrierBit);
        }

        // Pre-build the camera frame and lighting; these don't change between
        // AA samples (only the sub-pixel jitter does).
        var frame = CameraFrame.Build(camera, width, height);
        var lightDir = Vector3.Normalize(lightDirection);
        int flags = (settings.EnableSoftShadows ? 1 : 0)
                  | (settings.EnableAmbientOcclusion ? 2 : 0);

        int tiles = (height + tileRows - 1) / tileRows;
        int totalUnits = samples * tiles;
        int unitsDone = 0;

        // Main render: N AA samples * T tiles.
        _foldBuffer.BindBase(1);
        _renderBuffer.BindBase(4);
        _accumBuffer.BindBase(5);
        // Orb lights (binding 10): all MaxOrbs slots uploaded every render
        // (tiny); the active count rides RenderParams.MarchB.z.
        _orbBuffer.Upload(_orbData);
        _orbBuffer.BindBase(10);
        fractalShader.Use();

        // Jitter/lens indices continue the global sequence across progressive
        // calls. A single-sample fresh render stays a centered pinhole (one
        // jittered/lens-offset sample alone would shift the image, not
        // antialias or blur it); once accumulation is in play every sample
        // draws from the sequence.
        bool multiSample = sampleOffset + samples > 1;
        for (int sample = 0; sample < samples; sample++)
        {
            var jitter = multiSample ? HaltonJitter(sampleOffset + sample) : Vector2.Zero;
            // Lens sample for thin-lens DOF: a unit-disc point per AA sample,
            // averaged by the same accumulation as the subpixel jitter.
            var lens = multiSample ? LensSample(sampleOffset + sample) : Vector2.Zero;

            for (int tile = 0; tile < tiles; tile++)
            {
                int rowOffset = tile * tileRows;
                int rowCount = Math.Min(tileRows, height - rowOffset);

                _renderBuffer.Upload(new[] { BuildRenderParams(
                    width, height, rowOffset, rowCount, frame, camera,
                    lightDir, background, surface, settings, palette, flags, jitter,
                    lens, _orbCount) });
                _renderBuffer.BindBase(4);

                int groupsX = (width + 7) / 8;
                int groupsY = (rowCount + 7) / 8;
                fractalShader.Dispatch(groupsX, groupsY);
                _gl.MemoryBarrier(GlConst.ShaderStorageBarrierBit);
                _gl.Finish();

                unitsDone++;
                progress?.Invoke(unitsDone, totalUnits);
            }
        }

        // Finalize: divide accumulator by sampleCount, pack to RGBA8.
        _accumBuffer.BindBase(5);
        _imageBuffer.BindBase(2);
        _aaParamsBuffer.BindBase(3);
        _finalizeShader.Use();
        _finalizeShader.Dispatch((width + 7) / 8, (height + 7) / 8);
        _gl.MemoryBarrier(GlConst.ShaderStorageBarrierBit);
        _gl.Finish();

        return _imageBuffer.Download();
    }

    private static RenderParamsGpu BuildRenderParams(
        int width, int height, int rowOffset, int rowCount,
        CameraFrame frame, Camera3D camera, Vector3 lightDir,
        Color background, Color surface, RaymarchSettings s,
        PaletteParams palette, int flags, Vector2 jitter, Vector2 lens,
        int orbCount)
    {
        return new RenderParamsGpu
        {
            ImageWidth = width, ImageHeight = height,
            RowOffset = rowOffset, RowCount = rowCount,
            CamPos = new Vector4(camera.Position, 0),
            CamForward = new Vector4(frame.Forward, 0),
            CamRight = new Vector4(frame.Right, 0),
            CamUp = new Vector4(frame.Up, 0),
            // zw = (focus distance, aperture radius) for thin-lens DOF.
            TanFov = new Vector4(frame.TanFovX, frame.TanFovY,
                                 s.FocusDistance, s.Aperture),
            LightDir = new Vector4(lightDir, s.LightIntensity),
            // The shader shades in linear light and the finalize pass sRGB-encodes,
            // so the authored background must be decoded here to survive the
            // round-trip unchanged on miss pixels.
            Background = new Vector4(
                SrgbToLinear(background.R), SrgbToLinear(background.G),
                SrgbToLinear(background.B), 1),
            Surface = new Vector4(surface.R, surface.G, surface.B, 1),
            MarchA = new Vector4(s.HitEpsilon, s.MaxDistance, s.NormalEpsilon, s.ShadowSoftness),
            // MarchB.z carries the active orb-light count (spare slot; the orb
            // data itself lives in the binding-10 SSBO).
            MarchB = new Vector4(s.AOStepDistance, s.AOIntensity, orbCount, 0),
            MarchI0 = s.MaxSteps, MarchI1 = s.ShadowSteps,
            MarchI2 = s.AOSamples, MarchI3 = flags,
            PalBase = new Vector4(palette.Base, palette.Frequency),
            PalAmp = new Vector4(palette.Amp, palette.TrapScale),
            PalPhase = new Vector4(palette.Phase, palette.ShellMix),
            TrapMix = new Vector4(palette.TrapMix, 0f),
            // zw = this sample's unit-disc lens point (thin-lens DOF).
            SubpixelJitter = new Vector4(jitter.X, jitter.Y, lens.X, lens.Y),
            ReflectParams = new Vector4(
                s.EnableReflections ? 1f : 0f,
                s.ReflectionBounces,
                s.Gloss,
                s.F0),
        };
    }

    /// <summary>sRGB electro-optical transfer (decode): display value -> linear
    /// light. Inverse of the encode in the finalize shader.</summary>
    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>Uniform unit-disc lens point for AA sample index, via polar
    /// mapping of Halton(5,7) — bases disjoint from the subpixel jitter's
    /// (2,3) so lens and pixel positions don't correlate.</summary>
    private static Vector2 LensSample(int sampleIndex)
    {
        int n = sampleIndex + 1;
        float r = MathF.Sqrt(Halton(n, 5));
        float theta = 2f * MathF.PI * Halton(n, 7);
        return new Vector2(r * MathF.Cos(theta), r * MathF.Sin(theta));
    }

    /// <summary>Halton(2,3) sub-pixel jitter for sample index. Returns offset
    /// in [-0.5, 0.5] x [-0.5, 0.5]. Low-discrepancy quasi-random; produces
    /// better perceptual quality at low sample counts than uniform random.</summary>
    private static Vector2 HaltonJitter(int sampleIndex)
    {
        int n = sampleIndex + 1;  // Halton(0) is degenerate; use 1-based
        return new Vector2(Halton(n, 2) - 0.5f, Halton(n, 3) - 0.5f);
    }

    private static float Halton(int index, int b)
    {
        float f = 1f;
        float result = 0f;
        while (index > 0)
        {
            f /= b;
            result += f * (index % b);
            index /= b;
        }
        return result;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RaymarchPipeline));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clearShader.Dispose();
        _finalizeShader.Dispose();
        _foldBuffer.Dispose();
        _renderBuffer.Dispose();
        _accumBuffer.Dispose();
        _imageBuffer.Dispose();
        _aaParamsBuffer.Dispose();
        _orbBuffer.Dispose();
    }

    // ------------------------------------------------------------------------
    // Embedded helper shaders (clear + finalize). Self-contained so the
    // pipeline doesn't need extra embedded resources.
    // ------------------------------------------------------------------------

    private const string ClearShaderSource = @"
#version 430 core
layout(local_size_x = 8, local_size_y = 8) in;

layout(std430, binding = 5) buffer Accum {
    vec4 colors[];
} accum;

layout(std430, binding = 3) readonly buffer AAParams {
    int width;
    int height;
    int sampleCount;
    int pad0;
} aa;

void main() {
    int px = int(gl_GlobalInvocationID.x);
    int py = int(gl_GlobalInvocationID.y);
    if (px >= aa.width || py >= aa.height) return;
    accum.colors[py * aa.width + px] = vec4(0.0);
}
";

    private const string FinalizeShaderSource = @"
#version 430 core
layout(local_size_x = 8, local_size_y = 8) in;

layout(std430, binding = 5) readonly buffer Accum {
    vec4 colors[];
} accum;

layout(std430, binding = 2) writeonly buffer Output {
    uint pixels[];
} img;

layout(std430, binding = 3) readonly buffer AAParams {
    int width;
    int height;
    int sampleCount;
    int pad0;
} aa;

// Linear light -> sRGB display encoding. The accumulator is linear (shading
// happens there so SSAA averaging is physically correct); packing without
// this encode crushes midtones and the whole render reads dark.
vec3 linearToSrgb(vec3 c) {
    vec3 lo = c * 12.92;
    vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
    return mix(lo, hi, step(vec3(0.0031308), c));
}

void main() {
    int px = int(gl_GlobalInvocationID.x);
    int py = int(gl_GlobalInvocationID.y);
    if (px >= aa.width || py >= aa.height) return;
    int idx = py * aa.width + px;
    vec4 c = accum.colors[idx] / float(aa.sampleCount);
    vec3 srgb = linearToSrgb(clamp(c.rgb, 0.0, 1.0));
    uvec3 q = uvec3(srgb * 255.0 + 0.5);
    img.pixels[idx] = (255u << 24) | (q.b << 16) | (q.g << 8) | q.r;
}
";
}
