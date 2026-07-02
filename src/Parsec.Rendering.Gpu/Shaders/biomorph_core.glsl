// =============================================================================
// Parsec Pickover Biomorph (Mandelbulb-style 3D) - core
// =============================================================================
//
// Includable chunk. Concatenated after a #version line and before
// raymarch_main.glsl, like the other DE cores.
//
// Iteration:  z_{n+1} = bulb_pow2(z_n) + c
// Escape:     max(|z.x|, |z.y|, |z.z|) > B   <-- COMPONENTWISE, not |z| > B
//
// The componentwise escape is the entire trick (Pickover, 1986). Standard
// Julia uses the rotation-invariant |z| > B; biomorph uses L_inf escape,
// which treats the three axes asymmetrically. The result: orbits that
// "leak out" through one axis but not the others produce limb-like basin
// regions in each direction, giving Julia sets a recognizably creature-like
// quality with arms, antennae, and bulbous bodies. Validated in Python at
// canonical (c, B) settings; the classic c = (-0.5, 0.5, 0), B = 10 shows
// a clear radial creature with multiple arm protrusions.
//
// DE: RUNNING-SCALAR-DERIVATIVE (Hubbard-Douady), same family as Mandelbulb
// and Phoenix. Tracks |dz/dz_0| through the iteration:
//   |dz_{n+1}| = 2 * |z_n| * |dz_n|        with dz_0 = 1
//   DE = 0.5 * |z| * log|z| / dz
//
// Note: the standard DE formula assumes rotation-invariant escape |z| > B.
// For the L_inf escape this is approximate (off by at most sqrt(3) factor in
// the relevant region), which is fine for raymarching -- the fudge slider
// can compensate. A proper L_inf DE would replace |z| with max(|z.x|, |z.y|,
// |z.z|) in the formula; doesn't seem to be needed in practice.
//
// PARAMETER REUSE (shared FoldParams buffer, binding 1):
//   iterations    = iteration count
//   boxParams     = (bailout B, planeOffset, _, cutEnabled)
//   surfParams    = (planeNormal.xyz, _)
//   juliaC        = vec4 constant c = (cx, cy, cz, _)   -- only .xyz used
//   rot.w         = DE fudge
//   boundSphere   = bounding sphere for the fast skip

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // unused
    int   juliaMode;        // unused
    int   pad0;

    vec4  boxParams;        // (bailout, planeOffset, _, cutEnabled)
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
    float bailout    = max(fp.boxParams.x, 2.0);
    float planeOff   = fp.boxParams.y;
    bool  cutEnabled = fp.boxParams.w > 0.5;

    vec3  z  = p;
    float dz = 1.0;
    float r  = length(z);

    // Orbit-trap mins for the cosine palette. Same pattern as QJ/Phoenix.
    gTrap = vec4(1e20);

    for (int i = 0; i < fp.iterations; i++) {
        // Componentwise (L_inf) escape -- the biomorph's defining feature.
        float maxComp = max(max(abs(z.x), abs(z.y)), abs(z.z));
        if (maxComp > bailout) break;

        r = length(z);

        // Update derivative bound BEFORE z update.
        dz = min(2.0 * r * dz, 1e30);

        z = bulbPow2(z) + c;

        // Orbit traps.
        gTrap.x = min(gTrap.x, length(z));
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(length(z) - 1.0));
    }

    r = length(z);

    // Hubbard-Douady DE.
    float de;
    if (dz < 1e-12) {
        de = 0.0;
    } else {
        de = 0.5 * r * log(max(r, 1.0001)) / dz;
    }

    // Half-cut: CSG-intersect with a half-space, general plane normal.
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
