using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Parsec.App;

public partial class MainWindow : Window
{
    private FractalView? _view;
    private Border? _panelHost;
    private Border? _bankHost;
    private Button? _generateButton;

    // Animation timeline state.
    private KeyframeBank? _bank;
    private Timeline? _timeline;
    private ParamSchema? _activeSchema;   // shared by panel + timeline this fractal

    // Playback.
    private DispatcherTimer? _playTimer;
    private DateTime _playStart;
    private int _playFromIndex;
    private bool _playing;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _view = this.FindControl<FractalView>("FractalView");
        _panelHost = this.FindControl<Border>("PanelHost");
        _bankHost = this.FindControl<Border>("BankHost");
        var status = this.FindControl<TextBlock>("StatusText");
        var selector = this.FindControl<ComboBox>("FractalSelector");

        if (_view != null && status != null)
            _view.StatusChanged += text => status.Text = text;

        if (selector != null)
        {
            selector.SelectedIndex = 0;   // KIFS, the default ActiveType
            selector.SelectionChanged += OnFractalChanged;
        }

        var heroButton = this.FindControl<Button>("HeroButton");
        if (heroButton != null)
            heroButton.Click += OnHeroClick;

        var formulaSelector = this.FindControl<ComboBox>("FormulaSelector");
        if (formulaSelector != null)
            formulaSelector.SelectionChanged += (_, _) =>
            {
                // Dropdown index == formula int (Mandelbrot 0, Prospector 1,
                // Julia 2, Burning Ship 3).
                if (_view != null && formulaSelector.SelectedIndex >= 0)
                    _view.SetDeepFormula(formulaSelector.SelectedIndex);
            };

        var heroSamplesSelector = this.FindControl<ComboBox>("HeroSamplesSelector");
        if (heroSamplesSelector != null)
            heroSamplesSelector.SelectionChanged += (_, _) =>
            {
                if (_view != null)
                    _view.HeroSampleCount = heroSamplesSelector.SelectedIndex switch
                    {
                        0 => 1, 1 => 4, 2 => 9, 3 => 16, _ => 1,
                    };
            };

        _generateButton = this.FindControl<Button>("GenerateButton");
        if (_generateButton != null)
            _generateButton.Click += OnGenerateClick;

        var testRenderButton = this.FindControl<Button>("TestRenderButton");
        if (testRenderButton != null)
            testRenderButton.Click += OnTestRenderClick;

        var saveAnimButton = this.FindControl<Button>("SaveAnimButton");
        if (saveAnimButton != null)
            saveAnimButton.Click += OnSaveAnimClick;

        var loadAnimButton = this.FindControl<Button>("LoadAnimButton");
        if (loadAnimButton != null)
            loadAnimButton.Click += OnLoadAnimClick;

        var addOrbButton = this.FindControl<Button>("AddOrbButton");
        if (addOrbButton != null)
            addOrbButton.Click += OnAddOrbClick;

        var removeOrbButton = this.FindControl<Button>("RemoveOrbButton");
        if (removeOrbButton != null)
            removeOrbButton.Click += OnRemoveOrbClick;

        if (_view != null && status != null)
        {
            _view.HeroRenderComplete += text => status.Text = text;
            _view.AnimationProgress += (f, n) => status.Text = $"Rendering frame {f}/{n}...";
            _view.AnimationRenderComplete += text =>
            {
                status.Text = text;
                RefreshPanelValues();   // resync sliders after the batch
            };
        }

        // Build the keyframe bank once; it is re-bound to a new timeline whenever
        // the active fractal changes.
        _bank = new KeyframeBank();
        _bank.CellSelected += OnCellSelected;
        _bank.CellCleared += OnCellCleared;
        if (_bankHost != null) _bankHost.Child = _bank;

        // Space toggles playback. Tunneling handler so it works regardless of
        // focus (the GL view grabs keyboard focus for fly controls).
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        RebuildForActiveFractal();
    }

    // ----------------------------------------------------------- hero / generate
    // 4:3 hero resolution tiers, matching the preview's aspect (640×480) so the
    // hero framing is WYSIWYG. Width-anchored to the DCI 2K/4K/8K convention.
    private (int Width, int Height) HeroResolution() =>
        (this.FindControl<ComboBox>("ResolutionSelector")?.SelectedIndex ?? 1) switch
        {
            0 => (2048, 1536),   // 2K
            1 => (4096, 3072),   // 4K
            2 => (8192, 6144),   // 8K
            3 => (12288, 9216), // 12k
            _ => (4096, 3072),
        };

    private void OnHeroClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        string fractal = _view.ActiveType switch
        {
            FractalType.Kleinian => "kleinian",
            FractalType.PseudoKleinian4D => "pk4d",
            FractalType.RiemannSphere => "riemann",
            FractalType.Mandalay => "mandalay",
            FractalType.Anisotropic => "anisotropic",
            FractalType.OrbitHybrid => "orbithybrid",
            FractalType.BurningShip => "burningship",
            FractalType.AmazingBox => "amazingbox",
            FractalType.Mandelbox => "mandelbox",
            FractalType.Attractor => "thomas",
            FractalType.Mandelbulb => "mandelbulb",
            FractalType.QuaternionJulia => "qjulia",
            FractalType.RotBox => "rotbox",
            FractalType.Hybrid => "hybrid",
            FractalType.QJBox => "qjbox",
            FractalType.Menger => "menger",
            FractalType.Bicomplex => "bicomplex",
            FractalType.Apollonian => "apollonian",
            FractalType.Phoenix => "phoenix",
            FractalType.Biomorph => "biomorph",
            FractalType.Mosely => "mosely",
            _ => "kifs",
        };
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Parsec");
        string path = System.IO.Path.Combine(dir, $"parsec_{fractal}_{stamp}.png");

        var (w, h) = HeroResolution();
        SetStatus($"Rendering {w}x{h}... (window may pause)");
        _view.RequestHeroRender(path, w, h);
    }

    private void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        SetStatus("Generating attractor... (may pause briefly)");
        _view.RequestAttractorRegen();
    }

    // ----------------------------------------------------------- orb lights
    private void OnAddOrbClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        StopPlayback();
        if (!_view.AddOrbInView())
        {
            SetStatus($"Orb limit reached ({OrbState.MaxOrbs}).");
            return;
        }
        // Orb sliders join the shared schema, so the panel and timeline must
        // rebuild (the descriptor count changed -- keyframes reset, same as
        // switching fractals).
        RebuildForActiveFractal();
        SetStatus($"Orb {_view.Orbs.Count} placed in view — tune it under \"Orb {_view.Orbs.Count}\" in the panel.");
    }

    private void OnRemoveOrbClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        StopPlayback();
        if (!_view.RemoveLastOrb())
        {
            SetStatus("No orbs to remove.");
            return;
        }
        RebuildForActiveFractal();
        SetStatus($"Orb removed — {_view.Orbs.Count} remaining.");
    }

    // ----------------------------------------------------------- fractal switch
    private void OnFractalChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_view == null || sender is not ComboBox cb) return;
        StopPlayback();
        var type = cb.SelectedIndex switch
        {
            1 => FractalType.AmazingBox,
            2 => FractalType.Mandelbox,
            3 => FractalType.Kleinian,
            4 => FractalType.PseudoKleinian4D,
            5 => FractalType.Attractor,
            6 => FractalType.Mandelbulb,
            7 => FractalType.QuaternionJulia,
            8 => FractalType.RotBox,
            9 => FractalType.Hybrid,
            10 => FractalType.QJBox,
            11 => FractalType.Menger,
            12 => FractalType.Bicomplex,
            13 => FractalType.Apollonian,
            14 => FractalType.Phoenix,
            15 => FractalType.Biomorph,
            16 => FractalType.Mosely,
            17 => FractalType.RiemannSphere,
            18 => FractalType.Mandalay,
            19 => FractalType.Anisotropic,
            20 => FractalType.OrbitHybrid,
            21 => FractalType.BurningShip,
            22 => FractalType.DeepZoom,
            _ => FractalType.Kifs,
        };
        _view.SetActiveType(type);
        if (_generateButton != null)
            _generateButton.IsVisible = type == FractalType.Attractor;

        // The FORMULA dropdown belongs to the Deep Zoom 2D chapter only.
        bool deep = type == FractalType.DeepZoom;
        var formulaSelector = this.FindControl<ComboBox>("FormulaSelector");
        var formulaLabel = this.FindControl<TextBlock>("FormulaLabel");
        if (formulaSelector != null)
        {
            formulaSelector.IsVisible = deep;
            if (deep) formulaSelector.SelectedIndex = _view.DeepFormula;   // index == formula
        }
        if (formulaLabel != null) formulaLabel.IsVisible = deep;

        // Orbs are a 3D raymarch feature; the 2D deep-zoom mode has no scene
        // to place them in.
        var addOrbButton = this.FindControl<Button>("AddOrbButton");
        var removeOrbButton = this.FindControl<Button>("RemoveOrbButton");
        if (addOrbButton != null) addOrbButton.IsEnabled = !deep;
        if (removeOrbButton != null) removeOrbButton.IsEnabled = !deep;

        RebuildForActiveFractal();   // keyframes are per-fractal; reset on switch
    }

    private void RebuildPanel()
    {
        if (_view == null || _panelHost == null || _activeSchema == null) return;
        var panel = new ParameterPanel(_activeSchema);
        panel.OnChanged += OnParamChanged;
        _panelHost.Child = panel;
    }

    /// <summary>
    /// Build one schema instance for the active fractal and bind BOTH the panel
    /// and the timeline to it, so they share the exact same descriptor objects
    /// (capture/apply and the displayed sliders are guaranteed consistent).
    /// </summary>
    private void RebuildForActiveFractal()
    {
        if (_view == null) return;
        _activeSchema = _view.BuildActiveSchema();
        RebuildPanel();
        RebuildTimeline();
    }

    // ----------------------------------------------------------- timeline / bank
    private void RebuildTimeline()
    {
        if (_view == null || _bank == null || _activeSchema == null) return;

        // Animation is disabled for the attractor: interpolating its generation
        // params is meaningless (tweening seed count / drift phase across a
        // chaotic regime morphs between unrelated attractors), and every frame
        // would imply an expensive regenerate.
        bool animatable = _view.ActiveType != FractalType.Attractor;
        _bank.SetEnabled(animatable);

        if (!animatable)
        {
            _timeline = null;
            return;
        }

        // Bind the timeline to the SAME descriptor instances the panel uses (the
        // shared _activeSchema), so capture/apply read and write the live state
        // consistently and Values[] stays positionally aligned for its lifetime.
        _timeline = new Timeline(_activeSchema.Parameters, KindFor);
        // Keyframe values align to descriptors by position, and orbs add
        // descriptors -- so a saved animation only fits the same orb count.
        // (Suffix only when orbs exist, so pre-orb saves keep loading.)
        _timeline.SchemaTag = _view.Orbs.Count > 0
            ? $"{_view.ActiveType}+orb{_view.Orbs.Count}"
            : _view.ActiveType.ToString();
        _bank.Refresh(_timeline);
    }

    // Palette phase is cyclic with period 1.0 (the cosine palette is periodic
    // in phase with period 1, and the sliders run 0..1); everything else is
    // linear for the MVP. AngularWrap (2*pi) is reserved for radian-valued
    // params like camera orientation in phase 2.
    private static InterpKind KindFor(ParamDescriptor d) =>
        d.Label.Contains("phase", StringComparison.OrdinalIgnoreCase)
            ? InterpKind.UnitWrap : InterpKind.Linear;

    private void OnCellSelected(int index)
    {
        if (_timeline == null) return;
        StopPlayback();
        _timeline.Select(index);
        _bank?.Refresh(_timeline);
        // If the selected slot is already set, restore its values so the user
        // sees that keyframe's look (and edits start from it).
        if (_timeline.IsSet(index))
        {
            _timeline.ApplySlot(index);
            RefreshPanelValues();
            _view?.MarkDirty();
        }
    }

    private void OnCellCleared(int index)
    {
        if (_timeline == null) return;
        StopPlayback();
        if (_timeline.Clear(index))
            _bank?.Refresh(_timeline);
    }

    private void OnParamChanged()
    {
        // A slider moved. First move while a not-yet-set slot is selected sets
        // that keyframe (slot 0 is always set). Editing an already-set slot
        // updates its snapshot. Don't capture during playback (the interpolator
        // is the one writing values then).
        if (_timeline != null && !_playing)
        {
            int sel = _timeline.SelectedIndex;
            bool wasSet = _timeline.IsSet(sel);
            _timeline.CaptureInto(sel);
            if (!wasSet) _bank?.Refresh(_timeline);
        }
        _view?.MarkDirty();
    }

    // ----------------------------------------------------------- playback
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            if (_timeline == null) return;
            if (_playing) StopPlayback();
            else StartPlayback();
            e.Handled = true;
        }
    }

    private void StartPlayback()
    {
        if (_timeline == null || _view == null) return;
        if (_timeline.LastSetIndex() <= _timeline.SelectedIndex)
        {
            SetStatus("Playback: need a later keyframe to play toward.");
            return;
        }
        _playFromIndex = _timeline.SelectedIndex;
        _playStart = DateTime.UtcNow;
        _playing = true;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playTimer.Tick += OnPlayTick;
        _playTimer.Start();
        SetStatus("Playing... (space to stop)");
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_timeline == null || _view == null) { StopPlayback(); return; }
        double t = (DateTime.UtcNow - _playStart).TotalSeconds;
        bool more = _timeline.ApplyAtTime(_playFromIndex, t);
        _view.MarkDirty();
        if (!more)
        {
            StopPlayback();
            RefreshPanelValues();   // snap sliders to final keyframe values
            SetStatus("Playback complete.");
        }
    }

    private void StopPlayback()
    {
        if (_playTimer != null)
        {
            _playTimer.Stop();
            _playTimer.Tick -= OnPlayTick;
            _playTimer = null;
        }
        _playing = false;
    }

    // Rebuild the panel so slider positions reflect current live values (after
    // restoring/applying a keyframe). Cheap and simple for the MVP.
    private void RefreshPanelValues() => RebuildPanel();

    // ----------------------------------------------------------- animation I/O
    private const double RenderFps = 30.0;
    private const int TestWidth = 1280;
    private const int TestHeight = 720;

    private string AnimDir() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Parsec", "anim");

    private void OnTestRenderClick(object? sender, RoutedEventArgs e)
    {
        if (_view == null || _timeline == null)
        {
            SetStatus("Test render: animation not available for this fractal.");
            return;
        }
        StopPlayback();
        double duration = _timeline.DurationFrom(0);
        if (duration <= 0)
        {
            SetStatus("Test render: set a later keyframe first (need >1 keyframe).");
            return;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = System.IO.Path.Combine(AnimDir(), $"render_{stamp}");
        var timeline = _timeline;

        SetStatus($"Rendering ~{(int)(duration * RenderFps)} frames at {TestWidth}x{TestHeight}... (window will pause)");

        // The apply-callback runs on the GL thread inside the batch loop; it sets
        // the live params for playback time t via the timeline interpolator.
        _view.RequestAnimationRender(dir, TestWidth, TestHeight, RenderFps, duration,
            t => timeline.ApplyAtTime(0, t));

        // Stitch hint: print the ffmpeg command for turning frames into a video.
        string mp4 = System.IO.Path.Combine(dir, "out.mp4");
        Console.WriteLine($"To stitch: ffmpeg -framerate {RenderFps} -i \"{System.IO.Path.Combine(dir, "frame_%05d.png")}\" -c:v libx264 -pix_fmt yuv420p \"{mp4}\"");
    }

    private void OnSaveAnimClick(object? sender, RoutedEventArgs e)
    {
        if (_timeline == null) { SetStatus("Nothing to save for this fractal."); return; }
        try
        {
            string dir = AnimDir();
            System.IO.Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = System.IO.Path.Combine(dir, $"timeline_{stamp}.json");
            System.IO.File.WriteAllText(path, _timeline.ToJson());
            SetStatus($"Saved animation to {path}");
        }
        catch (Exception ex) { SetStatus($"Save failed: {ex.Message}"); }
    }

    private void OnLoadAnimClick(object? sender, RoutedEventArgs e)
    {
        if (_timeline == null) { SetStatus("Load not available for this fractal."); return; }
        try
        {
            string dir = AnimDir();
            if (!System.IO.Directory.Exists(dir))
            {
                SetStatus("No saved animations found.");
                return;
            }
            // MVP: load the most recent timeline_*.json. (A file picker is a
            // later refinement.)
            var files = System.IO.Directory.GetFiles(dir, "timeline_*.json");
            if (files.Length == 0) { SetStatus("No saved animations found."); return; }
            Array.Sort(files);
            string latest = files[^1];
            string json = System.IO.File.ReadAllText(latest);
            if (_timeline.LoadJson(json))
            {
                _bank?.Refresh(_timeline);
                _timeline.ApplySlot(_timeline.SelectedIndex);
                RefreshPanelValues();
                _view?.MarkDirty();
                SetStatus($"Loaded {System.IO.Path.GetFileName(latest)}");
            }
            else
            {
                SetStatus("Load failed: saved animation doesn't match this fractal.");
            }
        }
        catch (Exception ex) { SetStatus($"Load failed: {ex.Message}"); }
    }

    private void SetStatus(string text)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status != null) status.Text = text;
    }
}
