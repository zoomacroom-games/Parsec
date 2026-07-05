using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Parsec.Rendering;
using Parsec.Rendering.DeepZoom;
using Parsec.Rendering.Gpu;
using Parsec.Rendering.Raymarching;

namespace Parsec.App;

/// <summary>
/// Stage 2: a live free-fly view of the AmazingBox. WASD moves (with Q/E for
/// vertical), hold left-drag to mouse-look. Movement speed scales with the
/// distance estimate at the camera so it feels right at every scale. Renders
/// on change only — idle draws nothing.
/// </summary>
public enum FractalType { AmazingBox, Mandelbox, Kifs, Kleinian, Attractor, Mandelbulb, QuaternionJulia, RotBox, Hybrid, QJBox, Menger, Bicomplex, Apollonian, Phoenix, Biomorph, Mosely, PseudoKleinian4D, RiemannSphere, Mandalay, Anisotropic, OrbitHybrid, BurningShip, DeepZoom }

public sealed class FractalView : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    private Gl? _gl;
    private RaymarchPipeline? _pipeline;
    private GpuMandelboxRenderer? _boxRenderer;
    private GpuKifsRenderer? _kifsRenderer;
    private GpuKleinianRenderer? _kleinianRenderer;
    private GpuAttractorRenderer? _attractorRenderer;
    private GpuMandelbulbRenderer? _mandelbulbRenderer;
    private GpuQuaternionJuliaRenderer? _qjuliaRenderer;
    private GpuRotBoxRenderer? _rotboxRenderer;
    private GpuHybridRenderer? _hybridRenderer;
    private GpuQJBoxRenderer? _qjboxRenderer;
    private GpuMengerRenderer? _mengerRenderer;
    private GpuBicomplexRenderer? _bicomplexRenderer;
    private GpuApollonianRenderer? _apollonianRenderer;
    private GpuPhoenixRenderer? _phoenixRenderer;
    private GpuBiomorphRenderer? _biomorphRenderer;
    private GpuMoselyRenderer? _moselyRenderer;
    private GpuPseudoKleinian4DRenderer? _pk4dRenderer;
    private GpuRiemannSphereRenderer? _riemannRenderer;
    private GpuMandalayRenderer? _mandalayRenderer;
    private GpuAnisotropicRenderer? _anisoRenderer;
    private GpuOrbitHybridRenderer? _orbitHybridRenderer;
    private GpuBurningShipRenderer? _burningShipRenderer;
    private DeepZoomPipeline? _deepPipeline;
    private Parsec.Core.Attractors.AttractorHash? _attractorHash;
    private bool _attractorNeedsRegen = true;
    private uint _texture, _vao, _blitProgram;
    private int _samplerLocation;
    private bool _ready;
    private bool _dirty = true;

    // Live fractal parameters, shared with the parameter panel.
    public AmazingBoxState Fractal { get; } = new();
    public MandelboxState Mandelbox { get; } = new();
    public KifsState Kifs { get; } = new();
    public KleinianState Kleinian { get; } = new();
    public AttractorState Attractor { get; } = new();
    public MandelbulbState Mandelbulb { get; } = new();
    public QuaternionJuliaState QuaternionJulia { get; } = new();
    public RotBoxState RotBox { get; } = new();
    public HybridState Hybrid { get; } = new();
    public QJBoxState QJBox { get; } = new();
    public MengerState Menger { get; } = new();
    public BicomplexState Bicomplex { get; } = new();
    public ApollonianState Apollonian { get; } = new();
    public PhoenixState Phoenix { get; } = new();
    public BiomorphState Biomorph { get; } = new();
    public MoselyState Mosely { get; } = new();
    public PseudoKleinian4DState PseudoKleinian4D { get; } = new();
    public RiemannSphereState RiemannSphere { get; } = new();
    public MandalayState Mandalay { get; } = new();
    public AnisotropicState Anisotropic { get; } = new();
    public OrbitHybridState OrbitHybrid { get; } = new();
    public BurningShipState BurningShip { get; } = new();

    /// <summary>Orbit-trap palette, shared across all fractals.</summary>
    public PaletteState Palette { get; } = new();

    /// <summary>Glossy-reflection material controls, shared across all fractals.</summary>
    public ReflectionState Reflection { get; } = new();

    /// <summary>Key-light direction + intensity, shared across all fractals.</summary>
    public LightState Light { get; } = new();

    /// <summary>Placeable luminous orbs, shared across all 3D fractals.</summary>
    public OrbState Orbs { get; } = new();

    /// <summary>Thin-lens depth of field, shared across all 3D fractals.</summary>
    public DofState Dof { get; } = new();

    /// <summary>Skybox + reflective floor plane, shared across all 3D fractals.</summary>
    public EnvironmentState Env { get; } = new();

    /// <summary>Object-space fractal orientation, shared across all 3D fractals.</summary>
    public RotationState Rotation { get; } = new();

    /// <summary>Which fractal is currently displayed.</summary>
    public FractalType ActiveType { get; private set; } = FractalType.Kifs;

    /// <summary>Super-sampling AA factor for hero renders (1/4/9/16). Bound from
    /// the UI; preview always renders at 1x regardless of this value.</summary>
    public int HeroSampleCount { get; set; } = 1;

    public void SetActiveType(FractalType type)
    {
        ActiveType = type;
        MarkDirty();
    }

    /// <summary>
    /// Build the combined schema for the active fractal plus the shared palette,
    /// so the panel shows fractal params followed by colour controls.
    /// </summary>
    public ParamSchema BuildActiveSchema()
    {
        // Deep-zoom is a 2D escape-time mode: no DE-based fractal params, and no
        // 3D reflection/light. Expose the shared palette only (pan/zoom drive the
        // view via the mouse).
        if (ActiveType == FractalType.DeepZoom)
        {
            // Formula is chosen by the FORMULA dropdown (see SetDeepFormula), not a
            // slider. The panel exposes the palette plus the Julia constant kappa --
            // keyframeable, so a kappa sweep morphs the Julia set in an animation.
            var deepParams = new List<ParamDescriptor>
            {
                new ParamDescriptor {
                    Label = "kappa re (Julia)", Group = "Julia",
                    Min = -2.0, Max = 2.0, Step = 0.0001, Decimals = 4,
                    Get = () => _deepView.KappaRe,
                    Set = v => _deepView.KappaRe = v },
                new ParamDescriptor {
                    Label = "kappa im (Julia)", Group = "Julia",
                    Min = -2.0, Max = 2.0, Step = 0.0001, Decimals = 4,
                    Get = () => _deepView.KappaIm,
                    Set = v => _deepView.KappaIm = v },
            };
            deepParams.AddRange(Palette.BuildSchema().Parameters);
            return new ParamSchema { Parameters = deepParams };
        }

        var fractalSchema = ActiveType switch
        {
            FractalType.AmazingBox => Fractal.BuildSchema(),
            FractalType.Mandelbox => Mandelbox.BuildSchema(),
            FractalType.Kifs => Kifs.BuildSchema(),
            FractalType.Kleinian => Kleinian.BuildSchema(),
            FractalType.Attractor => Attractor.BuildSchema(),
            FractalType.Mandelbulb => Mandelbulb.BuildSchema(),
            FractalType.QuaternionJulia => QuaternionJulia.BuildSchema(),
            FractalType.RotBox => RotBox.BuildSchema(),
            FractalType.Hybrid => Hybrid.BuildSchema(),
            FractalType.QJBox => QJBox.BuildSchema(),
            FractalType.Menger => Menger.BuildSchema(),
            FractalType.Bicomplex => Bicomplex.BuildSchema(),
            FractalType.Apollonian => Apollonian.BuildSchema(),
            FractalType.Phoenix => Phoenix.BuildSchema(),
            FractalType.Biomorph => Biomorph.BuildSchema(),
            FractalType.Mosely => Mosely.BuildSchema(),
            FractalType.PseudoKleinian4D => PseudoKleinian4D.BuildSchema(),
            FractalType.RiemannSphere => RiemannSphere.BuildSchema(),
            FractalType.Mandalay => Mandalay.BuildSchema(),
            FractalType.Anisotropic => Anisotropic.BuildSchema(),
            FractalType.OrbitHybrid => OrbitHybrid.BuildSchema(),
            FractalType.BurningShip => BurningShip.BuildSchema(),
            _ => Kifs.BuildSchema(),
        };
        var combined = new List<ParamDescriptor>(fractalSchema.Parameters);
        combined.AddRange(Palette.BuildSchema().Parameters);
        combined.AddRange(Reflection.BuildSchema().Parameters);
        combined.AddRange(Light.BuildSchema().Parameters);
        combined.AddRange(Dof.BuildSchema().Parameters);
        combined.AddRange(Env.BuildSchema().Parameters);
        combined.AddRange(Rotation.BuildSchema().Parameters);
        combined.AddRange(Orbs.BuildSchema().Parameters);
        return new ParamSchema { Parameters = combined };
    }

    /// <summary>Request a re-render (e.g. after a parameter change from the panel).</summary>
    public void MarkDirty()
    {
        _expectRefine = false;   // scene changed: restart DOF refinement from a fresh base frame
        _dirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Flag that the attractor's GENERATION params changed, so the trajectory +
    /// spatial hash must be rebuilt before the next attractor render. The
    /// expensive rebuild itself happens on the GL thread (it uploads SSBOs), so
    /// here we just set the flag and request a frame. Camera/colour/tube changes
    /// do NOT call this -- they go through MarkDirty for an immediate re-render.
    /// </summary>
    public void RequestAttractorRegen()
    {
        _attractorNeedsRegen = true;
        _expectRefine = false;
        _dirty = true;
        RequestNextFrameRendering();
    }

    // --- hero render (high-res still to PNG) ---
    private bool _heroPending;
    private string? _heroPath;
    private int _heroWidth, _heroHeight;

    // --- Animation batch render ---
    private bool _animPending;
    private string? _animDir;
    private int _animWidth, _animHeight, _animFrameCount;
    private double _animFps;
    private Action<double>? _animApplyAtTime;   // applies interpolated state at time t (seconds)

    /// <summary>Fired (on UI thread) when an animation batch render finishes.</summary>
    public event Action<string>? AnimationRenderComplete;
    /// <summary>Fired (on UI thread) periodically during a batch render with (frame, total).</summary>
    public event Action<int, int>? AnimationProgress;

    /// <summary>
    /// Render an animation: <paramref name="applyAtTime"/> sets the live params for
    /// a given playback time (seconds); we sample it at each frame, render at the
    /// target resolution, and save frame_NNNNN.png into <paramref name="dir"/>.
    /// Runs synchronously on the next GL callback (the UI will freeze for the
    /// duration -- acceptable for a test render). Camera is whatever it is now
    /// (camera animation is a later phase).
    /// </summary>
    public void RequestAnimationRender(string dir, int width, int height,
        double fps, double durationSeconds, Action<double> applyAtTime)
    {
        _animDir = dir;
        _animWidth = width;
        _animHeight = height;
        _animFps = fps;
        _animFrameCount = Math.Max(1, (int)Math.Round(durationSeconds * fps));
        _animApplyAtTime = applyAtTime;
        _animPending = true;
        RequestNextFrameRendering();
    }

    /// <summary>Raised after a hero render finishes (or fails), with a status string.</summary>
    public event Action<string>? HeroRenderComplete;

    /// <summary>
    /// Queue a high-resolution render of the CURRENT view, params, and palette to
    /// a PNG at <paramref name="path"/>. The render runs on the next GL frame
    /// (the only time the context is current) via the TDR-safe tiled path.
    /// </summary>
    public void RequestHeroRender(string path, int width, int height)
    {
        _heroPath = path;
        _heroWidth = width;
        _heroHeight = height;
        _heroPending = true;
        RequestNextFrameRendering();
    }

    private const int PreviewWidth = 640;
    private const int PreviewHeight = 480;

    // The texture's current pixel dimensions. 3D modes render to the fixed
    // preview buffer (then upscale -- soft is fine for raymarching); deep-zoom
    // renders at native control resolution for crisp 1:1 detail. This tracks
    // whichever was last used so the blit can letterbox by the right aspect.
    private int _texW = PreviewWidth, _texH = PreviewHeight;

    // --- camera + input state ---
    private readonly FlyCamera _cam = new(
        position: new Vector3(4.0f, 3.0f, 4.0f),
        yaw: -MathF.PI / 4f,   // faces back toward the origin from (4,3,4)
        pitch: -0.53f);

    // 2D deep-zoom "camera": high-precision center + radius (see DeepZoomView).
    private readonly DeepZoomView _deepView = new();

    /// <summary>Current deep-zoom formula (0 Mandelbrot, 1 Prospector, 2 Julia,
    /// 3 Burning Ship). Driven by the FORMULA dropdown in the main window.</summary>
    public int DeepFormula => _deepView.Formula;

    /// <summary>Switch the deep-zoom formula and reframe to its home view. The
    /// formula is a mode (not a keyframeable slider), so this lives outside the
    /// parameter schema; changing it reframes and triggers a redraw.</summary>
    public void SetDeepFormula(int formula)
    {
        if (formula == _deepView.Formula) return;
        _deepView.Formula = formula;
        _deepView.ApplyFormulaHome();
        MarkDirty();
    }

    private readonly HashSet<Key> _keysDown = new();
    private bool _looking;
    private Point _lastPointer;
    private DateTime _lastTick = DateTime.UtcNow;

    private DispatcherTimer? _moveTimer;

    // Progressive DOF preview: when the aperture is open, the idle preview
    // re-renders with an accumulation offset so thin-lens blur (and AA)
    // refines in place on top of the sharp base frame. _previewAccum counts
    // samples currently in the pipeline's accumulator for the on-screen
    // frame; _expectRefine marks that the NEXT dirty render is one of our
    // refinement passes rather than a fresh scene (any real change clears it
    // via MarkDirty, which restarts from a sharp base frame).
    private int _previewAccum;
    private int _previewSampleOffset;
    private bool _expectRefine;

    private bool CanRefineDofPreview() =>
        ActiveType != FractalType.DeepZoom
        && Dof.Aperture > 0f
        && _previewAccum >= 1
        && _previewAccum < Math.Max(1, Dof.PreviewSamples)
        && !_looking
        && _keysDown.Count == 0
        && !(ActiveType == FractalType.Attractor && _attractorHash == null);

    // Deep-zoom adaptive resolution: while panning/zooming we render at a reduced
    // scale (targeting a known-interactive pixel budget) for responsiveness, then
    // render once at full native resolution after the interaction settles. The
    // move timer drives the settle check.
    private DateTime _lastDeepInteraction = DateTime.MinValue;
    // True while the user is actively panning/zooming the deep view -> the render
    // path draws low-res for responsiveness. Set by the interaction handlers and
    // cleared by the settle timer (OnMoveTick). Deliberately NOT recomputed from
    // wall-clock at render time: deep frames are slow, so a backlogged render
    // would see the stamp as already-stale and flip to the expensive native pass
    // mid-scroll, spiraling into a hang.
    private bool _deepInteracting;
    private const double DeepSettleMs = 500;
    // Interactive preview budget, in iteration*pixels. Held roughly constant and
    // spent on iterations FIRST (so the structure you're navigating into is
    // visible at depth, where it takes many thousands of iterations to escape),
    // then on resolution. ~ the old 640x480 x 3000 cost that scrolled smoothly.
    private const double DeepInteractiveCost = 640.0 * 480.0 * 3000.0;
    // Don't let the preview drop below this fraction of native per axis, even if
    // it means capping iterations -- a single block isn't navigable either.
    private const double DeepPreviewMinScale = 0.08;
    private int _deepPreviewIter = 1000;   // iteration count chosen for the last preview

    private const float BaseMoveSpeed = 1.5f;   // multiplied by DE-at-camera per second
    private const float LookSensitivity = 0.005f; // radians per pixel
    private const float RollSpeed = 1.2f;        // radians per second for Z/C bank

    public event Action<string>? StatusChanged;
    private void Status(string text) => StatusChanged?.Invoke(text);

    public FractalView()
    {
        Focusable = true;
        IsHitTestVisible = true;
        // A timer integrates movement while keys are held and requests redraws.
        _moveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _moveTimer.Tick += OnMoveTick;
        _moveTimer.Start();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Grab focus once we're in the tree so WASD registers without a click.
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
    }

    // OpenGlControlBase derives from Control, which has no Background and so is
    // invisible to pointer hit-testing (it renders via a GPU surface with no
    // hit-testable content). Implementing ICustomHitTest claims our full bounds
    // for hit-testing, so pointer events actually reach us. Without this,
    // keyboard works (focus is separate) but mouse events never arrive.
    public bool HitTest(Point point) =>
        point.X >= 0 && point.Y >= 0 && point.X <= Bounds.Width && point.Y <= Bounds.Height;

    // ----------------------------------------------------------------- input
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        bool left = props.IsLeftButtonPressed
                    || props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        if (left)
        {
            _looking = true;
            _lastPointer = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_looking) return;
        var pos = e.GetPosition(this);
        var dx = (float)(pos.X - _lastPointer.X);
        var dy = (float)(pos.Y - _lastPointer.Y);
        _lastPointer = pos;
        if (ActiveType == FractalType.DeepZoom)
        {
            // Drag-pan the complex plane. Height is the displayed control height,
            // so the pan tracks the cursor as a fraction of the view regardless of
            // the fixed preview resolution.
            _deepView.PanPixels(dx, dy, Math.Max(1, (int)Bounds.Height));
            _lastDeepInteraction = DateTime.UtcNow;
            _deepInteracting = true;
            _dirty = true;
            RequestNextFrameRendering();
            return;
        }
        // Drag right -> look right (yaw+); drag up -> look up (pitch+).
        _cam.Look(dx * LookSensitivity, -dy * LookSensitivity);
        _dirty = true;
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_looking)
        {
            _looking = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ActiveType != FractalType.DeepZoom) return;
        // Stepped zoom toward the cursor: each notch scales the radius and shifts
        // the center so the point under the pointer stays put. A radius change
        // triggers a reference-orbit recompute in the pipeline (per the design).
        var pos = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 0.8 : 1.25;   // up = zoom in
        _deepView.ZoomTowardPixel(factor, pos.X, pos.Y,
            Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        _lastDeepInteraction = DateTime.UtcNow;
        _deepInteracting = true;
        _dirty = true;
        RequestNextFrameRendering();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _keysDown.Add(e.Key);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keysDown.Remove(e.Key);
    }

    private void OnMoveTick(object? sender, EventArgs e)
    {
        if (ActiveType == FractalType.DeepZoom)
        {
            // Once panning/zooming has paused, upgrade the last low-res frame to a
            // full native render (one shot -- clearing _deepInteracting guards repeats).
            if (_deepInteracting &&
                (DateTime.UtcNow - _lastDeepInteraction).TotalMilliseconds >= DeepSettleMs)
            {
                _deepInteracting = false;   // settled -> next render is the crisp native pass
                _dirty = true;
                RequestNextFrameRendering();
            }
            return;   // 2D: mouse pan/zoom, no WASD fly
        }

        var now = DateTime.UtcNow;
        float dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (dt <= 0f || dt > 0.1f) dt = 0.016f; // clamp hitches

        Vector3 local = Vector3.Zero;
        if (_keysDown.Contains(Key.W)) local.Z += 1;
        if (_keysDown.Contains(Key.S)) local.Z -= 1;
        if (_keysDown.Contains(Key.D)) local.X += 1;
        if (_keysDown.Contains(Key.A)) local.X -= 1;
        if (_keysDown.Contains(Key.E)) local.Y += 1;
        if (_keysDown.Contains(Key.Q)) local.Y -= 1;

        // Roll / bank about the forward axis (Z/C). Independent of translation,
        // so it works while stationary too.
        float roll = 0f;
        if (_keysDown.Contains(Key.Z)) roll -= 1f;   // bank counter-clockwise
        if (_keysDown.Contains(Key.C)) roll += 1f;   // bank clockwise

        if (local == Vector3.Zero && roll == 0f) return;

        if (roll != 0f)
            _cam.RollBy(roll * RollSpeed * dt);

        if (local != Vector3.Zero)
        {
            local = Vector3.Normalize(local);

            // Speed scales with distance to the surface: glide slowly near detail,
            // travel fast in open space. Clamp so we never freeze or rocket.
            float de = EstimateCameraDE();
            float speed = BaseMoveSpeed * Math.Clamp(de, 0.02f, 4.0f);
            _cam.Move(local, speed * dt);
        }

        _dirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Distance estimate at the camera for the active fractal, used to scale
    /// fly speed and to place new orbs. Falls back to 1.0 for chapters with no
    /// cheap closed-form CPU DE (attractor, apollonian, phoenix, ...).
    /// </summary>
    private float EstimateCameraDE() => ActiveType switch
    {
        FractalType.Kleinian => KleinianDE.Estimate(_cam.Position, Kleinian.ToParams()),
        FractalType.Kifs => KifsDE.Estimate(_cam.Position, Kifs.ToParams()),
        // The attractor has no cheap closed-form DE (it lives in the hash),
        // and is viewed from outside, so a steady mid-speed glide is fine.
        FractalType.Attractor => 1.0f,
        FractalType.Mandelbulb => MandelbulbDE.Estimate(_cam.Position, Mandelbulb.ToParams()),
        FractalType.QuaternionJulia => QuaternionJuliaDE.Estimate(_cam.Position, QuaternionJulia.ToParams()),
        FractalType.RotBox => RotBoxDE.Estimate(_cam.Position, RotBox.ToParams()),
        FractalType.Hybrid => HybridDE.Estimate(_cam.Position, Hybrid.ToParams()),
        FractalType.QJBox => QJBoxDE.Estimate(_cam.Position, QJBox.ToParams()),
        FractalType.Menger => MengerDE.Estimate(_cam.Position, Menger.ToParams()),
        FractalType.Bicomplex => BicomplexDE.Estimate(_cam.Position, Bicomplex.ToParams()),
        FractalType.Apollonian => 1.0f,
        // Phoenix: same situation as Apollonian -- numerical-gradient DE only,
        // no cheap closed-form CPU DE. Could port one later if needed.
        FractalType.Phoenix => 1.0f,
        // Biomorph: same Mandelbulb-style scalar-derivative DE as Phoenix,
        // no closed-form CPU DE port done.
        FractalType.Biomorph => 1.0f,
        // Mosely: exact GPU DE, but no CPU port yet -> steady glide.
        FractalType.Mosely => 1.0f,
        FractalType.PseudoKleinian4D => 1.0f,
        FractalType.RiemannSphere => 1.0f,
        FractalType.Mandalay => 1.0f,
        FractalType.Anisotropic => 1.0f,
        FractalType.OrbitHybrid => 1.0f,
        FractalType.BurningShip => 1.0f,
        FractalType.Mandelbox => MandelboxDE.Estimate(_cam.Position, Mandelbox.ToParams()),
        _ => MandelboxDE.Estimate(_cam.Position, Fractal.ToParams()),
    };

    /// <summary>
    /// Place a new luminous orb somewhere visible: along the camera's view
    /// direction, most of the way toward the fractal surface (via the DE at
    /// the camera), with a small per-index lateral offset so consecutive orbs
    /// don't stack on one spot. Returns false at the orb limit.
    /// </summary>
    public bool AddOrbInView()
    {
        float de = EstimateCameraDE();
        float d = Math.Clamp(de * 0.8f, 0.4f, 4.0f);
        int i = Orbs.Count;
        var lateral = _cam.Right * (0.35f * ((i % 3) - 1))
                    + _cam.UpLocal * (0.35f * ((i / 3) % 3 - 1));
        bool ok = Orbs.Add(_cam.Position + _cam.Forward * d + lateral);
        if (ok) MarkDirty();
        return ok;
    }

    /// <summary>Remove the most recently placed orb. Returns false when none.</summary>
    public bool RemoveLastOrb()
    {
        bool ok = Orbs.RemoveLast();
        if (ok) MarkDirty();
        return ok;
    }

    // ------------------------------------------------------------------- GL
    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = new Gl(gl.GetProcAddress);
            Status($"GL {_gl.GetString(GlConst.Version)} | {_gl.GetString(GlConst.Renderer)}");
            _pipeline = new RaymarchPipeline(_gl);
            _boxRenderer = new GpuMandelboxRenderer(_gl, _pipeline);
            _kifsRenderer = new GpuKifsRenderer(_gl, _pipeline);
            _kleinianRenderer = new GpuKleinianRenderer(_gl, _pipeline);
            _attractorRenderer = new GpuAttractorRenderer(_gl, _pipeline);
            _mandelbulbRenderer = new GpuMandelbulbRenderer(_gl, _pipeline);
            _qjuliaRenderer = new GpuQuaternionJuliaRenderer(_gl, _pipeline);
            _rotboxRenderer = new GpuRotBoxRenderer(_gl, _pipeline);
            _hybridRenderer = new GpuHybridRenderer(_gl, _pipeline);
            _qjboxRenderer = new GpuQJBoxRenderer(_gl, _pipeline);
            _mengerRenderer = new GpuMengerRenderer(_gl, _pipeline);
            _bicomplexRenderer = new GpuBicomplexRenderer(_gl, _pipeline);
            _apollonianRenderer = new GpuApollonianRenderer(_gl, _pipeline);
            _phoenixRenderer = new GpuPhoenixRenderer(_gl, _pipeline);
            _biomorphRenderer = new GpuBiomorphRenderer(_gl, _pipeline);
            _moselyRenderer = new GpuMoselyRenderer(_gl, _pipeline);
            _pk4dRenderer = new GpuPseudoKleinian4DRenderer(_gl, _pipeline);
            _riemannRenderer = new GpuRiemannSphereRenderer(_gl, _pipeline);
            _mandalayRenderer = new GpuMandalayRenderer(_gl, _pipeline);
            _anisoRenderer = new GpuAnisotropicRenderer(_gl, _pipeline);
            _orbitHybridRenderer = new GpuOrbitHybridRenderer(_gl, _pipeline);
            _burningShipRenderer = new GpuBurningShipRenderer(_gl, _pipeline);
            _deepPipeline = new DeepZoomPipeline(_gl);

            _texture = _gl.GenTexture();
            _gl.BindTexture(GlConst.Texture2D, _texture);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMinFilter, (int)GlConst.Linear);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMagFilter, (int)GlConst.Linear);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapS, (int)GlConst.ClampToEdge);
            _gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapT, (int)GlConst.ClampToEdge);
            _gl.BindTexture(GlConst.Texture2D, 0);

            _vao = _gl.GenVertexArray();
            _blitProgram = _gl.CreateGraphicsProgram(BlitVertexSrc, BlitFragmentSrc);
            _samplerLocation = _gl.GetUniformLocation(_blitProgram, "uTex");
            _ready = true;
        }
        catch (Exception ex)
        {
            _ready = false;
            Status($"init failed: {ex.Message}");
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_ready || _gl == null || _pipeline == null || _boxRenderer == null || _kifsRenderer == null || _kleinianRenderer == null || _attractorRenderer == null || _mandelbulbRenderer == null || _qjuliaRenderer == null || _rotboxRenderer == null || _hybridRenderer == null || _qjboxRenderer == null || _mengerRenderer == null || _bicomplexRenderer == null || _apollonianRenderer == null || _phoenixRenderer == null || _biomorphRenderer == null || _moselyRenderer == null || _pk4dRenderer == null || _riemannRenderer == null || _mandalayRenderer == null || _anisoRenderer == null || _orbitHybridRenderer == null || _burningShipRenderer == null || _deepPipeline == null)
        {
            _gl?.BindFramebuffer(GlConst.Framebuffer, (uint)fb);
            _gl?.ClearColor(0.1f, 0.1f, 0.1f, 1f);
            _gl?.Clear(GlConst.ColorBufferBit);
            return;
        }

        // Hero render: the GL context is current here, so do the high-res tiled
        // render now, save it, and fall through to a normal preview render so the
        // on-screen view (and the preview-size buffers) are restored afterward.
        if (_heroPending && _heroPath != null)
        {
            try
            {
                SkiaSharp.SKBitmap bmp = RenderActiveTo(_heroWidth, _heroHeight);
                Parsec.Rendering.Output.ImageOutput.SavePng(bmp, _heroPath);
                bmp.Dispose();

                string savedTo = _heroPath;
                Dispatcher.UIThread.Post(() =>
                    HeroRenderComplete?.Invoke($"Saved {_heroWidth}x{_heroHeight} render to {savedTo}"));
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Dispatcher.UIThread.Post(() =>
                    HeroRenderComplete?.Invoke($"Hero render failed: {msg}"));
            }
            finally
            {
                _heroPending = false;
                _heroPath = null;
                _dirty = true;   // restore the preview-size view next
            }
        }

        // Animation batch render: run the whole frame loop here (GL context is
        // current). Synchronous -- the UI freezes until done (fine for a test).
        if (_animPending && _animDir != null && _animApplyAtTime != null)
        {
            int total = _animFrameCount;
            string dir = _animDir;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                for (int frame = 0; frame < total; frame++)
                {
                    double t = frame / _animFps;
                    _animApplyAtTime(t);                       // set live params for this time
                    using var bmp = RenderActiveTo(_animWidth, _animHeight);
                    string path = System.IO.Path.Combine(dir, $"frame_{frame:D5}.png");
                    Parsec.Rendering.Output.ImageOutput.SavePng(bmp, path);

                    int done = frame + 1;
                    if (done == total || done % 5 == 0)
                        Dispatcher.UIThread.Post(() => AnimationProgress?.Invoke(done, total));
                }
                Dispatcher.UIThread.Post(() =>
                    AnimationRenderComplete?.Invoke($"Rendered {total} frames to {dir}"));
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Dispatcher.UIThread.Post(() =>
                    AnimationRenderComplete?.Invoke($"Animation render failed: {msg}"));
            }
            finally
            {
                _animPending = false;
                _animDir = null;
                _animApplyAtTime = null;
                _dirty = true;
            }
        }

        // Attractor (re)generation: the expensive integrate + hash + SSBO upload.
        // Only runs when generation params changed (the "Generate" action), never
        // per frame. The GL context is current here, which SetAttractor needs.
        if (ActiveType == FractalType.Attractor && _attractorNeedsRegen)
        {
            try
            {
                var ap = Attractor.ToParams();
                var traj = Parsec.Core.Attractors.ThomasAttractor.Generate(ap);
                _attractorHash = Parsec.Core.Attractors.AttractorHash.Build(traj, gridSize: 96);
                _attractorRenderer.SetAttractor(_attractorHash);

                // Frame the camera to the cloud bounds on (re)generation so the
                // new shape is in view; the user can then fly around freely.
                var lo = _attractorHash.BoundsMin;
                var hi = _attractorHash.BoundsMax;
                var center = (lo + hi) * 0.5f;
                float span = (hi - lo).Length();
                _cam.Position = center + new Vector3(0.2f, 0.35f, 1.0f) * span * 0.8f;
                _cam.LookAt(center);

                _attractorNeedsRegen = false;
                _dirty = true;
                Status($"Generated attractor: {traj.Count} points");
            }
            catch (Exception ex)
            {
                _attractorNeedsRegen = false;
                Status($"Attractor generate failed: {ex.Message}");
            }
        }

        if (_dirty && !(ActiveType == FractalType.Attractor && _attractorHash == null))
        {
            // Consume the refinement flag: a scheduled DOF refinement pass
            // renders with an accumulation offset; anything else (param edit,
            // camera move, fractal switch) starts a fresh 1-sample base frame.
            _previewSampleOffset = _expectRefine ? _previewAccum : 0;
            _expectRefine = false;

            var camera = _cam.ToCamera(PreviewWidth, PreviewHeight);
            // Orb lights ride shared pipeline state (binding 10), so one call
            // covers every renderer in the switch below. DeepZoom ignores it.
            _pipeline.SetOrbs(Orbs.ToLights());
            // 3D raymarch previews use the fixed preview buffer; deep-zoom renders
            // at native resolution so the 2D filigree stays crisp at 1:1 -- but
            // while interacting it renders at a reduced scale for responsiveness
            // and snaps to native once settled (see OnMoveTick).
            int rw = PreviewWidth, rh = PreviewHeight;
            if (ActiveType == FractalType.DeepZoom)
            {
                var (nw, nh) = GetPixelSize();
                // Resolution follows the interaction FLAG, not the wall-clock: a
                // render that ran late must still honor the scroll that requested
                // it as low-res, or it spirals into back-to-back native renders.
                if (_deepInteracting)
                {
                    // Spend a fixed compute budget on iterations first, then on
                    // resolution. At depth the structure needs many thousands of
                    // iterations to appear, so try the full depth-scaled count and
                    // let resolution shrink to fit the budget; only if that would
                    // drop below the floor do we pin resolution and cap iterations.
                    int effIter = _deepView.IterationsForDepth();
                    double native = Math.Max(1, (double)nw * nh);
                    _deepPreviewIter = effIter;
                    double s = Math.Sqrt((DeepInteractiveCost / _deepPreviewIter) / native);
                    if (s < DeepPreviewMinScale)
                    {
                        s = DeepPreviewMinScale;
                        _deepPreviewIter = Math.Max(1000,
                            (int)(DeepInteractiveCost / (native * s * s)));
                    }
                    s = Math.Min(s, 1.0);
                    rw = Math.Max(1, (int)(nw * s));
                    rh = Math.Max(1, (int)(nh * s));
                }
                else
                {
                    rw = nw; rh = nh;
                }
            }
            uint[] pixels = ActiveType switch
            {
                FractalType.DeepZoom => _deepPipeline.Render(_deepView,
                    rw, rh, Palette.ToParams(),
                    new Color(0.02f, 0.03f, 0.07f), heroSamples: 1, tileRows: 64,
                    interactive: _deepInteracting, interactiveIter: _deepPreviewIter),
                FractalType.Mandelbulb => _mandelbulbRenderer.RenderToBuffer(Mandelbulb.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(210, 175, 140),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.BurningShip => _burningShipRenderer.RenderToBuffer(BurningShip.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(225, 140, 90),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.QuaternionJulia => _qjuliaRenderer.RenderToBuffer(QuaternionJulia.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(210, 180, 150),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.RotBox => _rotboxRenderer.RenderToBuffer(RotBox.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(190, 175, 155),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Hybrid => _hybridRenderer.RenderToBuffer(Hybrid.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(190, 170, 145),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.QJBox => _qjboxRenderer.RenderToBuffer(QJBox.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(195, 170, 145),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Menger => _mengerRenderer.RenderToBuffer(Menger.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(195, 170, 145),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Bicomplex => _bicomplexRenderer.RenderToBuffer(Bicomplex.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(200, 175, 150),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Apollonian => _apollonianRenderer.RenderToBuffer(Apollonian.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(200, 175, 150),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Phoenix => _phoenixRenderer.RenderToBuffer(Phoenix.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(210, 180, 150),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Biomorph => _biomorphRenderer.RenderToBuffer(Biomorph.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(220, 180, 140),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Attractor => _attractorRenderer.RenderToBuffer(Attractor.ToRenderParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(230, 120, 70),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Kleinian => _kleinianRenderer.RenderToBuffer(Kleinian.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(150, 125, 100),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Kifs => _kifsRenderer.RenderToBuffer(Kifs.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(150, 125, 100),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Mosely => _moselyRenderer.RenderToBuffer(Mosely.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(170, 150, 130),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.PseudoKleinian4D => _pk4dRenderer.RenderToBuffer(PseudoKleinian4D.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(165, 150, 130),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.RiemannSphere => _riemannRenderer.RenderToBuffer(RiemannSphere.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(205, 160, 135),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Mandalay => _mandalayRenderer.RenderToBuffer(Mandalay.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(175, 165, 150),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Anisotropic => _anisoRenderer.RenderToBuffer(Anisotropic.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(160, 158, 170),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.OrbitHybrid => _orbitHybridRenderer.RenderToBuffer(OrbitHybrid.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(195, 170, 135),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                FractalType.Mandelbox => _boxRenderer.RenderToBuffer(Mandelbox.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(170, 150, 130),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
                _ => _boxRenderer.RenderToBuffer(Fractal.ToParams(), camera,
                    PreviewWidth, PreviewHeight, PreviewSettings(),
                    background: new Color(0.02f, 0.03f, 0.07f),
                    surface: Color.Rgb(150, 125, 100),
                    lightDirection: Light.ToDirection(),
                    palette: Palette.ToParams(),
                    tileRows: 64),
            };

            _gl.BindTexture(GlConst.Texture2D, _texture);
            unsafe
            {
                fixed (uint* p = pixels)
                {
                    _gl.TexImage2D(GlConst.Texture2D, 0, (int)GlConst.Rgba8,
                        rw, rh, 0,
                        GlConst.Rgba, GlConst.UnsignedByte, (IntPtr)p);
                }
            }
            _texW = rw; _texH = rh;
            _dirty = false;
            // The accumulator now holds offset+1 samples of this frame (deep
            // zoom has its own pipeline and never refines).
            _previewAccum = ActiveType == FractalType.DeepZoom ? 0 : _previewSampleOffset + 1;
            bool atMaxDepth = ActiveType == FractalType.DeepZoom
                && _deepView.Radius <= DeepZoomView.MinRadius * 1.05;
            Status(ActiveType == FractalType.DeepZoom
                ? $"Deep Zoom 2D · {(_deepView.Formula switch { 1 => "Prospector", 2 => "Julia", 3 => "Burning Ship", _ => "Mandelbrot" })} · radius {_deepView.Radius:e2}{(atMaxDepth ? " · max depth" : "")} · {rw}x{rh} · drag pan · scroll zoom"
                : $"pos ({_cam.Position.X:F2}, {_cam.Position.Y:F2}, {_cam.Position.Z:F2})  ·  WASD+QE move · drag to look");
        }

        // Progressive DOF: while the camera is idle and the aperture is open,
        // schedule another pass through the render path above with an
        // accumulation offset -- the preview refines from a sharp pinhole to
        // converged thin-lens blur, one sample per frame.
        if (!_dirty && CanRefineDofPreview())
        {
            _expectRefine = true;
            _dirty = true;
            RequestNextFrameRendering();
        }

        _gl.BindFramebuffer(GlConst.Framebuffer, (uint)fb);
        var size = GetPixelSize();
        // Clear the whole control to the surround color first.
        _gl.Viewport(0, 0, size.w, size.h);
        _gl.ClearColor(0.02f, 0.03f, 0.07f, 1f);  // match render bg (dark blue)
        _gl.Clear(GlConst.ColorBufferBit);

        // Letterbox: fit the preview's aspect ratio inside the control, centered,
        // so the fractal isn't stretched when the view area isn't 4:3.
        float previewAspect = (float)_texW / _texH;
        float viewAspect = (float)size.w / size.h;
        int vpW, vpH, vpX, vpY;
        if (viewAspect > previewAspect)
        {
            // Control is wider than preview: bars on left/right.
            vpH = size.h;
            vpW = (int)(size.h * previewAspect);
            vpX = (size.w - vpW) / 2;
            vpY = 0;
        }
        else
        {
            // Control is taller than preview: bars on top/bottom.
            vpW = size.w;
            vpH = (int)(size.w / previewAspect);
            vpX = 0;
            vpY = (size.h - vpH) / 2;
        }
        _gl.Viewport(vpX, vpY, vpW, vpH);

        _gl.UseProgram(_blitProgram);
        _gl.ActiveTexture(GlConst.Texture0);
        _gl.BindTexture(GlConst.Texture2D, _texture);
        _gl.Uniform1i(_samplerLocation, 0);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(GlConst.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _moveTimer?.Stop();
        _moveTimer = null;
        if (_gl == null) return;
        if (_texture != 0) _gl.DeleteTexture(_texture);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_blitProgram != 0) _gl.DeleteProgram(_blitProgram);
        _boxRenderer?.Dispose();
        _kifsRenderer?.Dispose();
        _kleinianRenderer?.Dispose();
        _attractorRenderer?.Dispose();
        _mandelbulbRenderer?.Dispose();
        _qjuliaRenderer?.Dispose();
        _rotboxRenderer?.Dispose();
        _hybridRenderer?.Dispose();
        _qjboxRenderer?.Dispose();
        _mengerRenderer?.Dispose();
        _bicomplexRenderer?.Dispose();
        _apollonianRenderer?.Dispose();
        _phoenixRenderer?.Dispose();
        _biomorphRenderer?.Dispose();
        _moselyRenderer?.Dispose();
        _pk4dRenderer?.Dispose();
        _riemannRenderer?.Dispose();
        _mandalayRenderer?.Dispose();
        _anisoRenderer?.Dispose();
        _orbitHybridRenderer?.Dispose();
        _burningShipRenderer?.Dispose();
        _deepPipeline?.Dispose();
        _pipeline?.Dispose();
        _texture = _vao = _blitProgram = 0;
        _boxRenderer = null;
        _kifsRenderer = null;
        _kleinianRenderer = null;
        _attractorRenderer = null;
        _mandelbulbRenderer = null;
        _qjuliaRenderer = null;
        _rotboxRenderer = null;
        _hybridRenderer = null;
        _qjboxRenderer = null;
        _mengerRenderer = null;
        _bicomplexRenderer = null;
        _apollonianRenderer = null;
        _phoenixRenderer = null;
        _biomorphRenderer = null;
        _moselyRenderer = null;
        _pk4dRenderer = null;
        _riemannRenderer = null;
        _mandalayRenderer = null;
        _anisoRenderer = null;
        _orbitHybridRenderer = null;
        _burningShipRenderer = null;
        _deepPipeline = null;
        _pipeline = null;
        _gl = null;
        _ready = false;
    }

    private (int w, int h) GetPixelSize()
    {
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling));
        int h = Math.Max(1, (int)(Bounds.Height * scaling));
        return (w, h);
    }

    private RaymarchSettings PreviewSettings() => WithSharedLook(new(
        MaxSteps: 160, HitEpsilon: 1.5e-3f, MaxDistance: 40f, NormalEpsilon: 2e-3f,
        EnableSoftShadows: false, ShadowSteps: 0, ShadowSoftness: 12f,
        EnableAmbientOcclusion: true, AOSamples: 4, AOStepDistance: 0.05f, AOIntensity: 1.0f,
        HeroSamples: 1,
        EnableReflections: Reflection.Bounces > 0,
        ReflectionBounces: Reflection.Bounces,
        Gloss: Reflection.Gloss,
        F0: Reflection.F0,
        LightIntensity: Light.Intensity,
        FocusDistance: Dof.FocusDistance,
        Aperture: Dof.Aperture,
        SampleOffset: _previewSampleOffset));  // progressive DOF refinement (0 = fresh frame)

    /// <summary>Layer the shared look state (skybox, floor, fractal rotation)
    /// onto a settings record — same values for preview and hero, so the
    /// composition matches.</summary>
    private RaymarchSettings WithSharedLook(RaymarchSettings s) => s with
    {
        SkyboxEnable = Env.SkyEnable >= 1,
        SkyZenith = new Vector3(Env.ZenithR, Env.ZenithG, Env.ZenithB),
        SkyHorizon = new Vector3(Env.HorizonR, Env.HorizonG, Env.HorizonB),
        SkyGround = new Vector3(Env.GroundR, Env.GroundG, Env.GroundB),
        SunIntensity = Env.SunIntensity,
        SunSharpness = Env.SunSharpness,
        FloorEnable = Env.FloorEnable >= 1,
        FloorHeight = Env.FloorHeight,
        FloorColor = new Vector3(Env.FloorR, Env.FloorG, Env.FloorB),
        FloorReflect = Env.FloorReflect,
        FloorCheckerScale = Env.FloorChecker,
        FractalEulerRadians = Rotation.ToEulerRadians(),
    };

    // High quality for the hero still: more march steps, finer hit/normal
    // epsilon, soft shadows on, more AO samples. The tiled path keeps it
    // TDR-safe even at 4K (each tile is width x 32 px with a Finish() between).
    /// <summary>
    /// Render the active fractal at the given resolution from the current camera
    /// and live params, using hero-quality settings. Shared by the single hero
    /// render and the animation batch. Must be called with the GL context current.
    /// </summary>
    private SkiaSharp.SKBitmap RenderActiveTo(int width, int height)
    {
        // Hero renders reuse the pipeline's accumulator at a different size;
        // whatever the preview had accumulated is gone. Restart refinement
        // from the base frame the callers queue up afterwards (_dirty = true).
        _previewAccum = 0;
        var cam = _cam.ToCamera(width, height);
        var bg = new Color(0.02f, 0.03f, 0.07f);
        var light = Light.ToDirection();
        var pal = Palette.ToParams();
        _pipeline!.SetOrbs(Orbs.ToLights());
        return ActiveType switch
        {
            FractalType.DeepZoom => DeepZoomBitmap(width, height),
            FractalType.Mandelbulb => _mandelbulbRenderer!.Render(Mandelbulb.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(210, 175, 140), light, pal, tileRows: 32),
            FractalType.BurningShip => _burningShipRenderer!.Render(BurningShip.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(225, 140, 90), light, pal, tileRows: 32),
            FractalType.QuaternionJulia => _qjuliaRenderer!.Render(QuaternionJulia.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(210, 180, 150), light, pal, tileRows: 32),
            FractalType.RotBox => _rotboxRenderer!.Render(RotBox.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(190, 175, 155), light, pal, tileRows: 32),
            FractalType.Hybrid => _hybridRenderer!.Render(Hybrid.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(190, 170, 145), light, pal, tileRows: 32),
            FractalType.QJBox => _qjboxRenderer!.Render(QJBox.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(195, 170, 145), light, pal, tileRows: 32),
            FractalType.Menger => _mengerRenderer!.Render(Menger.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(195, 170, 145), light, pal, tileRows: 32),
            FractalType.Bicomplex => _bicomplexRenderer!.Render(Bicomplex.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(200, 175, 150), light, pal, tileRows: 32),
            FractalType.Apollonian => _apollonianRenderer!.Render(Apollonian.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(200, 175, 150), light, pal, tileRows: 32),
            FractalType.Phoenix => _phoenixRenderer!.Render(Phoenix.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(210, 180, 150), light, pal, tileRows: 32),
            FractalType.Biomorph => _biomorphRenderer!.Render(Biomorph.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(220, 180, 140), light, pal, tileRows: 32),
            FractalType.Attractor => _attractorRenderer!.Render(Attractor.ToRenderParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(230, 120, 70), light, pal, tileRows: 32),
            FractalType.Kleinian => _kleinianRenderer!.Render(Kleinian.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, tileRows: 32),
            FractalType.Kifs => _kifsRenderer!.Render(Kifs.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, tileRows: 32),
            FractalType.Mosely => _moselyRenderer!.Render(Mosely.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(170, 150, 130), light, pal, tileRows: 32),
            FractalType.PseudoKleinian4D => _pk4dRenderer!.Render(PseudoKleinian4D.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(165, 150, 130), light, pal, tileRows: 32),
            FractalType.RiemannSphere => _riemannRenderer!.Render(RiemannSphere.ToParams(), cam,
                width, height, HeroSettings(1.5e-3f), bg, Color.Rgb(205, 160, 135), light, pal, tileRows: 32),
            FractalType.Mandalay => _mandalayRenderer!.Render(Mandalay.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(175, 165, 150), light, pal, tileRows: 32),
            FractalType.Anisotropic => _anisoRenderer!.Render(Anisotropic.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(160, 158, 170), light, pal, tileRows: 32),
            FractalType.OrbitHybrid => _orbitHybridRenderer!.Render(OrbitHybrid.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(195, 170, 135), light, pal, tileRows: 32),
            FractalType.Mandelbox => _boxRenderer!.Render(Mandelbox.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(170, 150, 130), light, pal, tileRows: 32),
            _ => _boxRenderer!.Render(Fractal.ToParams(), cam,
                width, height, HeroSettings(), bg, Color.Rgb(150, 125, 100), light, pal, tileRows: 32),
        };
    }

    // Deep-zoom renders straight to a packed RGBA8 buffer (no SKBitmap-returning
    // renderer), so wrap it the same way GpuMandelbulbRenderer.Render does.
    private SkiaSharp.SKBitmap DeepZoomBitmap(int width, int height)
    {
        uint[] pixels = _deepPipeline!.Render(_deepView, width, height,
            Palette.ToParams(), new Color(0.02f, 0.03f, 0.07f),
            heroSamples: Math.Max(1, HeroSampleCount), tileRows: 32);
        var info = new SkiaSharp.SKImageInfo(width, height,
            SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        var bitmap = new SkiaSharp.SKBitmap(info);
        var bytes = new byte[pixels.Length * 4];
        System.Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    private RaymarchSettings HeroSettings() => HeroSettings(6e-7f);

    // Hero settings with an explicit hit/normal epsilon. Exact-DE chapters use the
    // razor-fine default (6e-7). Chapters whose DE is approximate and spiky need a
    // much coarser epsilon: the Riemann Sphere's effective power reaches ~72, so the
    // orbit escapes almost discontinuously and the surface has no sub-micron shell to
    // home into -- at 6e-7 the marcher oversteps and misses it (full in the coarse
    // preview, sparse at hero). A coarse epsilon catches the same surface the preview
    // does, and the DE can't resolve finer than that anyway.
    private RaymarchSettings HeroSettings(float eps) => WithSharedLook(new(
        MaxSteps: 400, HitEpsilon: eps, MaxDistance: 50f, NormalEpsilon: eps,
        EnableSoftShadows: true, ShadowSteps: 64, ShadowSoftness: 14f,
        EnableAmbientOcclusion: true, AOSamples: 6, AOStepDistance: 0.04f, AOIntensity: 1.0f,
        HeroSamples: Math.Max(1, HeroSampleCount),
        EnableReflections: Reflection.Bounces > 0,
        ReflectionBounces: Reflection.Bounces,
        Gloss: Reflection.Gloss,
        F0: Reflection.F0,
        LightIntensity: Light.Intensity,
        FocusDistance: Dof.FocusDistance,
        Aperture: Dof.Aperture));

    private const string BlitVertexSrc = @"#version 430 core
out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    private const string BlitFragmentSrc = @"#version 430 core
in vec2 vUv;
out vec4 fragColor;
uniform sampler2D uTex;
void main() {
    fragColor = texture(uTex, vec2(vUv.x, 1.0 - vUv.y));
}";
}
