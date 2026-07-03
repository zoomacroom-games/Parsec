namespace Parsec.App;

/// <summary>
/// Shared thin-lens depth-of-field controls, applied across all 3D fractals
/// (like the palette / light / orb states — a property of the LOOK). Aperture
/// 0 (the default) is a pinhole: no blur, and rendering is byte-identical to
/// the pre-DOF path.
///
/// The blur is averaged across the hero SSAA samples, so it only resolves in
/// hero/animation renders with AA at 4x or higher — the interactive preview
/// renders one sample and deliberately stays sharp. Both sliders are ordinary
/// descriptors, so focus pulls and aperture ramps are keyframeable.
/// </summary>
public sealed class DofState
{
    /// <summary>Distance from the camera to the focal plane, in world units.</summary>
    public float FocusDistance = 2.5f;

    /// <summary>Lens aperture radius in world units. 0 = off (pinhole);
    /// ~0.02 is subtle, ~0.1 is a strong cinematic blur.</summary>
    public float Aperture = 0f;

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            new ParamDescriptor {
                Label = "Focus distance", Group = "Depth of field (hero AA 4x+)",
                Min = 0.05, Max = 15, Step = 0.01, Decimals = 2,
                Get = () => FocusDistance, Set = v => FocusDistance = (float)v },
            new ParamDescriptor {
                Label = "Aperture (0 = off)", Group = "Depth of field (hero AA 4x+)",
                Min = 0, Max = 0.25, Decimals = 3,
                Get = () => Aperture, Set = v => Aperture = (float)v },
        },
    };
}
