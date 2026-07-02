using System.Numerics;
using Parsec.Core.Geometry;
using SkiaSharp;

namespace Parsec.Rendering.Raymarching;

/// <summary>
/// Configuration for <see cref="RaymarchingRenderer"/>.
/// </summary>
public sealed record RaymarchingConfig(
    IDistanceEstimator3D Estimator,
    Camera3D Camera,
    int ImageWidth = 800,
    int ImageHeight = 600,
    RaymarchSettings? Settings = null,
    Color? Background = null,
    Color? Surface = null,
    Vector3? LightDirection = null,
    bool Parallel = true)
{
    public RaymarchSettings EffectiveSettings => Settings ?? new RaymarchSettings();
    public Color EffectiveBackground => Background ?? new Color(0.97f, 0.965f, 0.94f);
    public Color EffectiveSurface => Surface ?? Color.Rgb(80, 100, 130);
    public Vector3 EffectiveLightDirection => Vector3.Normalize(LightDirection ?? new Vector3(0.6f, 1.0f, 0.4f));
}

/// <summary>
/// Renders an <see cref="IDistanceEstimator3D"/> by sphere-tracing rays
/// from a camera and shading hits with Lambert + soft shadows + AO.
/// </summary>
public sealed class RaymarchingRenderer
{
    private readonly RaymarchingConfig _config;

    public RaymarchingRenderer(RaymarchingConfig config)
    {
        _config = config;
    }

    public SKBitmap Render()
    {
        int w = _config.ImageWidth;
        int h = _config.ImageHeight;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);

        // We compute each pixel into a managed buffer, then copy into SkiaSharp.
        var pixels = new byte[w * h * 4];

        var settings = _config.EffectiveSettings;
        var bgColor = _config.EffectiveBackground;
        var lightDir = _config.EffectiveLightDirection;
        var surfaceColor = _config.EffectiveSurface;

        void RenderRow(int y)
        {
            // v=0 at the bottom of the image. Image rows count from the top.
            float v = 1f - (y + 0.5f) / h;
            int rowOffset = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;
                var ray = _config.Camera.RayForUV(u, v);
                var color = TraceRay(ray, settings, bgColor, surfaceColor, lightDir);

                int o = rowOffset + x * 4;
                pixels[o + 0] = color.R8;
                pixels[o + 1] = color.G8;
                pixels[o + 2] = color.B8;
                pixels[o + 3] = color.A8;
            }
        }

        if (_config.Parallel)
        {
            System.Threading.Tasks.Parallel.For(0, h, RenderRow);
        }
        else
        {
            for (int y = 0; y < h; y++) RenderRow(y);
        }

        // Copy into the bitmap.
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    private Color TraceRay(Ray ray, RaymarchSettings settings, Color background, Color surfaceColor, Vector3 lightDir)
    {
        // Fast-skip: advance the ray to the bounding sphere of the attractor.
        var bs = _config.Estimator.BoundingSphere;
        if (!IntersectSphereForward(ray, bs, out float tEnter))
            return background;

        // Sphere-trace from the entry point.
        float t = MathF.Max(0f, tEnter);
        for (int i = 0; i < settings.MaxSteps; i++)
        {
            var p = ray.At(t);
            float d = _config.Estimator.Estimate(p);

            if (d < settings.HitEpsilon)
            {
                // Surface hit. Shade.
                return Shade(p, ray, settings, surfaceColor, lightDir);
            }

            t += d;
            if (t > settings.MaxDistance) break;
        }
        return background;
    }

    private Color Shade(Vector3 hitPoint, Ray ray, RaymarchSettings settings, Color surfaceColor, Vector3 lightDir)
    {
        var normal = EstimateNormal(hitPoint, settings.NormalEpsilon);

        // Pull the point a tiny bit out along the normal so secondary rays
        // don't immediately re-collide with the surface.
        var offsetPoint = hitPoint + normal * settings.HitEpsilon * 4f;

        // Lambert.
        float lambert = MathF.Max(0f, Vector3.Dot(normal, lightDir));

        // Soft shadow toward the light.
        float shadow = 1f;
        if (settings.EnableSoftShadows && lambert > 0f)
            shadow = SoftShadow(offsetPoint, lightDir, settings);

        // Ambient occlusion.
        float ao = 1f;
        if (settings.EnableAmbientOcclusion)
            ao = AmbientOcclusion(offsetPoint, normal, settings);

        // Composite. Ambient term keeps shadowed regions from going pitch black.
        // AO attenuates only the ambient (indirect) term; the key light is
        // already gated by its own shadow ray. Matches the GPU shadeDirect().
        const float ambient = 0.25f;
        float intensity = MathF.Max(0f, settings.LightIntensity);
        float lighting = ambient * ao + (1f - ambient) * lambert * shadow * intensity;

        // Shade in linear light, then re-encode for the 8-bit output: the
        // authored surface color is sRGB, so decode -> multiply -> encode. A
        // fully lit surface reproduces its authored color exactly.
        return new Color(
            LinearToSrgb(SrgbToLinear(surfaceColor.R) * lighting),
            LinearToSrgb(SrgbToLinear(surfaceColor.G) * lighting),
            LinearToSrgb(SrgbToLinear(surfaceColor.B) * lighting),
            1f);
    }

    /// <summary>sRGB decode: display value -> linear light.</summary>
    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>sRGB encode: linear light -> display value, clamped at white.</summary>
    private static float LinearToSrgb(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    private Vector3 EstimateNormal(Vector3 p, float eps)
    {
        // Tetrahedron sample pattern: 4 evaluations instead of the naive 6.
        // The four offset directions sum to zero and span 3D.
        // See e.g. iquilezles.org for the standard variant.
        var k0 = new Vector3( 1, -1, -1);
        var k1 = new Vector3(-1, -1,  1);
        var k2 = new Vector3(-1,  1, -1);
        var k3 = new Vector3( 1,  1,  1);
        float d0 = _config.Estimator.Estimate(p + k0 * eps);
        float d1 = _config.Estimator.Estimate(p + k1 * eps);
        float d2 = _config.Estimator.Estimate(p + k2 * eps);
        float d3 = _config.Estimator.Estimate(p + k3 * eps);
        var n = k0 * d0 + k1 * d1 + k2 * d2 + k3 * d3;
        float len = n.Length();
        return len > float.Epsilon ? n / len : Vector3.UnitY;
    }

    private float SoftShadow(Vector3 origin, Vector3 dir, RaymarchSettings settings)
    {
        // Standard sphere-tracing soft shadow (Quilez).
        float result = 1f;
        float t = settings.HitEpsilon * 4f;
        for (int i = 0; i < settings.ShadowSteps; i++)
        {
            var p = origin + dir * t;
            float d = _config.Estimator.Estimate(p);
            if (d < settings.HitEpsilon) return 0f;
            result = MathF.Min(result, settings.ShadowSoftness * d / t);
            t += d;
            if (t > settings.MaxDistance) break;
        }
        return MathF.Max(0f, result);
    }

    private float AmbientOcclusion(Vector3 p, Vector3 normal, RaymarchSettings settings)
    {
        // Step outward along the normal in small increments; compare actual
        // empty space (the DE value) to the step distance. Surfaces that
        // crowd close to p reduce AO.
        float occ = 0f;
        float weight = 1f;
        for (int i = 1; i <= settings.AOSamples; i++)
        {
            float step = i * settings.AOStepDistance;
            var sample = p + normal * step;
            float d = _config.Estimator.Estimate(sample);
            occ += (step - d) * weight;
            weight *= 0.5f;
        }
        float ao = 1f - settings.AOIntensity * MathF.Max(0f, occ);
        return Math.Clamp(ao, 0f, 1f);
    }

    /// <summary>
    /// Ray-sphere intersection: returns the smallest non-negative t at which
    /// the ray enters or grazes the sphere. False if it misses.
    /// </summary>
    private static bool IntersectSphereForward(Ray ray, BoundingSphere sphere, out float t)
    {
        var oc = ray.Origin - sphere.Center;
        float b = Vector3.Dot(oc, ray.Direction);
        float c = Vector3.Dot(oc, oc) - sphere.Radius * sphere.Radius;

        // If origin is inside, return 0.
        if (c <= 0f) { t = 0f; return true; }

        // Origin is outside; need real positive roots.
        float disc = b * b - c;
        if (disc < 0f) { t = 0f; return false; }

        float sqrtDisc = MathF.Sqrt(disc);
        float t0 = -b - sqrtDisc;
        if (t0 >= 0f) { t = t0; return true; }
        float t1 = -b + sqrtDisc;
        if (t1 >= 0f) { t = t1; return true; }
        t = 0f;
        return false;
    }
}
