namespace Parsec.App;

/// <summary>
/// Object-space orientation of the fractal, shared across all 3D fractals (a
/// property of the LOOK, like palette / light / DOF / environment). The fractal
/// spins in place about the origin while the camera, key light, floor, and orb
/// lights stay fixed in world space — so lighting plays across the surface as
/// the object turns, which is what hero framing wants (distinct from orbiting
/// the camera). Angles are in DEGREES, applied as yaw (Y) then pitch (X) then
/// roll (Z). All zero = no rotation (byte-identical to the un-rotated path).
/// The three angles are ordinary descriptors, so turntable spins are just a
/// keyframed ramp on Yaw.
/// </summary>
public sealed class RotationState
{
    public float Pitch = 0f;   // X, degrees
    public float Yaw = 0f;     // Y, degrees
    public float Roll = 0f;    // Z, degrees

    /// <summary>Euler angles in radians (X = pitch, Y = yaw, Z = roll) for the
    /// renderer.</summary>
    public System.Numerics.Vector3 ToEulerRadians()
    {
        const float d2r = MathF.PI / 180f;
        return new System.Numerics.Vector3(Pitch * d2r, Yaw * d2r, Roll * d2r);
    }

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            new ParamDescriptor {
                Label = "Yaw (Y)", Group = "Fractal rotation", Min = -180, Max = 180, Decimals = 1,
                Get = () => Yaw, Set = v => Yaw = (float)v },
            new ParamDescriptor {
                Label = "Pitch (X)", Group = "Fractal rotation", Min = -180, Max = 180, Decimals = 1,
                Get = () => Pitch, Set = v => Pitch = (float)v },
            new ParamDescriptor {
                Label = "Roll (Z)", Group = "Fractal rotation", Min = -180, Max = 180, Decimals = 1,
                Get = () => Roll, Set = v => Roll = (float)v },
        },
    };
}
