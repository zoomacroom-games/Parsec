// =============================================================================
// Parsec Orbit Hybrid (prototype) distance estimator - core
// =============================================================================
//
// Includable chunk (no #version, no main). Concatenated after a #version line
// and before raymarch_main.glsl, like the other DE cores.
//
// PROTOTYPE of the orbit-hybrid mechanism: two formulas composed into ONE
// orbit, sharing a single z and a single running derivative dr, with the active
// formula chosen each iteration by a repeating schedule. This is the "new
// shape" kind of hybrid (function composition), not CSG of two finished fields.
//
// The pairing is KIFS + Mandelbox -- chosen after a parameter study (documented
// below). It is NOT the originally-attempted Mandelbulb + KIFS, which was found
// to be mathematically degenerate: with no magnitude-capping fold in either
// map, every orbit diverges (bounded fraction < 5% across 240 swept regimes,
// bailout-invariant). The selection rule that fell out of that study: an orbit
// hybrid needs at least one fold that CAPS |z|, and the two maps must share a
// bounded basin. Mandelbox's box fold (clamp) is that cap; KIFS's abs() is not.
//
// Validated in Python (kifs_mbox_proto):
//   * bounded fraction up to ~0.61 (rich, non-degenerate)
//   * scalar dr vs the true FD-Jacobian sigma_max: ratio ~1.7 (dr OVER-estimates
//     the derivative -> DE under-estimates distance -> hole-free/conservative,
//     the safe direction). So this hybrid takes the cheap |z|/dr DE; no delta-DE.
//
// The two steps keep their native conventions:
//   KIFS:     z = scale * sphereFold(rot(abs(z)))            ; dr = dr*|scale|+1
//   Mandelbox z = scale * sphereFold(boxFold(z)) + c         ; dr = dr*|scale|+1
// The Mandelbox step adds c (= p, Mandelbrot); the KIFS step does not. They
// share the sphere-fold radii. DE = |z| / |dr| (linear; both maps are folds).
//
// PARAMETER PACKING (shared FoldParams buffer, binding 1):
//   iterations  = total iteration cap
//   (mode slot)      reused as kifsCount  (KIFS steps per schedule cycle)
//   (juliaMode slot) reused as mboxCount  (Mandelbox steps per schedule cycle)
//   boxParams   = (kifsScale, mboxScale, minRadius, fixedRadius)
//   surfParams  = (postRotX, postRotY, postRotZ, bailout)
//   juliaC      = (boxFoldLimit, _, _, _)
//   rot         = (_, _, _, fudge)
//   boundSphere = (cx, cy, cz, r)

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;       // total iteration cap
    int   kifsCount;        // KIFS steps per cycle   (reuses 'mode' slot)
    int   mboxCount;        // Mandelbox steps per cycle (reuses 'juliaMode' slot)
    int   pad0;

    vec4  boxParams;        // (kifsScale, mboxScale, minRadius, fixedRadius)
    vec4  surfParams;       // (postRotX, postRotY, postRotZ, bailout)
    vec4  juliaC;           // (boxFoldLimit, _, _, _)
    vec4  rot;              // (_, _, _, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)
} fp;

vec4 gTrap;

mat3 rotationFromEuler(vec3 r) {
    float cx = cos(r.x), sx = sin(r.x);
    float cy = cos(r.y), sy = sin(r.y);
    float cz = cos(r.z), sz = sin(r.z);
    mat3 rx = mat3(1,0,0,  0,cx,-sx,  0,sx,cx);
    mat3 ry = mat3(cy,0,sy,  0,1,0,  -sy,0,cy);
    mat3 rz = mat3(cz,-sz,0,  sz,cz,0,  0,0,1);
    return rz * ry * rx;
}

// Shared sphere fold (inversion + inner linear zone). Scales dr by the same
// factor as z -- identical to the standalone KIFS / Mandelbox cores.
void sphereFold(inout vec3 z, inout float dr, float minR2, float fixedR2) {
    float r2 = dot(z, z);
    if (r2 < minR2) {
        float t = fixedR2 / minR2;   // inner: linear blow-up
        z *= t; dr *= t;
    } else if (r2 < fixedR2) {
        float t = fixedR2 / r2;      // shell: sphere inversion
        z *= t; dr *= t;
    }
}

// --- the two formula steps, each one iteration of its map -------------------

void kifsStep(inout vec3 z, inout float dr, mat3 postR, bool usePost,
              float scale, float minR2, float fixedR2) {
    z = abs(z);                       // plane fold into octant (does NOT cap |z|)
    if (usePost) z = postR * z;       // rotation after fold (the curl)
    sphereFold(z, dr, minR2, fixedR2);
    z = scale * z;
    dr = dr * abs(scale) + 1.0;
}

void mboxStep(inout vec3 z, inout float dr, vec3 c,
              float scale, float L, float minR2, float fixedR2) {
    z = clamp(z, -L, L) * 2.0 - z;    // BOX fold -- the magnitude cap that bounds the hybrid
    sphereFold(z, dr, minR2, fixedR2);
    z = scale * z + c;                // Mandelbox adds c (= p)
    dr = dr * abs(scale) + 1.0;
}

float estimate(vec3 p) {
    float kifsScale = fp.boxParams.x;
    float mboxScale = fp.boxParams.y;
    float minR2     = fp.boxParams.z * fp.boxParams.z;
    float fixedR2   = fp.boxParams.w * fp.boxParams.w;
    vec3  postE     = fp.surfParams.xyz;
    float bailout   = fp.surfParams.w;
    float L         = fp.juliaC.x;

    mat3 postR  = rotationFromEuler(postE);
    bool usePost = (postE.x != 0.0 || postE.y != 0.0 || postE.z != 0.0);

    int kc  = max(fp.kifsCount, 0);
    int mc  = max(fp.mboxCount, 0);
    int cyc = max(kc + mc, 1);        // schedule cycle length

    vec3  z  = p;
    vec3  c  = p;                     // Mandelbrot: c is the sample point
    float dr = 1.0;
    gTrap = vec4(1e20);

    for (int i = 0; i < fp.iterations; i++) {
        if (length(z) > bailout) break;

        // Pick the active formula for this iteration from the schedule.
        int phase = i - (i / cyc) * cyc;   // i % cyc
        if (phase < kc) {
            kifsStep(z, dr, postR, usePost, kifsScale, minR2, fixedR2);
        } else {
            mboxStep(z, dr, c, mboxScale, L, minR2, fixedR2);
        }

        float rz = length(z);
        gTrap.x = min(gTrap.x, rz);
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(rz - 1.0));
    }

    // Linear fold-family DE. dr over-estimates the true derivative (~1.7x), so
    // this under-estimates distance -> conservative, hole-free.
    return length(z) / max(abs(dr), 1e-12);
}

vec4 attractorBoundingSphere() { return fp.boundSphere; }
float deFudge()                { return fp.rot.w; }
