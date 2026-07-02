using System.Numerics;
using Parsec.Core.Ifs;
using Parsec.Rendering;
using Parsec.Rendering.Raymarching;
using SkiaSharp;

namespace Parsec.Rendering.Gpu;

/// <summary>
/// CLI-facing GPU render presets. Mirrors the CPU examples but runs on the
/// GPU via <see cref="GpuRaymarchingRenderer"/>.
/// </summary>
public static class GpuRenders
{
    public sealed record Spec(string Name, string Description);

    public static IReadOnlyList<Spec> All { get; } = new[]
    {
        new Spec("tetrahedron", "Sierpinski tetrahedron (GPU)"),
        new Spec("trefoil",     "Fractal trefoil knot (GPU)"),
        new Spec("twisted-tet", "Twisted Sierpinski tetrahedron (GPU)"),
        new Spec("mandelbox",   "Mandelbox (GPU)"),
        new Spec("amazingsurf", "AmazingSurf (GPU)"),
        new Spec("kifs",        "Kaleidoscopic IFS / Amazing IFS (GPU)"),
        new Spec("kleinian",    "Pseudo-Kleinian inversive limit set (GPU)"),
        new Spec("attractor",   "Thomas strange attractor tube (GPU)"),
        new Spec("mandelbulb",  "Mandelbulb (canonical power-8) (GPU)"),
        new Spec("qjulia",      "Quaternion Julia (half-cut) (GPU)"),
        new Spec("rotbox",      "Rotation-augmented Mandelbox (GPU)"),
        new Spec("hybrid",      "Rotated Mandelbox+Mandelbulb hybrid (GPU)"),
        new Spec("qjbox",       "Quaternion-Julia × Mandelbox hybrid (half-cut) (GPU)"),
        new Spec("menger",      "Rotated folded Menger-IFS (architectural) (GPU)"),
        new Spec("bicomplex",   "Bicomplex Julia (Fracmonk formula, half-cut) (GPU)"),
    };

    public static SKBitmap RenderByName(Gl gl, string name, int width, int height, Action<int, int>? progress)
    {
        _ = All.FirstOrDefault(s => s.Name == name)
            ?? throw new ArgumentException($"Unknown GPU render '{name}'. Available: {string.Join(", ", All.Select(s => s.Name))}");
        return name switch
        {
            "tetrahedron" => RenderTetrahedron(gl, width, height, progress),
            "trefoil"     => RenderTrefoil(gl, width, height, progress),
            "twisted-tet" => RenderTwistedTet(gl, width, height, progress),
            "mandelbox"   => RenderMandelbox(gl, width, height, progress),
            "amazingsurf" => RenderAmazingSurf(gl, width, height, progress),
            "kifs"        => RenderKifs(gl, width, height, progress),
            "kleinian"    => RenderKleinian(gl, width, height, progress),
            "attractor"   => RenderAttractor(gl, width, height, progress),
            "mandelbulb"  => RenderMandelbulb(gl, width, height, progress),
            "qjulia"      => RenderQuaternionJulia(gl, width, height, progress),
            "rotbox"      => RenderRotBox(gl, width, height, progress),
            "hybrid"      => RenderHybrid(gl, width, height, progress),
            "qjbox"       => RenderQJBox(gl, width, height, progress),
            "menger"      => RenderMenger(gl, width, height, progress),
            "bicomplex"   => RenderBicomplex(gl, width, height, progress),
            _             => throw new ArgumentException($"Unhandled render '{name}'."),
        };
    }

    private static SKBitmap RenderMandelbox(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuMandelboxRenderer(gl, __pipeline);
        var mb = new MandelboxParams
        {
            Iterations = 14,
            Mode = 0,                 // Mandelbox
            Scale = -1.5f,
            FoldingLimit = 1.0f,
            MinRadius = 0.5f,
            FixedRadius = 1.0f,
            Fudge = 0.8f,
            BoundRadius = 6.0f,
        };
        // The scale=-1.5 Mandelbox attractor sits within radius ~4 of origin.
        var camera = new Camera3D(
            position: new Vector3(4.5f, 3.2f, 4.5f),
            lookAt: new Vector3(0f, 0f, 0f),
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 5f,
            aspectRatio: (float)width / height);

        return renderer.Render(mb, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 220, HitEpsilon: 1e-3f, MaxDistance: 40f, NormalEpsilon: 1e-3f,
                EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.05f, AOIntensity: 1.0f),
            background: new Color(0.97f, 0.965f, 0.94f),
            surface: Color.Rgb(110, 130, 150),
            lightDirection: new Vector3(0.6f, 0.8f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderKifs(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuKifsRenderer(gl, __pipeline);
        // Kaleidoscopic IFS ("Amazing IFS"): rotate -> abs fold -> rotate ->
        // sphere fold -> scale-to-pivot. The post-fold rotation is the curl
        // generator. Params from the Python prototype's rot20 sweet spot
        // (kifs_proto2.py / kifs_spherefold.py): positive scale=2, post-rotation
        // 20/15/10 deg, pivot (1,1,1), with the sphere fold enabled.
        var kf = new KifsParams
        {
            Iterations = 18,
            Scale = 2.0f,
            MinRadius = 0.5f,
            FixedRadius = 1.0f,
            PreRotationRadians = Vector3.Zero,
            PostRotationRadians = new Vector3(
                20f * MathF.PI / 180f, 15f * MathF.PI / 180f, 10f * MathF.PI / 180f),
            Pivot = new Vector3(1f, 1f, 1f),
            Fudge = 0.7f,
            BoundRadius = 6.0f,
        };
        // Camera matches the prototype framing that produced the rot20 silhouette.
        var camera = new Camera3D(
            position: new Vector3(3.0f, 2.2f, 3.0f),
            lookAt: new Vector3(0f, 0f, 0f),
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 5f,
            aspectRatio: (float)width / height);

        return renderer.Render(kf, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 220, HitEpsilon: 1e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.05f, AOIntensity: 1.0f),
            background: new Color(0.97f, 0.965f, 0.94f),
            surface: Color.Rgb(150, 125, 100),
            lightDirection: new Vector3(0.6f, 0.8f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderKleinian(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuKleinianRenderer(gl, __pipeline);
        // Pseudo-Kleinian inversive limit set. The numerical-gradient DE
        // (kleinian_core.glsl) renders the Apollonian foam; the offset generates
        // the set. Params validated in Python (kleinian_numgrad.py): scale 2,
        // radii 0.5/1.0, tiling offset (0.5, 0.5, 1.2).
        var kl = new KleinianParams
        {
            Iterations = 9,
            Scale = 2.0f,
            Cell = 1.0f,
            MinRadius = 0.5f,
            FixedRadius = 1.0f,
            Offset = new Vector3(0.5f, 0.5f, 1.2f),
            Fudge = 0.7f,
            BoundRadius = 6.0f,
        };
        var camera = new Camera3D(
            position: new Vector3(2.0f, 2.0f, 2.6f),
            lookAt: new Vector3(0f, 0f, 0f),
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(kl, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 260, HitEpsilon: 7e-4f, MaxDistance: 40f, NormalEpsilon: 8e-4f,
                EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 1.0f),
            background: new Color(0.97f, 0.965f, 0.94f),
            surface: Color.Rgb(150, 125, 100),
            lightDirection: new Vector3(0.5f, 0.7f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderBicomplex(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuBicomplexRenderer(gl, __pipeline);
        var bp = new BicomplexParams
        {
            Iterations = 12,
            C = new Vector4(-0.5f, 0f, 0f, 0f),
            Cut = true, CutAxis = 0, PlaneOffset = 0f,
        };
        var camera = new Camera3D(
            position: new Vector3(2.8f, 2.0f, 3.5f),
            lookAt: Vector3.Zero,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(bp, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 200, HitEpsilon: 8e-4f, MaxDistance: 30f, NormalEpsilon: 8e-4f,
                EnableSoftShadows: true, ShadowSteps: 30, ShadowSoftness: 10f,
                EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.03f, AOIntensity: 1.0f),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(200, 175, 150),
            lightDirection: new Vector3(0.5f, 0.7f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderMenger(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuMengerRenderer(gl, __pipeline);
        var mg = new MengerParams
        {
            Iterations = 6, Scale = 3.0f,
            Offset = new Vector3(1.0f, 1.0f, 0.0f),
            Rotation = new Vector3(0.10f, 0.07f, 0.04f),
            Fudge = 0.8f,
        };
        var camera = new Camera3D(
            position: new Vector3(3.2f, 2.2f, 4.0f),
            lookAt: Vector3.Zero,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(mg, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 240, HitEpsilon: 6e-4f, MaxDistance: 30f, NormalEpsilon: 6e-4f,
                EnableSoftShadows: true, ShadowSteps: 40, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.03f, AOIntensity: 1.0f),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(195, 170, 145),
            lightDirection: new Vector3(0.5f, 0.7f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderQJBox(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuQJBoxRenderer(gl, __pipeline);
        // Default: half-cut on X axis to showcase the inherited killer feature.
        var qb = new QJBoxParams
        {
            Iterations = 8, Scale = -1.8f, MinRadius = 0.5f, FixedRadius = 1.0f, FoldLimit = 1.0f,
            C = new Vector4(-0.2f, 0.6f, 0.1f, 0.0f),
            WSlice = 0f,
            Rotation = new Vector3(0.10f, 0.07f, 0.04f),
            Cut = true, CutAxis = 0, PlaneOffset = 0f,
            Fudge = 0.6f,
        };
        var camera = new Camera3D(
            position: new Vector3(3.0f, 2.0f, 4.0f),
            lookAt: Vector3.Zero,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(qb, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 280, HitEpsilon: 7e-4f, MaxDistance: 30f, NormalEpsilon: 7e-4f,
                EnableSoftShadows: true, ShadowSteps: 40, ShadowSoftness: 10f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.03f, AOIntensity: 1.0f),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(195, 170, 145),
            lightDirection: new Vector3(0.5f, 0.7f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderHybrid(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuHybridRenderer(gl, __pipeline);
        var hp = new HybridParams
        {
            Iterations = 8, Scale = -1.8f, MinRadius = 0.5f, FixedRadius = 1.0f,
            FoldLimit = 1.0f, Power = 2.0f,
            Rotation = new Vector3(0.12f, 0.08f, 0.04f),
            Fudge = 0.6f,
        };
        var camera = new Camera3D(
            position: new Vector3(3.5f, 2.5f, 4.5f),
            lookAt: Vector3.Zero,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(hp, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 280, HitEpsilon: 7e-4f, MaxDistance: 40f, NormalEpsilon: 7e-4f,
                EnableSoftShadows: true, ShadowSteps: 40, ShadowSoftness: 10f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.03f, AOIntensity: 1.0f),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(190, 170, 145),
            lightDirection: new Vector3(0.5f, 0.7f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderRotBox(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuRotBoxRenderer(gl, __pipeline);
        // Compact-sculptural default: scale -2 Mandelbox regime with a modest
        // compounding rotation (validated stable in Python: rotbox_proto.py).
        var rb = new RotBoxParams
        {
            Iterations = 12, Scale = -2.0f, MinRadius = 0.5f, FixedRadius = 1.0f,
            FoldLimit = 1.0f, Rotation = new Vector3(0.15f, 0.10f, 0.05f),
        };
        var camera = new Camera3D(
            position: new Vector3(7f, 5f, 9f),
            lookAt: Vector3.Zero,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(rb, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 240, HitEpsilon: 8e-4f, MaxDistance: 60f, NormalEpsilon: 8e-4f,
                EnableSoftShadows: true, ShadowSteps: 40, ShadowSoftness: 10f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 1.0f),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(190, 175, 155),
            lightDirection: new Vector3(0.5f, 0.7f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderQuaternionJulia(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuQuaternionJuliaRenderer(gl, __pipeline);
        // Canonical c = (-0.2, 0.8, 0, 0), cut in half on the x=0 plane to reveal
        // the nested interior (validated in Python: qjulia_proto.py).
        var qj = new QuaternionJuliaParams
        {
            Iterations = 10,
            C = new Vector4(-0.2f, 0.8f, 0f, 0f),
            WSlice = 0f,
            Cut = true,
            CutAxis = 1,   // cut across Y -- reveals the dense cross-section
            PlaneOffset = 0f,
        };
        var camera = new Camera3D(
            position: new Vector3(2.2f, 1.5f, 2.6f),
            lookAt: Vector3.Zero,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(qj, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 240, HitEpsilon: 6e-4f, MaxDistance: 14f, NormalEpsilon: 6e-4f,
                EnableSoftShadows: true, ShadowSteps: 40, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.03f, AOIntensity: 1.0f),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(210, 180, 150),
            lightDirection: new Vector3(0.4f, 0.8f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderMandelbulb(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuMandelbulbRenderer(gl, __pipeline);
        var mb = new MandelbulbParams { Iterations = 8, Power = 8.0f, Bailout = 2.0f };
        // The bulb sits within radius ~1.2; frame it from just outside.
        var camera = new Camera3D(
            position: new Vector3(1.6f, 1.1f, 1.9f),
            lookAt: Vector3.Zero,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(mb, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 220, HitEpsilon: 6e-4f, MaxDistance: 12f, NormalEpsilon: 6e-4f,
                EnableSoftShadows: true, ShadowSteps: 40, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.02f, AOIntensity: 1.0f),
            background: new Color(0.02f, 0.03f, 0.07f),
            surface: Color.Rgb(210, 175, 140),
            lightDirection: new Vector3(0.5f, 0.8f, 0.35f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderAttractor(Gl gl, int width, int height, Action<int, int>? progress)
    {
        // Generate canonical Thomas (the clean logo shape), build the spatial
        // hash, upload, and render as a tube. The generate+hash steps are the
        // expensive part; here we just do them once for the headless render.
        var ap = new Parsec.Core.Attractors.AttractorParams { NumSteps = 200_000 };
        var traj = Parsec.Core.Attractors.ThomasAttractor.Generate(ap);
        var hash = Parsec.Core.Attractors.AttractorHash.Build(traj, gridSize: 96);

        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuAttractorRenderer(gl, __pipeline);
        renderer.SetAttractor(hash);

        // Frame the cloud from outside, looking at its center.
        var lo = hash.BoundsMin;
        var hi = hash.BoundsMax;
        var center = (lo + hi) * 0.5f;
        float span = (hi - lo).Length();
        var camPos = center + new Vector3(0.2f, 0.35f, 1.0f) * span * 0.8f;
        var camera = new Camera3D(
            position: camPos,
            lookAt: center,
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 4f,
            aspectRatio: (float)width / height);

        var rp = new AttractorRenderParams { TubeRadius = 0.06f, Fudge = 0.45f };

        return renderer.Render(rp, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 512, HitEpsilon: 1e-3f, MaxDistance: span * 3f, NormalEpsilon: 1e-3f,
                EnableSoftShadows: true, ShadowSteps: 32, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.03f, AOIntensity: 1.0f),
            background: new Color(0.04f, 0.04f, 0.06f),
            surface: Color.Rgb(230, 120, 70),
            lightDirection: new Vector3(0.4f, 0.7f, 0.5f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderAmazingSurf(Gl gl, int width, int height, Action<int, int>? progress)
    {
        using var __pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuMandelboxRenderer(gl, __pipeline);
        // The "Amazing" family uses an abs() fold (mode 1). The |z|/|dr| DE
        // requires a NEGATIVE scale so exterior points escape and the ratio
        // stays meaningful; positive scales collapse the field to solid (the
        // DE underflows to ~0 everywhere). Validated in Python: scale=-1.5
        // with the abs fold + rotation gives a clean distance field with the
        // characteristic curl.
        var mb = new MandelboxParams
        {
            Iterations = 14,
            Mode = 1,                 // Amazing (abs) fold
            Scale = -1.5f,
            FoldingLimit = 1.0f,
            MinRadius = 0.5f,
            FixedRadius = 1.0f,
            RotationRadians = new Vector3(
                13f * MathF.PI / 180f, 9f * MathF.PI / 180f, -20f * MathF.PI / 180f),
            Fudge = 0.8f,
            BoundRadius = 5.0f,
        };
        var camera = new Camera3D(
            position: new Vector3(4.0f, 3.0f, 4.0f),
            lookAt: new Vector3(0f, 0f, 0f),
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 5f,
            aspectRatio: (float)width / height);

        return renderer.Render(mb, camera, width, height,
            new RaymarchSettings(
                MaxSteps: 220, HitEpsilon: 1e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
                EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.05f, AOIntensity: 1.0f),
            background: new Color(0.97f, 0.965f, 0.94f),
            surface: Color.Rgb(150, 125, 100),
            lightDirection: new Vector3(0.6f, 0.8f, 0.4f),
            palette: PaletteParams.Default,
            tileRows: 32,
            progress: progress);
    }

    private static SKBitmap RenderTetrahedron(Gl gl, int width, int height, Action<int, int>? progress)
    {
        var ifs = CanonicalIFS3D.SierpinskiTetrahedron();
        using var pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuRaymarchingRenderer(gl, pipeline, ifs, maxDepth: 10, detailEpsilon: 1e-2f);

        var camera = new Camera3D(
            position: new Vector3(2.2f, 1.7f, 2.4f),
            lookAt: new Vector3(0.5f, 0.5f, 0.5f),
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 5f,
            aspectRatio: (float)width / height);

        return renderer.Render(camera, width, height,
            new RaymarchSettings(
                MaxSteps: 200, HitEpsilon: 1.5e-3f, MaxDistance: 20f, NormalEpsilon: 3e-3f,
                EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 1.2f),
            background: new Color(0.97f, 0.965f, 0.94f),
            surface: Color.Rgb(80, 100, 130),
            lightDirection: new Vector3(0.5f, 0.9f, 0.3f),
            palette: PaletteParams.Default,
            tileRows: 64,
            progress: progress);
    }

    private static SKBitmap RenderTwistedTet(Gl gl, int width, int height, Action<int, int>? progress)
    {
        var ifs = TwistedIFS3D.SierpinskiTetrahedron(
            twistRadians: 0.5f, axisMode: TwistAxisMode.CentroidToVertex);
        using var pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuRaymarchingRenderer(gl, pipeline, ifs, maxDepth: 10, detailEpsilon: 1e-2f);

        var camera = new Camera3D(
            position: new Vector3(2.2f, 1.7f, 2.4f),
            lookAt: new Vector3(0.5f, 0.5f, 0.5f),
            up: Vector3.UnitY,
            verticalFovRadians: MathF.PI / 5f,
            aspectRatio: (float)width / height);

        return renderer.Render(camera, width, height,
            new RaymarchSettings(
                MaxSteps: 200, HitEpsilon: 1.5e-3f, MaxDistance: 20f, NormalEpsilon: 3e-3f,
                EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 12f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 1.2f),
            background: new Color(0.97f, 0.965f, 0.94f),
            surface: Color.Rgb(95, 120, 95),
            lightDirection: new Vector3(0.5f, 0.9f, 0.3f),
            palette: PaletteParams.Default,
            tileRows: 64,
            progress: progress);
    }

    private static SKBitmap RenderTrefoil(Gl gl, int width, int height, Action<int, int>? progress)
    {
        var ifs = KnotIFS.TrefoilKnot(sampleCount: 64, contraction: 0.12f);
        using var pipeline = new RaymarchPipeline(gl);
        using var renderer = new GpuRaymarchingRenderer(gl, pipeline, ifs, maxDepth: 10, detailEpsilon: 1e-2f);

        var camera = new Camera3D(
            position: new Vector3(3.0f, -5.5f, 6.5f),
            lookAt: new Vector3(0f, -0.3f, 0f),
            up: Vector3.UnitZ,
            verticalFovRadians: MathF.PI / 4.5f,
            aspectRatio: (float)width / height);

        return renderer.Render(camera, width, height,
            new RaymarchSettings(
                MaxSteps: 250, HitEpsilon: 2e-3f, MaxDistance: 30f, NormalEpsilon: 4e-3f,
                EnableSoftShadows: true, ShadowSteps: 48, ShadowSoftness: 10f,
                EnableAmbientOcclusion: true, AOSamples: 5, AOStepDistance: 0.04f, AOIntensity: 1.0f),
            background: new Color(0.97f, 0.965f, 0.94f),
            surface: Color.Rgb(80, 100, 130),
            lightDirection: new Vector3(0.5f, -0.4f, 0.9f),
            palette: PaletteParams.Default,
            tileRows: 48,
            progress: progress);
    }
}
