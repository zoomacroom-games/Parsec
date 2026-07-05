namespace Parsec.App;

/// <summary>
/// Shared environment controls, applied across all 3D fractals (like the
/// palette / light / DOF states — a property of the LOOK):
///
/// SKYBOX — a procedural sky replacing the flat background: zenith/horizon/
/// ground gradient plus a sun glow that tracks the key light direction. The
/// same sky feeds reflections, so glossy surfaces and the floor mirror it.
///
/// FLOOR — an analytic reflective plane at an adjustable height under (or
/// above) the fractal. The fractal casts soft shadows and contact AO onto it,
/// and the floor reflects the fractal even when the fractal's own glossy
/// reflections are off. Reflectivity 0 = matte, 1 = mirror; an optional
/// checker pattern helps ground the sense of scale.
///
/// Both default OFF: rendering is byte-identical to the pre-environment path
/// until enabled. All values are ordinary descriptors, so sky ramps, sun
/// sweeps, and floor reveals are keyframeable.
/// </summary>
public sealed class EnvironmentState
{
    public int SkyEnable = 0;                                  // 0/1
    public float ZenithR = 0.10f, ZenithG = 0.16f, ZenithB = 0.28f;
    public float HorizonR = 0.45f, HorizonG = 0.38f, HorizonB = 0.32f;
    public float GroundR = 0.16f, GroundG = 0.13f, GroundB = 0.11f;
    public float SunIntensity = 1.2f;
    public float SunSharpness = 96f;

    public int FloorEnable = 0;                                // 0/1
    public float FloorHeight = -1.0f;
    public float FloorR = 0.55f, FloorG = 0.52f, FloorB = 0.48f;
    public float FloorReflect = 0.45f;
    public float FloorChecker = 0f;                            // 0 = plain

    public ParamSchema BuildSchema() => new()
    {
        Parameters = new[]
        {
            new ParamDescriptor {
                Label = "Skybox (0/1)", Group = "Skybox", Min = 0, Max = 1, Step = 1, Decimals = 0,
                Get = () => SkyEnable, Set = v => SkyEnable = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Zenith R", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => ZenithR, Set = v => ZenithR = (float)v },
            new ParamDescriptor {
                Label = "Zenith G", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => ZenithG, Set = v => ZenithG = (float)v },
            new ParamDescriptor {
                Label = "Zenith B", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => ZenithB, Set = v => ZenithB = (float)v },
            new ParamDescriptor {
                Label = "Horizon R", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => HorizonR, Set = v => HorizonR = (float)v },
            new ParamDescriptor {
                Label = "Horizon G", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => HorizonG, Set = v => HorizonG = (float)v },
            new ParamDescriptor {
                Label = "Horizon B", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => HorizonB, Set = v => HorizonB = (float)v },
            new ParamDescriptor {
                Label = "Ground R", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => GroundR, Set = v => GroundR = (float)v },
            new ParamDescriptor {
                Label = "Ground G", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => GroundG, Set = v => GroundG = (float)v },
            new ParamDescriptor {
                Label = "Ground B", Group = "Skybox", Min = 0, Max = 1, Decimals = 3,
                Get = () => GroundB, Set = v => GroundB = (float)v },
            new ParamDescriptor {
                Label = "Sun intensity", Group = "Skybox", Min = 0, Max = 4, Decimals = 2,
                Get = () => SunIntensity, Set = v => SunIntensity = (float)v },
            new ParamDescriptor {
                Label = "Sun sharpness", Group = "Skybox", Min = 8, Max = 512, Decimals = 0,
                Get = () => SunSharpness, Set = v => SunSharpness = (float)v },

            new ParamDescriptor {
                Label = "Floor (0/1)", Group = "Floor", Min = 0, Max = 1, Step = 1, Decimals = 0,
                Get = () => FloorEnable, Set = v => FloorEnable = (int)Math.Round(v) },
            new ParamDescriptor {
                Label = "Floor height", Group = "Floor", Min = -3, Max = 3, Decimals = 3,
                Get = () => FloorHeight, Set = v => FloorHeight = (float)v },
            new ParamDescriptor {
                Label = "Floor R", Group = "Floor", Min = 0, Max = 1, Decimals = 3,
                Get = () => FloorR, Set = v => FloorR = (float)v },
            new ParamDescriptor {
                Label = "Floor G", Group = "Floor", Min = 0, Max = 1, Decimals = 3,
                Get = () => FloorG, Set = v => FloorG = (float)v },
            new ParamDescriptor {
                Label = "Floor B", Group = "Floor", Min = 0, Max = 1, Decimals = 3,
                Get = () => FloorB, Set = v => FloorB = (float)v },
            new ParamDescriptor {
                Label = "Reflectivity", Group = "Floor", Min = 0, Max = 1, Decimals = 2,
                Get = () => FloorReflect, Set = v => FloorReflect = (float)v },
            new ParamDescriptor {
                Label = "Checker scale (0 = plain)", Group = "Floor", Min = 0, Max = 8, Decimals = 2,
                Get = () => FloorChecker, Set = v => FloorChecker = (float)v },
        },
    };
}
