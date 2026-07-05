using System.Runtime.InteropServices;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// A typed Shader Storage Buffer Object (SSBO). Allocated once, uploaded
/// once (or rarely), bound to a binding point for shader access. All GL calls
/// go through an injected <see cref="Gl"/> instance (the unified GetProcAddress
/// layer).
/// </summary>
/// <typeparam name="T">
/// The element type. Must be a blittable struct or primitive. The layout in
/// the shader must use <c>std430</c> and match this struct's memory layout.
/// </typeparam>
public sealed class StorageBuffer<T> : IDisposable where T : unmanaged
{
    private readonly Gl _gl;
    public uint Handle { get; }
    public int ElementCount { get; private set; }
    public int ByteCount => ElementCount * Marshal.SizeOf<T>();
    private bool _disposed;

    public StorageBuffer(Gl gl)
    {
        _gl = gl;
        Handle = gl.GenBuffer();
    }

    /// <summary>
    /// Upload <paramref name="data"/> as the buffer's complete contents
    /// (replacing any previous contents). Uses STATIC_DRAW hint — appropriate
    /// for upload-once-read-many.
    /// </summary>
    public void Upload(ReadOnlySpan<T> data)
    {
        ThrowIfDisposed();
        ElementCount = data.Length;
        _gl.BindBuffer(GlConst.ShaderStorageBuffer, Handle);
        unsafe
        {
            fixed (T* ptr = data)
            {
                _gl.BufferData(GlConst.ShaderStorageBuffer,
                    (IntPtr)(data.Length * Marshal.SizeOf<T>()),
                    (IntPtr)ptr,
                    GlConst.StaticDraw);
            }
        }
        _gl.BindBuffer(GlConst.ShaderStorageBuffer, 0);
    }

    /// <summary>
    /// Allocate without initializing. At a NEW size the storage is recreated
    /// and its contents are undefined until something writes to them; at the
    /// SAME size the existing storage (and its contents) are kept, which lets
    /// callers accumulate into a buffer across calls. Returns true if the
    /// storage was (re)created, false if the existing allocation was reused.
    /// </summary>
    public bool Allocate(int elementCount, uint usage = 0)
    {
        ThrowIfDisposed();
        if (elementCount == ElementCount && elementCount > 0) return false;
        if (usage == 0) usage = GlConst.DynamicCopy;
        ElementCount = elementCount;
        _gl.BindBuffer(GlConst.ShaderStorageBuffer, Handle);
        _gl.BufferData(GlConst.ShaderStorageBuffer,
            (IntPtr)(elementCount * Marshal.SizeOf<T>()),
            IntPtr.Zero,
            usage);
        _gl.BindBuffer(GlConst.ShaderStorageBuffer, 0);
        return true;
    }

    /// <summary>
    /// Bind this buffer to <paramref name="bindingPoint"/> for shader access.
    /// Must match the <c>layout(std430, binding = N)</c> declaration in GLSL.
    /// </summary>
    public void BindBase(int bindingPoint)
    {
        ThrowIfDisposed();
        _gl.BindBufferBase(GlConst.ShaderStorageBuffer, (uint)bindingPoint, Handle);
    }

    /// <summary>
    /// Read the entire buffer back to CPU memory.
    /// </summary>
    public T[] Download()
    {
        ThrowIfDisposed();
        var result = new T[ElementCount];
        _gl.BindBuffer(GlConst.ShaderStorageBuffer, Handle);
        unsafe
        {
            fixed (T* ptr = result)
            {
                _gl.GetBufferSubData(GlConst.ShaderStorageBuffer,
                    IntPtr.Zero,
                    (IntPtr)(result.Length * Marshal.SizeOf<T>()),
                    (IntPtr)ptr);
            }
        }
        _gl.BindBuffer(GlConst.ShaderStorageBuffer, 0);
        return result;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StorageBuffer<T>));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gl.DeleteBuffer(Handle);
    }
}
