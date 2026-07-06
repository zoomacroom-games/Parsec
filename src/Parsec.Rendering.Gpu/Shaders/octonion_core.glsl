// =============================================================================
// Parsec Octonion associator-Julia distance estimator - core
// =============================================================================
//
// Includable chunk (no #version, no main). Concatenated after a #version line
// and before raymarch_main.glsl, like the other DE cores.
//
// THE MAP (genuinely non-associative -- no analog in C/H):
//     z -> z^2 + eps * [z, p, q] + c          (z, p, q, c octonions)
// where  [z,p,q] = (z p) q - z (p q)  is the associator. In any ASSOCIATIVE
// algebra the associator is identically zero, so this reduces exactly to the
// complex z^2+c (a solid of revolution). With octonions it is nonzero and
// linear in z, mixing z out of the quaternion subalgebra <1,p,q> that Artin's
// theorem would otherwise confine the orbit to -- which is precisely why a
// plain octonion z^2+c is secretly the complex Mandelbrot, and this is not.
//
// DE: the map is analytic and the associator term is linear, so the
// Hubbard-Douady running-scalar derivative applies with the Lipschitz bound
//     |M'(z)| <= 2|z| + eps*||A||,   ||A|| = assocNorm (passed in, from p,q)
// giving dr -> (2|z| + eps*||A||)*dr,  DE = 0.5*|z|*log|z|/dr.  The octonion
// product (Cayley-Dickson) and this DE were validated in Python against the
// structure-constant tensor and a numerical-gradient field before this port.
//
// Standard core contract (estimate / gTrap / boundingSphere / fudge).
//
// PARAMETER REUSE (shared FoldParams buffer, binding 1):
//   iterations   = iteration count
//   boxParams.x  = bailout radius
//   boxParams.y  = eps (associator strength; 0 => plain complex, axisymmetric)
//   boxParams.z  = assocNorm = ||A|| spectral norm (computed in C# from p,q)
//   rot.w        = DE fudge
//   surfParams   = (stereoMode>0.5, inputScale k, sphereRadius R, _)  -- slice mode
//   boundSphere  = bounding sphere for the fast skip
//   octC*/octP*/octQ* = the octonions c, p, q (lo = comps 0..3, hi = 4..7)

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // unused
    int   juliaMode;        // unused
    int   pad0;

    vec4  boxParams;        // (bailout, eps, assocNorm, _)
    vec4  surfParams;       // (stereoMode>0.5, inputScale k, sphereRadius R, _)
    vec4  juliaC;           // unused
    vec4  rot;              // (_, _, _, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)

    vec4  trapA;            // unused (quaternion-Julia orbit trap slots; shared layout)
    vec4  trapB;            // unused
    vec4  cvary;            // unused (quaternion-Julia spatial-c slot; shared layout)

    vec4  octCLo;           // c[0..3]
    vec4  octCHi;           // c[4..7]
    vec4  octPLo;           // p[0..3]
    vec4  octPHi;           // p[4..7]
    vec4  octQLo;           // q[0..3]
    vec4  octQHi;           // q[4..7]
} fp;

vec4 gTrap;

// ---- Cayley-Dickson octonion algebra (verified vs structure constants) ------
vec2 cmul(vec2 u, vec2 v) { return vec2(u.x * v.x - u.y * v.y, u.x * v.y + u.y * v.x); }
vec2 cconj(vec2 u)        { return vec2(u.x, -u.y); }

vec4 qmul(vec4 x, vec4 y) {
    vec2 A = x.xy, B = x.zw, C = y.xy, D = y.zw;
    return vec4(cmul(A, C) - cmul(cconj(D), B),
                cmul(D, A) + cmul(B, cconj(C)));
}
vec4 qconj(vec4 x) { return vec4(x.x, -x.y, -x.z, -x.w); }

struct Oct { vec4 lo; vec4 hi; };

Oct omul(Oct a, Oct b) {
    vec4 P = a.lo, Q = a.hi, R = b.lo, S = b.hi;
    Oct r;
    r.lo = qmul(P, R) - qmul(qconj(S), Q);
    r.hi = qmul(S, P) + qmul(Q, qconj(R));
    return r;
}
Oct oadd(Oct a, Oct b)      { return Oct(a.lo + b.lo, a.hi + b.hi); }
Oct osub(Oct a, Oct b)      { return Oct(a.lo - b.lo, a.hi - b.hi); }
Oct oscale(Oct a, float s)  { return Oct(a.lo * s, a.hi * s); }
float onorm(Oct a)          { return sqrt(dot(a.lo, a.lo) + dot(a.hi, a.hi)); }

float estimate(vec3 p) {
    float bailout  = fp.boxParams.x;
    float eps      = fp.boxParams.y;
    float assocNrm = fp.boxParams.z;

    Oct c  = Oct(fp.octCLo, fp.octCHi);
    Oct pp = Oct(fp.octPLo, fp.octPHi);
    Oct qq = Oct(fp.octQLo, fp.octQHi);
    Oct pq = omul(pp, qq);                 // constant inside the loop

    // --- seed mapping: flat 3D slice, or inverse-stereographic (curved) slice ---
    // Flat: the march point seeds octonion axes e0,e1,e2 (a flat 3-plane through
    // the 8D set). Stereographic: wrap R^3 onto the unit 3-sphere S^3, scale by R,
    // and embed in e0..e3 -- a CURVED 3-manifold that cuts the set transversally
    // and surfaces angular structure a flat plane flattens. Because inverse
    // stereographic projection is conformal, the seed-space DE maps back to 3D by
    // a single isotropic factor 1/lambda, lambda = 2kR/(1+k^2|p|^2).
    float stereoMode = fp.surfParams.x;
    float kIn        = fp.surfParams.y;
    float Rsph       = fp.surfParams.z;

    Oct z;
    float deScale = 1.0;
    if (stereoMode > 0.5) {
        vec3 pk = p * kIn;
        float s = dot(pk, pk);
        vec4 q4 = vec4(2.0 * pk, s - 1.0) / (s + 1.0);   // R^3 -> unit S^3
        z = Oct(Rsph * q4, vec4(0.0));                   // embed in e0..e3
        deScale = (1.0 + s) / max(2.0 * kIn * Rsph, 1e-9); // = 1 / lambda
    } else {
        z = Oct(vec4(p, 0.0), vec4(0.0));                // flat slice (e0,e1,e2)
    }

    float dr = 1.0;
    float r  = 0.0;
    gTrap = vec4(1e20);

    for (int i = 0; i < fp.iterations; i++) {
        r = onorm(z);
        if (r > bailout) break;

        // running derivative (Lipschitz bound on the analytic map)
        dr = (2.0 * r + eps * assocNrm) * dr;

        // associator [z,p,q] = (z p) q - z (p q)  -- linear, the octonionic part
        Oct assoc = osub(omul(omul(z, pp), qq), omul(z, pq));

        // M(z) = z^2 + eps*assoc + c
        z = oadd(oadd(omul(z, z), oscale(assoc, eps)), c);

        // orbit traps for the shared cosine palette
        float zl = onorm(z);
        gTrap.x = min(gTrap.x, zl);
        gTrap.y = min(gTrap.y, abs(z.lo.x));
        gTrap.z = min(gTrap.z, length(z.lo.xyz));
        gTrap.w = min(gTrap.w, abs(zl - 1.0));
    }

    return deScale * 0.5 * log(max(r, 1e-12)) * r / max(dr, 1e-12);
}

vec4 attractorBoundingSphere() {
    // Stereographic wraps all of R^3 onto the sphere, so the flat-mode skip sphere
    // would clip the visible form to a disc -- disable the skip in stereo mode.
    if (fp.surfParams.x > 0.5) return vec4(0.0, 0.0, 0.0, 1e6);
    return fp.boundSphere;
}
float deFudge()                { return fp.rot.w; }
