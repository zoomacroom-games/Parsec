// =============================================================================
// Parsec quaternion Julia distance estimator - core
// =============================================================================
//
// Includable chunk (no #version, no main). Concatenated after a #version line
// and before raymarch_main.glsl, like the other DE cores.
//
// A quaternion Julia set: iterate z -> z^2 + c in the quaternions (4D). The set
// lives in 4D; we render a 3D SLICE (fix the 4th component of the sampling point
// to 'wslice'), and CUT THE SOLID IN HALF with a clipping plane to reveal the
// iconic intricate nested interior (smooth glassy shell, layered onion guts).
//
// Quaternions ARE a real division algebra, so z^2+c is genuinely analytic and
// the DE is exact and well-behaved: with z' -> 2*z*z', DE = 0.5*|z|*log|z|/|z'|.
// Validated in Python (qjulia_proto.py) against the recognizable cutaway.
//
// Two headline knobs beyond a normal fractal:
//   - wslice: which 3D slice of the 4D object we see (morphs the whole shape)
//   - cut plane (normal + offset): which half of the solid we reveal
//
// GEOMETRIC ORBIT TRAPS (iquilezles.org/articles/orbittraps3d): while iterating,
// track the orbit's closest approach to a chosen 3D shape (sphere / cylinder /
// plane / sine sheet). Seeds whose orbit passes through the shape are solid, so
// the shape materializes all over the set, repeated at every scale. Each sample
// is divided by the running derivative |z'| -- in the quaternions the derivative
// map is a similarity with exactly that scale, so this is the proper first-order
// compensation for the fractal's domain distortion (iq's "divide by the
// gradient"). In color-only mode the trap just drives the shell glaze channel.
//
// PARAMETER REUSE (shared FoldParams buffer, binding 1):
//   iterations    = iteration count
//   mode          = orbit trap shape (0 off, 1 sphere, 2 cylinder, 3 plane, 4 sine)
//   juliaMode     = orbit trap mode (0 = color-only, 1 = union into the DE,
//                   2 = trap-only fibers)
//   juliaC        = the quaternion constant c = (cx, cy, cz, cw)
//   boxParams     = (wslice, planeOffset, bailout, cutEnabled)
//   surfParams    = (planeNormal.xyz, _)
//   rot           = (stereoMode>0.5, stereo scale k, stereo radius R, DE fudge)
//   boundSphere   = bounding sphere for the fast skip
//   trapA         = (trap center xyz, trap radius / slab half-thickness)
//   trapB         = (sine amplitude, sine frequency, trap DE fudge, _)
//   cvary         = (axis 0off/1x/2y/3z, dc.x, dc.y, dc.z) -- SPATIALLY VARYING c
//
// SPATIALLY VARYING c ("hybrid" / parameter-field Julia): instead of one global
// constant, each seed point uses c(p) = juliaC + p[axis]*dc.xyz, so c sweeps
// across the object along a chosen spatial axis. Every point still iterates a
// FIXED c during its own orbit (c depends only on the seed), so the per-point
// DE math is unchanged; but the boundary now moves through space as c does, so
// the distance metric is only first-order (domain distortion, per iq's orbit-
// trap note) -- the DE fudge slows the marcher to cope. cw is left untouched.

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // orbit trap shape: 0 off, 1 sphere, 2 cylinder, 3 plane, 4 sine
    int   juliaMode;        // orbit trap mode: 0 color-only, 1 union, 2 trap-only
    int   pad0;

    vec4  boxParams;        // (wslice, planeOffset, bailout, cutEnabled)
    vec4  surfParams;       // (planeNormal.xyz, _)
    vec4  juliaC;           // quaternion constant c
    vec4  rot;              // (stereoMode, stereoK, stereoR, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)

    vec4  trapA;            // (trap center xyz, trap radius)
    vec4  trapB;            // (sine amplitude, sine frequency, trap DE fudge, _)
    vec4  cvary;            // (axis 0off/1x/2y/3z, dc.x, dc.y, dc.z)
} fp;

vec4 gTrap;

// Hamilton product of two quaternions (x = scalar, yzw = vector part packed as
// (a, b, c, d) = a + b i + c j + d k).
vec4 qmul(vec4 p, vec4 q) {
    return vec4(
        p.x * q.x - p.y * q.y - p.z * q.z - p.w * q.w,
        p.x * q.y + p.y * q.x + p.z * q.w - p.w * q.z,
        p.x * q.z - p.y * q.w + p.z * q.x + p.w * q.y,
        p.x * q.w + p.y * q.z - p.z * q.y + p.w * q.x);
}

// Quaternion square (clean closed form: vector part is just 2*scalar*vector).
vec4 qsq(vec4 q) {
    return vec4(q.x * q.x - q.y * q.y - q.z * q.z - q.w * q.w,
                2.0 * q.x * q.y, 2.0 * q.x * q.z, 2.0 * q.x * q.w);
}

// Signed distance from an orbit point (3D projection) to the trap shape. Plain
// SDFs in orbit space; the fractal domain distortion is compensated at the call
// site by dividing by |z'|.
float trapShapeDist(vec3 q) {
    vec3  tc = fp.trapA.xyz;
    float tr = fp.trapA.w;
    if (fp.mode == 1) return length(q - tc) - tr;            // sphere
    if (fp.mode == 2) return length(q.xz - tc.xz) - tr;      // cylinder along y
    if (fp.mode == 3) return abs(q.x - tc.x) - tr;           // plane slab at x
    // Sine sheet: slab of half-thickness tr around y = cy + A*sin(f*(x - cx)).
    // The vertical residual overestimates distance by up to the graph's slope,
    // so scale by 1/sqrt(1 + (A*f)^2) to keep it a lower bound.
    float A = fp.trapB.x, f = fp.trapB.y;
    float dy = abs(q.y - tc.y - A * sin(f * (q.x - tc.x))) - tr;
    return dy * inversesqrt(1.0 + A * A * f * f);
}

float estimate(vec3 p) {
    float wslice     = fp.boxParams.x;
    float planeOff   = fp.boxParams.y;
    float bailout    = fp.boxParams.z;
    bool  cutEnabled = fp.boxParams.w > 0.5;
    vec4  c          = fp.juliaC;

    // Spatially varying c: shift c by the seed's coordinate along the chosen
    // axis. Constant for THIS orbit (depends only on p), so the DE recurrence
    // below is untouched; the fudge covers the boundary's spatial drift.
    int cvAxis = int(fp.cvary.x + 0.5);
    if (cvAxis > 0) {
        float coord = (cvAxis == 1) ? p.x : (cvAxis == 2) ? p.y : p.z;
        c.xyz += fp.cvary.yzw * coord;
    }

    // --- seed mapping: flat 4D slice, or inverse-stereographic (curved) slice ---
    // Flat: z = (p, wslice) -- the standard flat 3-plane through the 4D set.
    // Stereographic: wrap R^3 onto a 3-sphere of radius R (input pre-scaled by k);
    // the 4D point IS the quaternion seed. Conformal, so the seed-space DE maps
    // back to 3D by 1/lambda, lambda = 2kR/(1+k^2|p|^2). (wslice is unused here.)
    float stereoMode = fp.rot.x;
    float kIn        = fp.rot.y;
    float Rsph       = fp.rot.z;

    vec4 z;
    float deScale = 1.0;
    if (stereoMode > 0.5) {
        vec3 pk = p * kIn;
        float s = dot(pk, pk);
        z = Rsph * vec4(2.0 * pk, s - 1.0) / (s + 1.0);    // R^3 -> R*S^3
        deScale = (1.0 + s) / max(2.0 * kIn * Rsph, 1e-9); // = 1 / lambda
    } else {
        z = vec4(p, wslice);                               // flat slice
    }

    vec4 zp = vec4(1.0, 0.0, 0.0, 0.0);   // running derivative
    float r = 0.0;
    gTrap = vec4(1e20);
    float trapDE = 1e20;   // geometric trap: min over the orbit of dist/|z'|

    for (int i = 0; i < fp.iterations; i++) {
        r = length(z);
        if (r > bailout) break;
        zp = 2.0 * qmul(z, zp);   // z' = 2 z z'  (before updating z)
        z  = qsq(z) + c;
        gTrap.x = min(gTrap.x, length(z));
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        if (fp.mode == 0) {
            gTrap.w = min(gTrap.w, abs(length(z) - 1.0));
        } else {
            // Shell glaze channel traces the trap instead of the unit sphere,
            // so the palette highlights wherever orbits graze the shape.
            // Divisor clamped at 1: where |z'| < 1 (attracting basin) the
            // first-order compensation would INFLATE the estimate and the
            // marcher punches through thin tubes. The unscaled distance is a
            // conservative bound there (contraction only fattens the tubes in
            // seed space), so clamping trades speed for no holes.
            float td = trapShapeDist(z.xyz);
            gTrap.w = min(gTrap.w, abs(td));
            trapDE  = min(trapDE, td / max(length(zp), 1.0));
        }
    }

    r = length(z);
    float dz = length(zp);
    float de = (dz < 1e-12) ? 0.0 : 0.5 * log(max(r, 1e-12)) * r / dz;
    de *= deScale;   // conformal correction (1.0 in flat mode)

    // Geometric trap modes. trapDE was measured in orbit space and divided by
    // |z'| per sample; the seed-map correction (stereographic 1/lambda) is the
    // same as the main DE's. The trap fudge shrinks steps for safety -- the
    // compensation is first-order.
    //   1 = union: the trapped shape materializes inside the normal Julia solid.
    //   2 = trap-only: the tubes ARE the geometry (drop the Julia surface).
    //       With a small sphere trap at a slowly-attracting fixed point this
    //       renders the interior as bundles of orbit-streamline fibers (the
    //       look of iq's "Julia - Quaternion 2").
    if (fp.mode != 0 && fp.juliaMode == 1) {
        de = min(de, trapDE * fp.trapB.z * deScale);
    } else if (fp.mode != 0 && fp.juliaMode == 2) {
        de = trapDE * fp.trapB.z * deScale;
        // Fiber fields are all thin geometry; even the clamped estimate is
        // first-order, so cap the step at a few trap radii. Costs a bounded
        // number of extra steps, kills the remaining tube punch-through.
        de = min(de, 6.0 * fp.trapA.w);
    }

    // Half-cut: intersect the solid with a half-space (CSG intersection = max of
    // signed distances). The plane is dot(p, n) = planeOff, in march space, so it
    // is already a true 3D distance and is NOT scaled by deScale.
    // Guard: normalize(0) is NaN and max(de, NaN) poisons the march, so an
    // unset plane normal means "no cut" rather than garbage pixels.
    if (cutEnabled && dot(fp.surfParams.xyz, fp.surfParams.xyz) > 1e-12) {
        vec3 n = normalize(fp.surfParams.xyz);
        float plane = dot(p, n) - planeOff;
        de = max(de, plane);
    }

    return de;
}

vec4 attractorBoundingSphere() {
    // Stereographic wraps all of R^3 onto the sphere; the flat-mode skip sphere
    // would clip the form to a disc, so disable the skip in stereo mode.
    if (fp.rot.x > 0.5) return vec4(0.0, 0.0, 0.0, 1e6);
    return fp.boundSphere;
}

float deFudge() {
    return fp.rot.w;
}
