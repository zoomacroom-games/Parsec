# Parsec

A GPU-accelerated explorer for 2D and 3D fractals - built in C# with [Avalonia](https://avaloniaui.net/) and OpenGL compute shaders.

Parsec renders distance-estimated 3D fractals (Mandelbox, Mandelbulb, Kleinian groups, Menger sponges, and many more) in real time with a fly camera, and explores classic 2D escape-time sets (Mandelbrot, Julia, Burning Ship) with true perturbation-based deep zoom. It has a keyframe animation system, high-resolution "hero" stills, and video export (through FFMPEG).

<img width="1870" height="1870" alt="Screenshot 2026-05-30 125912" src="https://github.com/user-attachments/assets/51c61924-0efb-472c-8a2d-b915ea5258d7" />

---

## Building & running

> [!IMPORTANT]
> **Build and run `Parsec.App.csproj`.** The solution contains several projects, but `Parsec.App` is the only runnable application — the others are libraries it references. Building the solution or a library project on its own will not launch Parsec.

You will probably need to make sure that Parsec.Rendering.csproj, Parsec.Rendering.Gpu.csproj, and Parsec.Core.csproj are included in your solution. (Right click solution -> Add -> Existing project and select each csproj file).

```bash
git clone https://github.com/zoomacroom-games/Parsec
cd parsec

# Run it (Release is strongly recommended — the renderer is compute-heavy):
dotnet run --project Parsec.App/Parsec.App.csproj -c Release
```

### Requirements

- **.NET SDK** — see the `<TargetFramework>` in `Parsec.App.csproj` for the exact version.
- **A GPU with OpenGL 4.3+**, including compute shaders and **double-precision (fp64)** support. The 3D raymarchers and the 2D deep-zoom pipeline both rely on compute shaders; deep zoom additionally needs fp64.
- **ffmpeg** *(optional)* — only needed to stitch exported animation frames into a video. Parsec prints the exact `ffmpeg` command after a frame export.

---

## What's inside

### 3D fractals (raymarched, distance-estimated)

Mandelbox (classic) · AmazingBox · Rotated Mandelbox · Mandelbulb · Quaternion Julia · Amazing IFS (KIFS) · Kleinian · Pseudo-Kleinian · Pseudo-Kleinian 4D · Folded Menger · Bicomplex Julia · Apollonian Gasket · Phoenix · Biomorph · Mosely Snowflake · Riemann Sphere · Mandalay Fold · Anisotropic Fold · 3D Burning Ship · Hybrid (box+bulb) · QJulia × Box · Orbit Hybrid (KIFS+Mbox) · Thomas Attractor

Each is a GPU distance estimator with orbit-trap coloring, configurable lighting/reflection, and a free-fly camera whose speed adapts to the distance field (where a CPU DE mirror exists).

### 2D deep zoom

A perturbation-theory escape-time explorer with four formulas — **Mandelbrot, Julia, Burning Ship, Prospector** — selectable from a dropdown. Highlights:

- **Arbitrary-depth zoom** via a high-precision reference orbit (binary fixed-point) plus per-pixel delta iteration, validated against an mpmath oracle.
- Three render paths chosen automatically by depth: **direct fp64** (shallow), **fp64 perturbation** (mid), and **floatexp perturbation** (deep), reaching radii down to ~1e-147.
- Julia mode exposes a keyframeable constant **κ**, so a κ sweep morphs the set in an animation.
- Drag to pan, scroll to zoom.

### Rendering & animation

- **Keyframe timeline** with playback (Space to play/pause) and per-fractal animation save/load.
- **Hero stills** with up to 16× SSAA at resolutions up to 12K.
- **Video export** to a frame sequence (+ an `ffmpeg` stitch command).
- Shared palette / lighting / reflection controls across all chapters.

---

## Using it

1. Pick a fractal from the **FRACTAL** dropdown (top-right). For Deep Zoom 2D, a **FORMULA** dropdown appears.
2. **3D:** fly with the mouse + keyboard. **2D deep zoom:** drag to pan, scroll to zoom.
3. Tune parameters in the side panel.
4. Set keyframes in the timeline, hit **Space** to preview the animation.
5. **Save Hero Render** for a high-res still, or **Test Render** to export an animation frame sequence.

---

## Known limitations

Parsec is a personal project shared in the hope it's useful — these are the rough edges to expect:

- **Deep-zoom Burning Ship at very wide views.** Perturbation is unreliable for the abs-fold map when the delta is large; the renderer uses direct fp64 at shallow zoom to compensate, but extreme wide framings can still show boundary noise. Zoom in for clean results.
- **Fly-camera speed near some 3D fractals.** A few chapters lack a CPU distance-estimate mirror, so the camera glides at a constant speed near them instead of slowing into detail. Purely a navigation nicety, not a render issue.
- **Deep-zoom floor.** Zoom bottoms out around radius `1e-147` (the combined fp64 + floatexp precision limit).
- **Hardware.** No software fallback — a reasonably modern GPU with fp64 compute is required.

---

## Contributing

Contributions are welcome — issues and pull requests both. This is a labor of love maintained alongside other projects, so please be patient with review times; there's no SLA.


## License

Parsec is released under the **GNU General Public License v3.0 or later**. See `LICENSE` for the full text.

## Acknowledgments

Parsec stands on decades of work from the fractal community — Tom Lowe's Mandelbox, the Mandelbulb collaboration, and the formula research shared on fractalforums and in Mandelbulber, among many others. Special thank you to Inigo Quilez for his work on fractals.

Pseudo Kleinian 4D, Mandalay Fold, and Riemann Sphere fractal formulas based approximately on formulas found within Mandelbulber.
