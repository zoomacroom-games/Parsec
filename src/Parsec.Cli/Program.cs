using Parsec.Cli.Examples;
using Parsec.Rendering.Output;

namespace Parsec.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var examples = new IExample[]
        {
            new DiamondExample(),
            new DiamondConstructionExample(),
            new CarpetExample(),
            new TriangleExample(),
            new Sanity3DExample(),
            new TetrahedronExample(),
            new TrefoilExample(),
            new TwistedTetrahedronExample(),
        };

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(examples);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] is "list" or "--list")
        {
            foreach (var ex in examples)
                Console.WriteLine($"  {ex.Name,-24} {ex.Description}");
            return 0;
        }

        if (args[0] is "all")
        {
            int failures = 0;
            foreach (var ex in examples)
                if (!RunExample(ex)) failures++;
            return failures == 0 ? 0 : 1;
        }

        if (args[0] is "gpu-smoke")
        {
            try
            {
                Parsec.Rendering.Gpu.SmokeTest.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GPU smoke test FAILED: {ex.Message}");
                if (ex.StackTrace is not null)
                    Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        if (args[0] is "gpu-de-validate")
        {
            try
            {
                return Parsec.Rendering.Gpu.DeValidation.Run();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GPU DE validation FAILED: {ex.Message}");
                if (ex.StackTrace is not null)
                    Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        if (args[0] is "attractor-stats")
        {
            // Stage A check: integrate the Thomas attractor (canonical by default,
            // or enhanced with flags) and print point count + bounds, so the C#
            // integrator can be sanity-checked against the Python/Unity numbers.
            //   parsec attractor-stats [steps] [enhanced]
            try
            {
                int steps = args.Length > 1 ? int.Parse(args[1]) : 60_000;
                bool enhanced = args.Length > 2 && args[2] is "enhanced" or "true" or "1";

                var p = new Parsec.Core.Attractors.AttractorParams
                {
                    NumSteps = steps,
                    UseParameterDrift = enhanced,
                    UsePhaseModulation = enhanced,
                    UseNonlinearCoupling = enhanced,
                    UseMultiSeed = enhanced,
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pts = Parsec.Core.Attractors.ThomasAttractor.Generate(p);
                sw.Stop();

                var lo = new System.Numerics.Vector3(float.MaxValue);
                var hi = new System.Numerics.Vector3(float.MinValue);
                foreach (var tp in pts)
                {
                    lo = System.Numerics.Vector3.Min(lo, tp.Position);
                    hi = System.Numerics.Vector3.Max(hi, tp.Position);
                }
                Console.WriteLine($"Thomas attractor ({(enhanced ? "enhanced" : "canonical")}): "
                    + $"{pts.Count} points in {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"  bounds x[{lo.X:F2},{hi.X:F2}] y[{lo.Y:F2},{hi.Y:F2}] z[{lo.Z:F2},{hi.Z:F2}]");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"attractor-stats FAILED: {ex.Message}");
                return 1;
            }
        }

        if (args[0] is "gpu-progressive-validate")
        {
            // Validates the progressive-accumulation path (RaymarchSettings.
            // SampleOffset, used by the app's DOF preview refinement): one
            // 8-sample render must match the same scene accumulated as eight
            // 1-sample calls with offsets 0..7.
            try
            {
                using var ctx = new Parsec.Rendering.Gpu.HeadlessGLContext();
                Console.WriteLine("GPU progressive accumulation validation");
                Console.WriteLine(ctx.Info());
                return Parsec.Rendering.Gpu.GpuRenders.ValidateProgressiveAccumulation(ctx.Gl) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"gpu-progressive-validate FAILED: {ex.Message}");
                return 1;
            }
        }

        if (args[0] is "gpu-render")
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: parsec gpu-render <name> [width] [height]");
                Console.Error.WriteLine($"Available: {string.Join(", ", Parsec.Rendering.Gpu.GpuRenders.All.Select(s => s.Name))}");
                return 2;
            }
            try
            {
                string name = args[1];
                int width = args.Length > 2 ? int.Parse(args[2]) : 900;
                int height = args.Length > 3 ? int.Parse(args[3]) : width;

                using var ctx = new Parsec.Rendering.Gpu.HeadlessGLContext();
                Console.WriteLine($"GPU render '{name}' at {width}x{height}");
                Console.WriteLine(ctx.Info());

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var bmp = Parsec.Rendering.Gpu.GpuRenders.RenderByName(
                    ctx.Gl, name, width, height,
                    (tile, tiles) =>
                    {
                        Console.Write($"\r  tile {tile}/{tiles}   ");
                        if (tile == tiles) Console.WriteLine();
                    });
                sw.Stop();

                var outPath = ResolveOutputPath($"gpu-{name}.png");
                Parsec.Rendering.Output.ImageOutput.SavePng(bmp, outPath);
                Console.WriteLine($"  -> {outPath}  ({sw.ElapsedMilliseconds} ms)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GPU render FAILED: {ex.Message}");
                if (ex.StackTrace is not null) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        var matches = examples.Where(e => e.Name == args[0]).ToList();
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Unknown example: '{args[0]}'");
            Console.Error.WriteLine();
            PrintUsage(examples);
            return 2;
        }

        return RunExample(matches[0]) ? 0 : 1;
    }

    private static bool RunExample(IExample example)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var bitmap = example.Render();
            stopwatch.Stop();

            if (bitmap is null)
            {
                Console.WriteLine($"  {example.Name,-24} (no image)  ({stopwatch.ElapsedMilliseconds} ms)");
                return true;
            }

            var outputPath = ResolveOutputPath($"{example.Name}.png");
            ImageOutput.SavePng(bitmap, outputPath);

            Console.WriteLine($"  {example.Name,-24} -> {outputPath}  ({stopwatch.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {example.Name,-24} FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Output PNGs land next to the CLI executable, in an <c>outputs/</c> subdirectory.
    /// </summary>
    private static string ResolveOutputPath(string filename)
    {
        var exeDir = AppContext.BaseDirectory;
        var outputDir = Path.Combine(exeDir, "outputs");
        return Path.Combine(outputDir, filename);
    }

    private static void PrintUsage(IExample[] examples)
    {
        Console.WriteLine("Parsec CLI — IFS rendering spike");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  parsec <example>      Render a single example");
        Console.WriteLine("  parsec all            Render every example");
        Console.WriteLine("  parsec list           List available examples");
        Console.WriteLine("  parsec gpu-smoke      Phase-1 GPU plumbing test");
        Console.WriteLine("  parsec gpu-de-validate  Phase-2a GPU DE validation");
        Console.WriteLine("  parsec gpu-render <name> [w] [h]   GPU raymarch render");
        Console.WriteLine("  parsec help           Show this help");
        Console.WriteLine();
        Console.WriteLine("Available examples:");
        foreach (var ex in examples)
            Console.WriteLine($"  {ex.Name,-24} {ex.Description}");
        Console.WriteLine();
        Console.WriteLine("Output goes to <exe-dir>/outputs/<example>.png");
    }
}
