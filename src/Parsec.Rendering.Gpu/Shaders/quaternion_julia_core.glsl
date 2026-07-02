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
// PARAMETER REUSE (shared FoldParams buffer, binding 1):
//   iterations    = iteration count
//   juliaC        = the quaternion constant c = (cx, cy, cz, cw)
//   boxParams     = (wslice, planeOffset, bailout, cutEnabled)
//   surfParams    = (planeNormal.xyz, _)
//   rot           = (stereoMode>0.5, stereo scale k, stereo radius R, DE fudge)
//   boundSphere   = bounding sphere for the fast skip

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // unused
    int   juliaMode;        // unused
    int   pad0;

    vec4  boxParams;        // (wslice, planeOffset, bailout, cutEnabled)
    vec4  surfParams;       // (planeNormal.xyz, _)
    vec4  juliaC;           // quaternion constant c
    vec4  rot;              // (stereoMode, stereoK, stereoR, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)
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

float estimate(vec3 p) {
    float wslice     = fp.boxParams.x;
    float planeOff   = fp.boxParams.y;
    float bailout    = fp.boxParams.z;
    bool  cutEnabled = fp.boxParams.w > 0.5;
    vec4  c          = fp.juliaC;

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

    for (int i = 0; i < fp.iterations; i++) {
        r = length(z);
        if (r > bailout) break;
        zp = 2.0 * qmul(z, zp);   // z' = 2 z z'  (before updating z)
        z  = qsq(z) + c;
        gTrap.x = min(gTrap.x, length(z));
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(length(z) - 1.0));
    }

    r = length(z);
    float dz = length(zp);
    float de = (dz < 1e-12) ? 0.0 : 0.5 * log(max(r, 1e-12)) * r / dz;
    de *= deScale;   // conformal correction (1.0 in flat mode)

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
