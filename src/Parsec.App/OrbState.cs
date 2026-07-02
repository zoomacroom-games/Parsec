using System.Numerics;
using Parsec.Rendering.Raymarching;

namespace Parsec.App;

/// <summary>One placeable luminous orb: position, size, light output, and
/// color (authored sRGB, tints both the visible orb and its light). Defaults
/// to the original warm white.</summary>
public sealed class Orb
{
    public float X, Y, Z;
    public float Radius = 0.12f;
    public float Luminosity = 3.0f;
    public float R = 1.0f, G = 0.9f, B = 0.75f;
}

/// <summary>
/// Shared placeable-light state: a small list of luminous orbs, applied across
/// all 3D fractals (like the palette / reflection / key-light states — lights
/// are a property of the LOOK, not of a particular fractal's math). Each orb
/// renders as an emissive sphere and illuminates the fractal as a point light.
///
/// The schema exposes X/Y/Z/Size/Luminosity sliders per orb, which also makes
/// orbs keyframeable in the timeline for free. NOTE: adding or removing an orb
/// changes the descriptor count, so the host must rebuild the panel + timeline
/// (same reset as switching fractals).
/// </summary>
public sealed class OrbState
{
    /// <summary>Mirror of <see cref="RaymarchPipeline.MaxOrbs"/> (the shader's
    /// fixed slot count).</summary>
    public const int MaxOrbs = RaymarchPipeline.MaxOrbs;

    private readonly List<Orb> _orbs = new();

    public int Count => _orbs.Count;

    /// <summary>Add an orb at <paramref name="position"/>. False when the
    /// shader's slot limit is reached.</summary>
    public bool Add(Vector3 position)
    {
        if (_orbs.Count >= MaxOrbs) return false;
        _orbs.Add(new Orb { X = position.X, Y = position.Y, Z = position.Z });
        return true;
    }

    /// <summary>Remove the most recently added orb. False when empty.</summary>
    public bool RemoveLast()
    {
        if (_orbs.Count == 0) return false;
        _orbs.RemoveAt(_orbs.Count - 1);
        return true;
    }

    /// <summary>Snapshot for the render pipeline.</summary>
    public IReadOnlyList<OrbLight> ToLights() =>
        _orbs.Select(o => new OrbLight(
            new Vector3(o.X, o.Y, o.Z), o.Radius, o.Luminosity,
            new Vector3(o.R, o.G, o.B))).ToArray();

    public ParamSchema BuildSchema()
    {
        var ps = new List<ParamDescriptor>();
        for (int i = 0; i < _orbs.Count; i++)
        {
            var orb = _orbs[i];   // capture per-iteration instance, not the index
            string group = $"Orb {i + 1}";
            ps.Add(new ParamDescriptor {
                Label = $"{group} X", Group = group,
                Min = -8, Max = 8, Step = 0.01, Decimals = 2,
                Get = () => orb.X, Set = v => orb.X = (float)v });
            ps.Add(new ParamDescriptor {
                Label = $"{group} Y", Group = group,
                Min = -8, Max = 8, Step = 0.01, Decimals = 2,
                Get = () => orb.Y, Set = v => orb.Y = (float)v });
            ps.Add(new ParamDescriptor {
                Label = $"{group} Z", Group = group,
                Min = -8, Max = 8, Step = 0.01, Decimals = 2,
                Get = () => orb.Z, Set = v => orb.Z = (float)v });
            ps.Add(new ParamDescriptor {
                Label = $"{group} size", Group = group,
                Min = 0.02, Max = 1.5, Decimals = 2,
                Get = () => orb.Radius, Set = v => orb.Radius = (float)v });
            ps.Add(new ParamDescriptor {
                Label = $"{group} luminosity", Group = group,
                Min = 0, Max = 10, Decimals = 2,
                Get = () => orb.Luminosity, Set = v => orb.Luminosity = (float)v });
            // Color as R/G/B sliders, matching the palette's Base R/G/B idiom
            // (and keyframeable, so orb hues can animate).
            ps.Add(new ParamDescriptor {
                Label = $"{group} R", Group = group,
                Min = 0, Max = 1, Decimals = 2,
                Get = () => orb.R, Set = v => orb.R = (float)v });
            ps.Add(new ParamDescriptor {
                Label = $"{group} G", Group = group,
                Min = 0, Max = 1, Decimals = 2,
                Get = () => orb.G, Set = v => orb.G = (float)v });
            ps.Add(new ParamDescriptor {
                Label = $"{group} B", Group = group,
                Min = 0, Max = 1, Decimals = 2,
                Get = () => orb.B, Set = v => orb.B = (float)v });
        }
        return new ParamSchema { Parameters = ps };
    }
}
