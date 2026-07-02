using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Core.Ifs;
using Parsec.Rendering;
using Parsec.Rendering.Raymarching;
using SkiaSharp;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// GPU raymarcher for affine IFS attractors: sphere-traces the Hart-style
/// branch-and-bound distance estimator in de_core.glsl. Follows the same
/// pattern as the other Gpu*Renderers -- owns only its compute shader plus the
/// IFS/Query SSBOs; the shared buffers and the tile/AA loop live in
/// <see cref="RaymarchPipeline"/>.
///
/// The IFS data is constant for the renderer's lifetime, so it is packed and
/// uploaded once in the constructor (bindings 0 and 9; see de_core.glsl for
/// why 9) and only re-bound per render call.
/// </summary>
public sealed class GpuRaymarchingRenderer : IDisposable
{
    private readonly RaymarchPipeline _pipeline;
    private readonly ComputeShader _shader;
    private readonly StorageBuffer<IFSMapGpu> _ifsBuffer;
    private readonly StorageBuffer<QueryParams> _queryBuffer;
    private bool _disposed;

    public GpuRaymarchingRenderer(Gl gl, RaymarchPipeline pipeline, IFS3D ifs,
        int maxDepth = 10, float detailEpsilon = 1e-2f)
    {
        if (ifs.Nodes.IsDefaultOrEmpty)
            throw new ArgumentException("IFS must have at least one node", nameof(ifs));
        if (ifs.Nodes.Length > 64)
            throw new ArgumentException("GPU renderer supports up to 64 maps.", nameof(ifs));
        if (maxDepth < 1 || maxDepth > 13)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "MaxDepth must be in [1, 13].");

        _pipeline = pipeline;
        var src = ShaderLoader.LoadComposite("de_core.glsl", "raymarch_main.glsl");
        _shader = ComputeShader.FromSource(gl, src, "ifs_raymarch");

        // Pack and upload the IFS data once; it is immutable per renderer.
        int numMaps = ifs.Nodes.Length;
        var gpuMaps = new IFSMapGpu[numMaps];
        for (int i = 0; i < numMaps; i++)
        {
            var t = ifs.Nodes[i].Transform;
            gpuMaps[i] = new IFSMapGpu
            {
                Row0 = new Vector4(t.M00, t.M01, t.M02, t.Tx),
                Row1 = new Vector4(t.M10, t.M11, t.M12, t.Ty),
                Row2 = new Vector4(t.M20, t.M21, t.M22, t.Tz),
                SigmaPad = new Vector4(t.SpectralNorm, 0, 0, 0),
            };
        }
        _ifsBuffer = new StorageBuffer<IFSMapGpu>(gl);
        _ifsBuffer.Upload(gpuMaps);

        var sphere = ifs.ComputeBoundingSphere();
        _queryBuffer = new StorageBuffer<QueryParams>(gl);
        _queryBuffer.Upload(new[] { new QueryParams
        {
            PointCount = 0,
            NumMaps = numMaps,
            MaxDepth = maxDepth,
            Pad0 = 0,
            AttractorSphere = new Vector4(sphere.Center, sphere.Radius),
            DetailEps = new Vector4(detailEpsilon, 0, 0, 0),
        }});
    }

    public uint[] RenderToBuffer(
        Camera3D camera,
        int width, int height,
        RaymarchSettings settings,
        Color background, Color surface,
        Vector3 lightDirection,
        PaletteParams palette,
        int tileRows = 64,
        Action<int, int>? progress = null)
    {
        ThrowIfDisposed();
        // The DE reads these alongside the pipeline's own bindings (1/4/5).
        _ifsBuffer.BindBase(0);
        _queryBuffer.BindBase(9);
        return _pipeline.Render(_shader, default, camera, width, height, settings,
                                background, surface, lightDirection, palette,
                                heroSamples: settings.HeroSamples,
                                tileRows: tileRows, progress: progress);
    }

    public SKBitmap Render(
        Camera3D camera, int width, int height,
        RaymarchSettings settings, Color background, Color surface,
        Vector3 lightDirection, PaletteParams palette,
        int tileRows = 64, Action<int, int>? progress = null)
    {
        uint[] pixels = RenderToBuffer(camera, width, height, settings,
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
        if (_disposed) throw new ObjectDisposedException(nameof(GpuRaymarchingRenderer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _ifsBuffer.Dispose();
        _queryBuffer.Dispose();
    }

    // -- GPU struct layouts (must match std430 in de_core.glsl) --

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct IFSMapGpu
    {
        public Vector4 Row0, Row1, Row2, SigmaPad;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct QueryParams
    {
        public int PointCount, NumMaps, MaxDepth, Pad0;
        public Vector4 AttractorSphere;
        public Vector4 DetailEps;
    }
}
