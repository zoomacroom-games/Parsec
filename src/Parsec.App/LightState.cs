using System.Numerics;
using Parsec.Rendering.Gpu;

namespace Parsec.App;

/// <summary>
/// Shared key-light controls, applied across all fractals (like the palette and
/// reflection state). The light direction was previously hardcoded per-fractal at
/// the render call sites; lighting is a property of the LOOK, not of a particular
/// fractal's math, so it lives in one shared state and is exposed for every fractal.
///
/// Direction is expressed as azimuth/elevation (artist-friendly: spin around and
/// tilt up/down) and converted to the unit vector the shader already consumes via
/// <see cref="ToDirection"/>. Intensity scales the diffuse contribution; it rides
/// the previously-unused lightDir.w slot, so no GPU struct change is needed.
///
/// Defaults (az 35, el 48, intensity 1.0) reproduce the old hardcoded hero light
/// (~0.6, 0.8, 0.4 normalized) so the out-of-the-box look is unchanged.
/// </summary>
public sealed class LightState
{
    /// <summary>Horizontal angle of the key light in degrees, 0 to 360. Spins the
    /// light around the vertical axis.</summary>
    public float Azimuth = 35f;

    /// <summary>Vertical angle of the key light in degrees, -90 to 90. +90 = straight
    /// overhead, 0 = on the horizon, -90 = straight below.</summary>
    public float Elevation = 48f;

    /// <summary>Diffuse light intensity. 1.0 = the default look; 0 = key light
    /// off (flat ambient fill only); &gt;1 brightens into the HDR headroom
    /// (clamped at white by the finalize pass's sRGB encode).</summary>
    public float Intensity = 1.0f;

    /// <summary>Unit direction TOWARD the light (what the shader's Lambert term
    /// wants). Elevation maps to +Y; azimuth sweeps the XZ plane.</summary>
    public Vector3 ToDirection()
    {
        float az = Azimuth * (MathF.PI / 180f);
        float el = Elevation * (MathF.PI / 180f);
        float c = MathF.Cos(el);
        var dir = new Vector3(c * MathF.Cos(az), MathF.Sin(el), c * MathF.Sin(az));
        return Vector3.Normalize(dir);
    }

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            new ParamDescriptor {
                Label = "Light azimuth", Group = "Light",
                Min = 0, Max = 360, Decimals = 0,
                Get = () => Azimuth, Set = v => Azimuth = (float)v },
            new ParamDescriptor {
                Label = "Light elevation", Group = "Light",
                Min = -90, Max = 90, Decimals = 0,
                Get = () => Elevation, Set = v => Elevation = (float)v },
            new ParamDescriptor {
                Label = "Light intensity", Group = "Light",
                Min = 0, Max = 2, Decimals = 2,
                Get = () => Intensity, Set = v => Intensity = (float)v },
        },
    };
}
