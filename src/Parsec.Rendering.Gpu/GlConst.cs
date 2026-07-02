namespace Parsec.Rendering.Gpu;

/// <summary>
/// The OpenGL enum constants Parsec's compute pipeline uses, as raw values.
/// Defining them here (rather than pulling a binding library's enums) keeps
/// the <see cref="Gl"/> layer self-contained and binding-agnostic — it talks
/// to whatever context is current via GetProcAddress, with no dependency on
/// OpenTK or Silk.NET enum types.
/// </summary>
public static class GlConst
{
    // Shader types
    public const uint ComputeShader = 0x91B9;

    // glGetShaderiv / glGetProgramiv pnames
    public const uint CompileStatus = 0x8B81;
    public const uint LinkStatus = 0x8B82;
    public const uint InfoLogLength = 0x8B84;

    // Buffer targets
    public const uint ShaderStorageBuffer = 0x90D2;

    // Buffer usage hints
    public const uint StaticDraw = 0x88E4;
    public const uint DynamicCopy = 0x88EA;

    // glGetString names
    public const uint Vendor = 0x1F00;
    public const uint Renderer = 0x1F01;
    public const uint Version = 0x1F02;
    public const uint ShadingLanguageVersion = 0x8B8C;

    // Memory barrier bits
    public const uint ShaderStorageBarrierBit = 0x00002000;
    // Required (GL 4.6 §7.13.2) for shader writes to be visible to
    // glGetBufferSubData readback; ShaderStorageBarrierBit only orders
    // shader-to-shader access.
    public const uint BufferUpdateBarrierBit = 0x00000200;
    public const uint AllBarrierBits = 0xFFFFFFFF;

    // ---- additional constants for the on-screen blit path ----

    // Shader types (graphics)
    public const uint VertexShader = 0x8B31;
    public const uint FragmentShader = 0x8B30;

    // Textures
    public const uint Texture2D = 0x0DE1;
    public const uint Texture0 = 0x84C0;
    public const uint Rgba8 = 0x8058;
    public const uint Rgba = 0x1908;
    public const uint UnsignedByte = 0x1401;
    public const uint TextureMinFilter = 0x2801;
    public const uint TextureMagFilter = 0x2800;
    public const uint TextureWrapS = 0x2802;
    public const uint TextureWrapT = 0x2803;
    public const uint Nearest = 0x2600;
    public const uint Linear = 0x2601;
    public const uint ClampToEdge = 0x812F;

    // Framebuffer / drawing
    public const uint Framebuffer = 0x8D40;
    public const uint ColorBufferBit = 0x00004000;
    public const uint Triangles = 0x0004;
    public const uint TriangleStrip = 0x0005;
}
