// =============================================================================
// Parsec Phoenix Julia (Mandelbulb-style 3D) - core
// =============================================================================
//
// Includable chunk. Concatenated after a #version line and before
// raymarch_main.glsl, like the other DE cores.
//
// Iteration:
//   z_{n+1} = bulb_pow2(z_n) + c + p_mem * z_{n-1}
//
// Where bulb_pow2 is the Mandelbulb-style square: spherical (r, theta, phi)
// -> (r^2, 2*theta, 2*phi). Lifts Ushiki's 2D Phoenix (z^2 + c + p*z_prev)
// to 3D in the same way the standard Mandelbulb lifts z^2. The memory term
// p_mem * z_{n-1} is what distinguishes Phoenix from regular Mandelbulb-Julia:
// non-Markovian iteration giving organic curling rather than crystalline form.
//
// DE: RUNNING-SCALAR-DERIVATIVE (Hubbard-Douady), same as Mandelbulb.
// NOT numerical gradient: that's an earlier version of this core, removed
// because it produced wispy artifacts for non-analytic iterations. The
// numerical-gradient DE picks up high-frequency noise in the potential field
// that the analytic algebras (quaternion Julia) avoid because they ARE smooth,
// but Mandelbulb-style trig is not smooth in the same way, and the artifacts
// show as concentric ghost-rings and filamentary haze at high resolution.
// (Same fix applies to bicomplex if it shows the same symptom.)
//
// Tracking the scalar derivative |dz/dz_0| through the iteration:
//   |dz_{n+1}| <= 2 * |z_n| * |dz_n| + |p_mem| * |dz_{n-1}|
// with dz_0 = 1, dz_{-1} = 0 (the identity Jacobian at the sample point, no
// prior state). This is an upper bound on the true derivative magnitude --
// a conservative DE that under-estimates distance, never the other way.
//
// Two headline parameters:
//   - c (vec3): the static-shape parameter, like Julia c
//   - p_mem (scalar): memory strength. p=0 -> pure Mandelbulb-Julia,
//     p=-0.5 the canonical Phoenix character. Sweeping p changes the
//     "growth feel" without changing the underlying shape.
//
// PARAMETER REUSE (shared FoldParams buffer, binding 1):
//   iterations    = iteration count
//   boxParams     = (p_mem, planeOffset, bailout, cutEnabled)
//   surfParams    = (planeNormal.xyz, _)
//   juliaC        = vec4 constant c = (cx, cy, cz, _)   -- only .xyz used
//   rot.w         = DE fudge
//   boundSphere   = bounding sphere for the fast skip

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // unused
    int   juliaMode;        // unused
    int   pad0;

    vec4  boxParams;        // (p_mem, planeOffset, bailout, cutEnabled)
    vec4  surfParams;       // (planeNormal.xyz, _)
    vec4  juliaC;           // (cx, cy, cz, _)
    vec4  rot;              // (_, _, _, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)
} fp;

vec4 gTrap;

// Mandelbulb-style square: spherical (r, theta, phi) -> (r^2, 2 theta, 2 phi).
vec3 bulbPow2(vec3 p) {
    float r2 = dot(p, p);
    float r  = sqrt(r2);
    float theta = asin(clamp(p.z / max(r, 1e-12), -1.0, 1.0));
    float phi   = atan(p.y, p.x);
    float thN = 2.0 * theta;
    float phN = 2.0 * phi;
    float cosTh = cos(thN);
    return vec3(
        r2 * cosTh * cos(phN),
        r2 * cosTh * sin(phN),
        r2 * sin(thN));
}

float estimate(vec3 p) {
    vec3  c          = fp.juliaC.xyz;
    float pMem       = fp.boxParams.x;
    float planeOff   = fp.boxParams.y;
    float bailout    = max(fp.boxParams.z, 2.0);
    bool  cutEnabled = fp.boxParams.w > 0.5;

    float absPMem = abs(pMem);
    float bo2 = bailout * bailout;

    vec3  z       = p;
    vec3  zPrev   = vec3(0.0);
    float dz      = 1.0;   // |dz/dz_0| at iteration 0 (identity)
    float dzPrev  = 0.0;   // no prior state
    float r       = length(z);

    // Orbit-trap mins, for the cosine palette. Same pattern as QJ:
    //  .x = min |z| (origin trap), .y = min |z.x| (axis trap),
    //  .z = min length(z.xy) (plane trap), .w = min ||z|-1| (unit shell).
    gTrap = vec4(1e20);

    for (int i = 0; i < fp.iterations; i++) {
        float r2 = dot(z, z);
        if (r2 > bo2) break;
        r = sqrt(r2);

        // Update derivative bound BEFORE updating z. Uses current z_n, dz_n,
        // and the previous derivative (memory contribution).
        float dzNew = 2.0 * r * dz + absPMem * dzPrev;
        // Cap dz to avoid float overflow; saturates conservatively.
        dzNew = min(dzNew, 1e30);

        // Compute next z (uses current z and previous z via memory term).
        vec3 zNew = bulbPow2(z) + c + pMem * zPrev;

        // Roll memory.
        zPrev  = z;
        z      = zNew;
        dzPrev = dz;
        dz     = dzNew;

        // Orbit traps.
        gTrap.x = min(gTrap.x, length(z));
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(length(z) - 1.0));
    }

    r = length(z);

    // Hubbard-Douady DE for the Mandelbulb-style power. Same as the standard
    // Mandelbulb shader uses; works for any non-analytic iteration where
    // |dz_{n+1}/dz_n| ~ n * |z_n|^(n-1) with n=2 here.
    float de;
    if (dz < 1e-12) {
        de = 0.0;
    } else {
        de = 0.5 * r * log(max(r, 1.0001)) / dz;
    }

    // Half-cut: CSG-intersect with a half-space.
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
    return fp.boundSphere;
}

float deFudge() {
    return fp.rot.w;
}
