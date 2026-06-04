// =============================================================================
// Parsec 3D Burning Ship distance estimator - core
// =============================================================================
//
// Includable chunk (no #version, no main). Concatenated after a #version line
// and before raymarch_main.glsl, like the other DE cores.
//
// 3D Burning Ship: the same triplex z -> z^n + c power map as the Mandelbulb,
// but in a y-up angular convention (polar angle measured from +Y) and with an
// abs() fold applied to every component AFTER the +c, exactly as in the posted
// reference formula:
//     theta  = atan2(sqrt(x*x+z*z), y) * n      (polar from +Y)
//     zangle = atan2(x, z) * n                  (azimuth in the x-z plane)
//     x = abs( sin(zangle)*sin(theta)*r + c.x )
//     y = abs( cos(theta)*r          + c.y )
//     z = abs( sin(theta)*cos(zangle)*r + c.z )
// The abs() folds are what make this a "burning ship" rather than a Mandelbulb;
// they produce the terraced, vertically-mirror-symmetric massing.
//
// DE STRATEGY: scalar-derivative escape-time, identical in form to the
// Mandelbulb core (dr -> n*r^(n-1)*dr + 1, DE = 0.5*log(r)*r/dr), tracked in
// LOG space (ldr) to stay overflow-proof past ~iter 43. An abs() reflection is
// an isometry, so it does NOT change the magnitude of the derivative -> the dr
// recurrence is unchanged. This was checked in Python (burningship_proto.py):
//   - z=0 slice bounded fraction ~0.30 (rich, non-degenerate);
//   - scalar dr is strongly conservative at the median (dr/sigma_max ~ 23) but
//     is NOT a strict lower bound near the spherical-coordinate poles (the same
//     intrinsic limitation as the Mandelbulb), so the DEFAULT fudge is < 1 and
//     a coarse-eps hero arm is available (cf. Riemann Sphere) if creases tear.
//
// PARAMETER REUSE (shared FoldParams buffer, binding 1) -- identical slot map to
// the Mandelbulb core, so the C# packing is a clone:
//   iterations   = iteration count
//   boxParams.x  = power (n)
//   boxParams.y  = bailout radius
//   rot.w        = DE fudge
//   boundSphere  = bounding sphere for the fast skip

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // unused
    int   juliaMode;        // unused
    int   pad0;

    vec4  boxParams;        // (power, bailout, _, _)
    vec4  surfParams;       // unused
    vec4  juliaC;           // unused
    vec4  rot;              // (_, _, _, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)
} fp;

vec4 gTrap;

float estimate(vec3 p) {
    float power   = fp.boxParams.x;
    float bailout = fp.boxParams.y;

    vec3 z = p;
    float ldr = 0.0;       // log of the running derivative (dr = exp(ldr))
    float r = 0.0;
    gTrap = vec4(1e20);

    for (int i = 0; i < fp.iterations; i++) {
        r = length(z);
        if (r > bailout) break;

        // Spherical coords, y-up: polar angle from +Y, azimuth in the x-z plane.
        float sq_xz  = sqrt(z.x * z.x + z.z * z.z);
        float theta  = atan(sq_xz, z.y);     // = atan2(sqrt(x^2+z^2), y)
        float zangle = atan(z.x, z.z);        // = atan2(x, z)

        // Running derivative in LOG space (same as Mandelbulb; abs() preserves
        // magnitude). dr = n*r^(n-1)*dr + 1, with the "+1" carried exactly via
        // logaddexp(x,0) = x + log(1+exp(-x)). Validated == direct to ~1e-14.
        float x = ldr + log(power) + (power - 1.0) * log(max(r, 1e-12));
        ldr = x + log(1.0 + exp(-x));

        // Raise to the power: scale radius, multiply angles, rebuild, add c (=p),
        // then the burning-ship abs() fold.
        float zr = pow(r, power);
        theta  *= power;
        zangle *= power;
        z = zr * vec3(sin(zangle) * sin(theta),
                      cos(theta),
                      sin(theta)  * cos(zangle)) + p;
        z = abs(z);

        // Orbit traps for the shared palette.
        gTrap.x = min(gTrap.x, length(z));
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(length(z) - 1.0));
    }

    // DE = 0.5*log(r)*r / dr = 0.5*log(r)*r*exp(-ldr).
    return 0.5 * log(max(r, 1e-12)) * r * exp(-ldr);
}

vec4 attractorBoundingSphere() {
    return fp.boundSphere;
}

float deFudge() {
    return fp.rot.w;
}
