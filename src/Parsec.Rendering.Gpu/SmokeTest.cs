
namespace Parsec.Rendering.Gpu;

/// <summary>
/// Phase-1 smoke test: spin up a headless GL context, compile a trivial
/// compute shader, dispatch it over a buffer, read the result back, and
/// check that the values match the expected pattern.
/// </summary>
/// <remarks>
/// This validates the entire GPU plumbing path before we write any DE code.
/// If this passes, we know the context, the shader compiler, the SSBO upload/
/// download, and the dispatch all work. If it fails, the failure mode tells
/// us exactly which piece is broken.
/// </remarks>
public static class SmokeTest
{
    public static void Run()
    {
        Console.WriteLine("Parsec GPU smoke test");
        Console.WriteLine("=====================");

        using var ctx = new HeadlessGLContext();
        Console.WriteLine(ctx.Info());
        Console.WriteLine();

        // Compile the smoke shader.
        var src = ShaderLoader.Load("smoke.comp");
        using var shader = ComputeShader.FromSource(ctx.Gl, src, "smoke");
        Console.WriteLine("Compiled smoke.comp");

        // Allocate output buffer of 1024 uints.
        const int N = 1024;
        using var buffer = new StorageBuffer<uint>(ctx.Gl);
        buffer.Allocate(N);
        buffer.BindBase(0);
        Console.WriteLine($"Allocated SSBO of {N} uints, bound to binding=0");

        // Dispatch. Local size is 64, so we need ceil(N / 64) = 16 workgroups.
        int groups = (N + 63) / 64;
        shader.Use();
        shader.Dispatch(groups);
        // BufferUpdate covers the glGetBufferSubData readback below.
        ctx.Gl.MemoryBarrier(GlConst.BufferUpdateBarrierBit);
        Console.WriteLine($"Dispatched {groups} workgroups");

        // Read back and verify.
        uint[] result = buffer.Download();

        int errors = 0;
        for (int i = 0; i < N; i++)
        {
            uint expected = (uint)i * 2 + 1;
            if (result[i] != expected)
            {
                if (errors < 5)
                    Console.WriteLine($"  MISMATCH at index {i}: got {result[i]}, expected {expected}");
                errors++;
            }
        }

        if (errors == 0)
        {
            Console.WriteLine($"All {N} values match expected pattern. GPU plumbing works.");
            Console.WriteLine("First 8 values:");
            for (int i = 0; i < 8; i++)
                Console.WriteLine($"  result[{i}] = {result[i]}");
        }
        else
        {
            Console.WriteLine($"FAILED: {errors} mismatches out of {N}.");
        }
    }
}
