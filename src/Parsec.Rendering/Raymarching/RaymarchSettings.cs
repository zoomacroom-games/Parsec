namespace Parsec.Rendering.Raymarching;

/// <summary>
/// Tunables for sphere-tracing raymarching. Defaults are sensible for a
/// hero shot of a typical IFS attractor; tweak if you see banding (too few
/// steps), wrong surfaces (too-large epsilon), or background creeping in
/// where it shouldn't (max-distance too small).
///
/// HeroSamples drives super-sampling antialiasing: 1 = one ray per pixel
/// (preview default, original behavior), 4/9/16 = N Halton-jittered rays per
/// pixel averaged together. The RaymarchPipeline reads this to decide how many
/// AA passes to run. Preview keeps it at 1; the hero path sets it from the UI.
/// </summary>
public sealed record RaymarchSettings(
    int MaxSteps = 256,
    float HitEpsilon = 1e-6f,
    float MaxDistance = 75f,
    float NormalEpsilon = 1e-6f,
    bool EnableSoftShadows = true,
    int ShadowSteps = 64,
    float ShadowSoftness = 16f,
    bool EnableAmbientOcclusion = true,
    int AOSamples = 5,
    float AOStepDistance = 0.05f,
    float AOIntensity = 1.0f,
    int HeroSamples = 1,
    bool EnableReflections = false,
    int ReflectionBounces = 2,
    float Gloss = 0.5f,
    float F0 = 0.05f,
    float LightIntensity = 1f,
    // Thin-lens depth of field. Aperture 0 = pinhole (off). Blur is averaged
    // across the SSAA samples, so it needs HeroSamples >= 4 to resolve; the
    // pipeline zeroes the lens jitter at 1 sample (preview stays sharp).
    float FocusDistance = 2.5f,
    float Aperture = 0f);
