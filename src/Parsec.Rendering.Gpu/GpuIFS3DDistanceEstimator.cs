using System.Numerics;
using System.Runtime.InteropServices;
using Parsec.Core.Geometry;
using Parsec.Core.Ifs;
using Parsec.Core.Transforms;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// GPU implementation of the IFS distance estimator. Holds a compiled shader
/// and uploaded IFS data; each call to <see cref="Estimate"/> dispatches the
/// shader over a batch of query points and returns their estimated distances.
/// </summary>
/// <remarks>
/// This is the GPU equivalent of
/// <c>Parsec.Rendering.Raymarching.IFS3DDistanceEstimator</c> — same
/// algorithm, same correctness guarantees, just enormously faster for large
/// query batches.
/// </remarks>
public sealed class GpuIFS3DDistanceEstimator : IDisposable
{
    // -- GPU resources --
    private readonly Gl _gl;
    private readonly ComputeShader _shader;
    private readonly StorageBuffer<IFSMapGpu> _ifsBuffer;
    private readonly StorageBuffer<QueryParams> _paramBuffer;
    private readonly StorageBuffer<Vector4> _pointsBuffer;
    private readonly StorageBuffer<float> _resultBuffer;

    // -- CPU-side cached values --
    private readonly int _numMaps;
    private readonly int _maxDepth;
    private readonly float _detailEpsilon;
    private readonly BoundingSphere _attractorSphere;

    private bool _disposed;

    /// <summary>
    /// Build the GPU DE. The headless GL context must already be current on
    /// this thread.
    /// </summary>
    public GpuIFS3DDistanceEstimator(Gl gl, IFS3D ifs, int maxDepth = 10, float detailEpsilon = 1e-2f)
    {
        _gl = gl;
        if (ifs.Nodes.IsDefaultOrEmpty)
            throw new ArgumentException("IFS must have at least one node", nameof(ifs));
        if (ifs.Nodes.Length > 64)
            throw new ArgumentException(
                $"GPU DE currently supports up to 64 maps (got {ifs.Nodes.Length}). " +
                "Widen MAX_MAPS in the shader if you need more.", nameof(ifs));
        if (maxDepth < 1 || maxDepth > 13)
            throw new ArgumentOutOfRangeException(nameof(maxDepth),
                "MaxDepth must be in [1, 13]; the shader's MAX_STACK is 14.");

        _numMaps = ifs.Nodes.Length;
        _maxDepth = maxDepth;
        _detailEpsilon = detailEpsilon;
        _attractorSphere = ifs.ComputeBoundingSphere();

        // Compile shader from the shared DE core + validation entry point.
        var src = ShaderLoader.LoadComposite("de_core.glsl", "de_validate_main.glsl");
        _shader = ComputeShader.FromSource(_gl, src, "ifs_de_validate");

        // Pack IFS data into the GPU struct layout and upload.
        var gpuMaps = new IFSMapGpu[_numMaps];
        for (int i = 0; i < _numMaps; i++)
        {
            var t = ifs.Nodes[i].Transform;
            gpuMaps[i] = new IFSMapGpu
            {
                Row0    = new Vector4(t.M00, t.M01, t.M02, t.Tx),
                Row1    = new Vector4(t.M10, t.M11, t.M12, t.Ty),
                Row2    = new Vector4(t.M20, t.M21, t.M22, t.Tz),
                SigmaPad = new Vector4(t.SpectralNorm, 0f, 0f, 0f),
            };
        }
        _ifsBuffer = new StorageBuffer<IFSMapGpu>(_gl);
        _ifsBuffer.Upload(gpuMaps);

        _paramBuffer = new StorageBuffer<QueryParams>(_gl);
        _pointsBuffer = new StorageBuffer<Vector4>(_gl);
        _resultBuffer = new StorageBuffer<float>(_gl);
    }

    public BoundingSphere AttractorBoundingSphere => _attractorSphere;

    /// <summary>
    /// Compute the DE for each input point, returning the estimated distances
    /// in the same order. Synchronous: dispatches, fences, reads back.
    /// </summary>
    public float[] Estimate(ReadOnlySpan<Vector3> points)
    {
        ThrowIfDisposed();
        int n = points.Length;
        if (n == 0) return Array.Empty<float>();

        // Pack input points as Vector4 (the w is ignored but pads to 16 bytes
        // for std430 alignment).
        var packed = new Vector4[n];
        for (int i = 0; i < n; i++) packed[i] = new Vector4(points[i], 0f);
        _pointsBuffer.Upload(packed);

        // Upload query parameters.
        var qp = new QueryParams
        {
            PointCount       = n,
            NumMaps          = _numMaps,
            MaxDepth         = _maxDepth,
            _Pad0            = 0,
            AttractorSphere  = new Vector4(_attractorSphere.Center, _attractorSphere.Radius),
            DetailEps        = new Vector4(_detailEpsilon, 0f, 0f, 0f),
        };
        _paramBuffer.Upload(new[] { qp });

        // Allocate output buffer.
        _resultBuffer.Allocate(n);

        // Bind everything to the binding points the shader expects. Query lives
        // at 9 (not 1) so the raymarch composite of de_core can coexist with
        // RaymarchPipeline's FoldParams at binding 1.
        _ifsBuffer.BindBase(0);
        _paramBuffer.BindBase(9);
        _pointsBuffer.BindBase(2);
        _resultBuffer.BindBase(3);

        // Dispatch — local size is 64, so ceil(n / 64) workgroups.
        _shader.Use();
        int groups = (n + 63) / 64;
        _shader.Dispatch(groups);

        // Fence before readback to ensure the writes are visible. BufferUpdate
        // is the bit that covers glGetBufferSubData; ShaderStorage alone only
        // orders shader-to-shader access.
        _gl.MemoryBarrier(GlConst.BufferUpdateBarrierBit);

        return _resultBuffer.Download();
    }

    /// <summary>
    /// Single-point convenience overload.
    /// </summary>
    public float Estimate(Vector3 point)
    {
        Span<Vector3> single = stackalloc Vector3[] { point };
        return Estimate(single)[0];
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GpuIFS3DDistanceEstimator));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _ifsBuffer.Dispose();
        _paramBuffer.Dispose();
        _pointsBuffer.Dispose();
        _resultBuffer.Dispose();
    }

    // -- GPU struct layouts. These MUST match the std430 layout in ifs_de.comp --

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct IFSMapGpu
    {
        public Vector4 Row0;        // 16 bytes
        public Vector4 Row1;        // 16 bytes
        public Vector4 Row2;        // 16 bytes
        public Vector4 SigmaPad;    // 16 bytes — only X used
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct QueryParams
    {
        public int     PointCount;
        public int     NumMaps;
        public int     MaxDepth;
        public int     _Pad0;
        public Vector4 AttractorSphere;
        public Vector4 DetailEps;
    }
}
