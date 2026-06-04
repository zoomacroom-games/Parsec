# Parsec — Adding a New Chapter

A practical guide to adding a fractal chapter to Parsec. Companion to
`ParsecHandoff.md` (read that first for the big picture: shared `RaymarchPipeline`,
reflections, SSAA, hero tiers). This doc is only about the mechanics of getting a
new distance estimator into the gallery.

Working constraints worth stating up front, because they shape everything:

- **The sandbox cannot build or run Parsec.** No `dotnet`, no GPU. The *only*
  thing that executes is `glslangValidator` (compiles GLSL) and Python
  (numpy/scipy/PIL, for math validation). So the loop is: validate the math in
  Python → compile-check the GLSL → brace/paren-check the C# → hand to the human
  to render. You never see the image; they do.
- **Only the GPU judges beauty.** Your job is validated *correctness* (the DE is
  right, the surface won't tear); the aesthetic call is the human's. Be honest
  about approximations and limitations rather than over-claiming.

---

## 1. The mental model

Every chapter is one **distance estimator** (`*_core.glsl`) plugged into a shared
raymarcher. The core is an includable chunk — **no `#version`, no `main`**.
`ShaderLoader.LoadComposite("x_core.glsl", "raymarch_main.glsl")` prepends
`#version 430 core` and concatenates the shared `raymarch_main.glsl` (which owns
the marcher, shading, shadows, reflections). The core only has to provide the
distance field and a few accessors.

### The core contract (every `*_core.glsl` MUST declare)

```glsl
// Shared parameter buffer. The field NAMES are per-chapter (only the std430
// LAYOUT must match: 4 ints, then 5 vec4). Each chapter REINTERPRETS the slots.
layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;            // reinterpret freely
    int   juliaMode;       // reinterpret freely
    int   pad0;
    vec4  boxParams;
    vec4  surfParams;
    vec4  juliaC;
    vec4  rot;             // rot.w is conventionally the DE fudge
    vec4  boundSphere;     // (cx, cy, cz, r)
} fp;

vec4 gTrap;                                  // 4 orbit traps, written by estimate()

float estimate(vec3 p) { ... return DE; }    // the distance estimate
vec4  attractorBoundingSphere() { return fp.boundSphere; }
float deFudge()                 { return fp.rot.w; }
```

- **`gTrap`** must be filled during `estimate()` — the shared shader calls
  `estimate()` again at the hit point to read these for the palette. Standard four:
  `x = min|z|`, `y = min|z.x|`, `z = min len(z.xy)`, `w = min |len(z)-1|`.
- The marcher steps `d = estimate(p) * deFudge()` and registers a hit when
  `d < hitEps * (1.0 + t*0.5)` — note the epsilon is **distance-scaled**, not
  resolution-scaled. This matters for DE tuning (see §5).

---

## 2. The four DE strategies — pick one

Picking the right strategy is the real design decision. The gallery now has one
chapter exemplifying each:

| Strategy | Example chapter | When | DE form |
|---|---|---|---|
| **Exact analytic** | Pseudo-Kleinian 4D | Closed-form DE derivable | whatever the math gives |
| **Scalar-derivative escape-time** | Mandelbulb, Riemann Sphere | Conformal/similarity map | `0.5*log(r)*r/dr` (power) or `|z|/dr` (linear) |
| **Fold scaffold** | Mandalay | A transform (SDF CSG), no native escape | wrap `z=scale*fold(z)+c`, `dr=|scale|*dr+1`, `DE=|z|/dr` |
| **Numerical delta-DE** | Anisotropic | Non-conformal / anisotropic map | finite-difference Jacobian `J`, `DE=|z|/‖J‖` |

**The decision rule:** a scalar `dr` carries one number, so it is only valid when
the map scales space *uniformly* in every direction (conformal / similarity). The
moment the map stretches unequally (anisotropic), a scalar `dr` over-estimates
distance along the most-stretched axis and the marcher tears holes in the surface.
For those, finite-difference the 3×3 Jacobian and use `|z|/‖J‖` (Frobenius `‖J‖`
is a strict lower bound → hole-free but ~conservative; power-iteration σ_max is
tight but can sparkle at fold seams). Delta-DE costs ~4× (four orbits per step).

**How to decide in Python (do this before writing GLSL):** run the orbit tracking
both a scalar `dr` *and* an FD-Jacobian σ_max, then compare on near-surface points.
- ratio ≈ 1 → scalar `dr` valid, use the cheap DE.
- ratio < 1 anywhere (dr under-estimates the derivative) → **overshoot, holes** →
  you need delta-DE.
- ratio > 1 (dr over-estimates) → safe but conservative; scalar `dr` is fine.

---

## 3. Validation workflow (always, in this order)

1. **Python ground truth.** Implement the map + DE in numpy. Check two things:
   - **Non-degeneracy:** sample a slice, count the bounded fraction. ~0.05–0.6 is
     a rich set; ~0 means it all escapes (wrong regime); ~1 means a solid blob.
   - **DE validity:** scalar `dr` vs FD σ_max ratio (above), or for delta-DE,
     compare the FD Jacobian DE against an exact matrix-DE (should match ~1e-10).
   This step is where you find a *working parameter regime* and catch degeneracy
   cheaply — sweep parameters here, not by asking the human to render dozens of times.
2. **Compile-check the GLSL:**
   ```
   { echo "#version 430 core"; cat x_core.glsl; echo; cat raymarch_main.glsl; } > _x.comp
   glslangValidator _x.comp
   ```
3. **Cross-check the GLSL construction against Python** — re-implement the core's
   exact slot-unpacking + map in numpy and confirm it matches the validated math.
   (Catches column-major mat bugs, wrong slot maps, etc.)
4. **Brace/paren balance** every edited C# file (a quick `count('{')==count('}')`
   and `count('(')==count(')')` script catches most botched edits before handoff).

---

## 4. Files to create (per chapter)

Two C# files; clone an existing pair (e.g. `GpuAnisotropicRenderer.cs` +
`AnisotropicState.cs`, or the `OrbitHybrid` pair) and adapt.

**`GpuXRenderer.cs`** (in `Parsec.Rendering.Gpu`) — contains:
- an immutable `XParams` record (the render snapshot),
- `GpuXRenderer(Gl gl, RaymarchPipeline pipeline)` whose ctor does
  `_shader = ComputeShader.FromSource(gl, ShaderLoader.LoadComposite("x_core.glsl","raymarch_main.glsl"), "x");`
- `RenderToBuffer(...)` that builds a `FoldParamsGpu` (packing params into the
  reinterpreted slots) and calls
  `_pipeline.Render(_shader, foldParams, camera, w, h, settings, bg, surface, lightDir, palette, heroSamples: settings.HeroSamples, tileRows: tileRows, progress: progress)`,
- a `Render(...)` overload returning an `SKBitmap` (Marshal.Copy the buffer),
- `Dispose()` disposing `_shader`.

**`XState.cs`** (in `Parsec.App`) — mutable live fields + `ToParams()` + a
`BuildSchema()` returning `ParamSchema { Parameters = ParamDescriptor[] }`. Each
`ParamDescriptor` has `{ Label, Group, Min, Max, Step?, Decimals, Get, Set }`;
`Get`/`Set` close over the mutable fields. Group strings become panel sections.

### Parameter packing
`FoldParamsGpu` (Pack=1): `int Iterations, Mode, JuliaMode, Pad0; Vector4 BoxParams,
SurfParams, JuliaCVec, Rot, BoundSphere`. That's **4 ints + 20 floats**. Most
chapters fit; reinterpret the slots however you like and document the map in a
comment at the top of the core (the GLSL field names need not match the C# names —
only the std430 layout matters). If a chapter needs more than 4 ints / 20 floats,
that's a bigger lift (extend the buffer + pipeline) — avoid it for a single chapter.

---

## 5. Wiring — `FractalView.cs` (11 spots)

All in `Parsec.App/FractalView.cs`. Grep for an existing chapter (e.g.
`Anisotropic` or `OrbitHybrid`) and mirror every hit:

1. **`enum FractalType`** — add the value (before `DeepZoom`, which must stay last-ish; see gotchas).
2. **Renderer field** — `private GpuXRenderer? _xRenderer;`
3. **State property** — `public XState X { get; } = new();`
4. **`BuildActiveSchema()`** — `FractalType.X => X.BuildSchema(),`
5. **`OnMoveTick()` fly-DE switch** — `FractalType.X => 1.0f,` (unless you wrote a
   cheap CPU DE for fly-camera collision; `1.0f` means "glide, don't probe").
6. **`OnOpenGlInit()`** — construct: `_xRenderer = new GpuXRenderer(_gl, _pipeline);`
7. **`OnOpenGlRender()` ready-guard** — add `|| _xRenderer == null` to the null chain.
8. **Preview render switch** — `FractalType.X => _xRenderer.RenderToBuffer(X.ToParams(),
   camera, PreviewWidth, PreviewHeight, PreviewSettings(), background:…, surface: Color.Rgb(…),
   lightDirection:…, palette:…, tileRows: 64),`
9. **Hero render switch** (in `RenderActiveTo()`) — `FractalType.X => _xRenderer!.Render(
   X.ToParams(), cam, width, height, HeroSettings(), bg, Color.Rgb(…), light, pal, tileRows: 32),`
10. **`OnOpenGlDeinit()`** — `_xRenderer?.Dispose();`
11. **Null-out** (after dispose) — `_xRenderer = null;`

Pick a distinct `surface: Color.Rgb(...)` so the chapter reads differently from its neighbours.

### Hero epsilon — the one real subtlety
`HeroSettings()` uses a very tight hit epsilon (`6e-7`). That is right for a genuine
DE lower bound (exact, fold, scalar-conformal, delta-DE Frobenius). It is **wrong**
for a *spiky / near-discontinuous* numerical DE — the Riemann Sphere rendered with
holes at `6e-7` because its escape DE is jagged. The fix already in the tree: a
`HeroSettings(float eps)` overload; the Riemann hero arm passes `HeroSettings(1.5e-3f)`.
If your DE is numerical and you see sparse hero output, that's the lever. Conservative-
but-valid DEs (delta-DE, folds) do **not** need it.

---

## 6. Wiring — `MainWindow.axaml` + `MainWindow.axaml.cs`

- **`MainWindow.axaml`** — add a `<ComboBoxItem>Your Name</ComboBoxItem>` in the
  selector, in the position you want it to appear.
- **`MainWindow.axaml.cs`, `OnFractalChanged()`** — add the index→type arm
  (`N => FractalType.X,`). **This index MUST match the combo position** (see gotchas).
- **`MainWindow.axaml.cs`, `OnHeroClick()`** — add the slug arm
  (`FractalType.X => "yourslug",`) used for the hero PNG filename.

---

## 7. `.csproj` — easy to forget

`Parsec_Rendering_Gpu.csproj` lists shader resources **explicitly, not by glob**.
Every new core needs a manual line:

```xml
<EmbeddedResource Include="Shaders\x_core.glsl" />
```

If you skip it, the shader isn't embedded and the chapter throws at construction.
(Note: the uploaded csproj has historically been stale — verify it actually lists
every `*_core.glsl` currently in `Shaders/`.)

---

## 8. Gotchas checklist

- [ ] **Combo index ↔ enum ↔ `OnFractalChanged` must stay in sync.** The combo is
      position-indexed; inserting a chapter mid-list shifts every later index.
      `DeepZoom` (the 2D pipeline) must be explicitly mapped — it was once a latent
      bug where it fell through to the `kifs` default.
- [ ] **Name collisions.** `Hybrid` already exists as an enum value (a different,
      older chapter) — that's why the orbit-hybrid chapter is `OrbitHybrid`. Check
      the enum before naming.
- [ ] **`.csproj` EmbeddedResource line added** (§7).
- [ ] **`gTrap` written** in `estimate()` or the palette breaks.
- [ ] **DE fudge** (`rot.w`) is the safety lever: `<1` shortens steps (safer/slower).
      Conservative DEs can sit near `1.0`; tight/numerical DEs want margin.
- [ ] **`deFudge()` / `attractorBoundingSphere()`** present in the core (the marcher
      calls them).
- [ ] **Brace/paren balance** of `FractalView.cs` after editing — it's the file most
      likely to get a botched multi-arm edit.
- [ ] **`DeepZoom` is special** — it uses a separate `_deepPipeline` (2D), not the
      shared raymarcher, and is excluded from anything that assumes a 3D `estimate()`
      (e.g. it can't be a hybrid partner). The Thomas **Attractor** is also special
      (point cloud, no `estimate()`).

---

## 9. Worked reference

The five chapters added most recently are clean, self-consistent templates — read
the core comment headers, they document the slot maps and the DE reasoning:

- `pseudokleinian4d_core.glsl` — exact analytic DE (4D slice).
- `riemann_sphere_core.glsl` — scalar-derivative escape-time + the coarse-hero fix.
- `mandalay_core.glsl` — transform wrapped in a fold scaffold.
- `anisotropic_core.glsl` — numerical delta-DE (FD Jacobian, Frobenius/σ_max).
- `orbithybrid_core.glsl` — two formulas composed in one orbit (prototype). Its
  header also records the **orbit-hybrid selection rule**: a composed hybrid needs
  at least one magnitude-capping fold and overlapping basins, or it diverges
  (Mandelbulb+KIFS was proven degenerate; KIFS+Mandelbox works).
