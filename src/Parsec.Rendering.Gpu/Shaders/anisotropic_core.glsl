// =============================================================================
// Parsec Anisotropic Fold (delta-DE) distance estimator - core
// =============================================================================
//
// Includable chunk (no #version, no main). Concatenated after a #version line
// and before raymarch_main.glsl, like the other DE cores.
//
// The first DELTA-DE chapter. The map is an escape-time box-fold under an
// ANISOTROPIC linear step:  z = M * boxFold(z) + c , where
//     M = Rz(shearZ) * Ry(shearY) * diag(scale*rx, scale*ry, scale*rz).
// The per-axis stretch ratios (rx,ry,rz) plus the shear rotations make M a
// non-similarity: it stretches space by different amounts in different
// directions. A scalar running derivative (dr) carries only ONE number and so
// overestimates distance along the most-stretched direction -- the marcher
// then steps through the surface and tears holes (proven in Python:
// anisotropic_de_comparison -- scalar drops ~11% of the surface).
//
// So this core estimates distance NUMERICALLY (delta-DE): run the base orbit,
// then three more orbits from p + eps along each axis, finite-difference them
// into the 3x3 Jacobian J = dz/dp, and return  DE = |z| / ||J||. No analytic
// derivative is derived -- it works for ANY map, which is the whole point of
// the tool. ||J|| is selectable:
//   mode 0 = Frobenius norm  (>= sigma_max, so the DE only ever UNDER-shoots:
//            guaranteed hole-free, slightly conservative/slow)
//   mode 1 = largest singular value via power iteration on J^T J (tight DE)
//
// Cost is ~4x a scalar core (four orbits). Validated in Python (shearfold_proto):
// this delta-DE matches the exact matrix-DE (full Jacobian propagation) to ~1e-10.
//
// PARAMETER REUSE (shared FoldParams buffer, binding 1):
//   iterations
//   mode         = ||J|| norm: 0 = Frobenius (safe), 1 = sigma_max (tight)
//   juliaMode    = 0 -> Mandelbrot (c = position), 1 -> Julia (c = juliaC.xyz)
//   boxParams    = (scale, rx, ry, rz)          // overall scale + per-axis stretch
//   surfParams   = (foldLimit, shearZ, shearY, bailout)
//   juliaC       = (cx, cy, cz, _)
//   rot          = (_, _, _, fudge)
//   boundSphere  = (cx, cy, cz, r)

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // 0 = Frobenius ||J||, 1 = sigma_max ||J||
    int   juliaMode;        // 0 = Mandelbrot, 1 = Julia
    int   pad0;

    vec4  boxParams;        // (scale, rx, ry, rz)
    vec4  surfParams;       // (foldLimit, shearZ, shearY, bailout)
    vec4  juliaC;           // (cx, cy, cz, _)
    vec4  rot;              // (_, _, _, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)
} fp;

const float DELTA_EPS = 1e-4;   // object-space finite-difference step

vec4 gTrap;

mat3 buildM() {
    float scale = fp.boxParams.x;
    vec3  ratio = fp.boxParams.yzw;          // (rx, ry, rz)
    float cz = cos(fp.surfParams.y), sz = sin(fp.surfParams.y);
    float cy = cos(fp.surfParams.z), sy = sin(fp.surfParams.z);
    // column-major: each triple is a column. Rz, Ry as the math matrices.
    mat3 Rz = mat3(cz, sz, 0.0,  -sz, cz, 0.0,  0.0, 0.0, 1.0);
    mat3 Ry = mat3(cy, 0.0, -sy,  0.0, 1.0, 0.0,  sy, 0.0, cy);
    mat3 S  = mat3(scale * ratio.x, 0.0, 0.0,
                   0.0, scale * ratio.y, 0.0,
                   0.0, 0.0, scale * ratio.z);
    return Rz * Ry * S;
}

vec3 boxFold(vec3 z, float L) { return clamp(z, -L, L) * 2.0 - z; }

// Run the map for exactly n iterations (no escape break) -- used for the offset
// orbits so they line up with the base orbit's iteration count.
vec3 runFixed(vec3 p0, int n, mat3 M, float L, bool julia, vec3 jc) {
    vec3 z = p0;
    vec3 c = julia ? jc : p0;
    for (int i = 0; i < n; i++) {
        z = M * boxFold(z, L) + c;
    }
    return z;
}

// Largest singular value of J via power iteration on A = J^T J.
float sigmaMax(mat3 J) {
    mat3 A = transpose(J) * J;
    vec3 v = normalize(vec3(0.71, 0.53, 0.46));
    for (int k = 0; k < 10; k++) {
        vec3 Av = A * v;
        float m = max(max(abs(Av.x), abs(Av.y)), abs(Av.z));
        v = (m > 1e-20) ? Av / m : v;
        v = normalize(v);
    }
    return sqrt(max(dot(v, A * v), 0.0));   // Rayleigh quotient (v normalized)
}

float estimate(vec3 p) {
    mat3  M       = buildM();
    float L       = fp.surfParams.x;
    float bailout = fp.surfParams.w;
    bool  julia   = (fp.juliaMode == 1);
    vec3  jc      = fp.juliaC.xyz;

    // ---- base orbit: find escape iteration N and final z ----
    vec3 z = p;
    vec3 c = julia ? jc : p;
    int  N = fp.iterations;
    gTrap = vec4(1e20);
    for (int i = 0; i < fp.iterations; i++) {
        if (length(z) > bailout) { N = i; break; }
        z = M * boxFold(z, L) + c;
        float rz = length(z);
        gTrap.x = min(gTrap.x, rz);
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(rz - 1.0));
    }
    vec3 zb = z;

    // ---- three offset orbits, run for exactly N iterations ----
    vec3 zx = runFixed(p + vec3(DELTA_EPS, 0.0, 0.0), N, M, L, julia, jc);
    vec3 zy = runFixed(p + vec3(0.0, DELTA_EPS, 0.0), N, M, L, julia, jc);
    vec3 zz = runFixed(p + vec3(0.0, 0.0, DELTA_EPS), N, M, L, julia, jc);

    // ---- Jacobian columns = dz/dp, then ||J|| ----
    mat3 J = mat3((zx - zb) / DELTA_EPS,
                  (zy - zb) / DELTA_EPS,
                  (zz - zb) / DELTA_EPS);

    float norm;
    if (fp.mode == 1) {
        norm = sigmaMax(J);
    } else {
        norm = sqrt(dot(J[0], J[0]) + dot(J[1], J[1]) + dot(J[2], J[2])); // Frobenius
    }

    return length(zb) / max(norm, 1e-12);
}

vec4 attractorBoundingSphere() { return fp.boundSphere; }
float deFudge()                { return fp.rot.w; }
