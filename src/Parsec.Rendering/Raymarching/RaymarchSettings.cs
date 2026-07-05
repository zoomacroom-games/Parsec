using System.Numerics;

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
    // across the SSAA samples, so it needs several samples to resolve; the
    // pipeline zeroes the lens jitter for a single-sample render (a lone lens
    // offset would shift the image, not blur it).
    float FocusDistance = 2.5f,
    float Aperture = 0f,
    // Progressive accumulation: how many samples are ALREADY in the GPU
    // accumulator from previous calls at this resolution. 0 (default) starts a
    // fresh render (clears the accumulator); > 0 skips the clear, continues
    // the jitter/lens sample sequence from this index, and finalizes with the
    // running total. Used by the interactive preview to refine DOF/AA in place
    // while the camera is idle; hero and CLI renders leave it at 0.
    int SampleOffset = 0,
    // Procedural skybox: replaces both the primary-miss background and the
    // reflection environment with a zenith/horizon/ground gradient plus a sun
    // glow that tracks the key light. Colors are authored sRGB. Off = legacy
    // flat background (byte-identical output).
    bool SkyboxEnable = false,
    Vector3 SkyZenith = default,
    Vector3 SkyHorizon = default,
    Vector3 SkyGround = default,
    float SunIntensity = 1.0f,
    float SunSharpness = 96f,
    // Reflective floor plane at y = FloorHeight (analytic, exact; the fractal
    // still casts soft shadows and contact AO onto it). FloorReflect 0 = matte,
    // 1 = mirror; the floor reflects the fractal even when the fractal's own
    // glossy reflections are disabled. CheckerScale 0 = plain color.
    bool FloorEnable = false,
    float FloorHeight = -1.0f,
    Vector3 FloorColor = default,
    float FloorReflect = 0.4f,
    float FloorCheckerScale = 0f);
