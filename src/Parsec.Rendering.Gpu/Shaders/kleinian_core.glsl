// =============================================================================
// Parsec pseudo-Kleinian (inversive limit set) distance estimator - core
// =============================================================================
//
// Includable chunk (no #version, no main). Concatenated after a #version line
// and before an entry-point main(), like the other DE cores.
//
// This is the inversive-limit-set family: box folds (plane reflections) plus a
// sphere INVERSION, iterated, whose attractor is a 3D Kleinian/Apollonian foam
// (nested packed spheres). Unlike the Mandelbox/KIFS, this family has NO stable
// analytic scalar-derivative DE -- the real renderers (Mandelbulb3D,
// Mandelbulber) use a NUMERICAL GRADIENT instead, and so do we. We validated in
// Python (kleinian_numgrad.py) that the analytic |z|/dr collapses the field to
// solid, while the numerical-gradient DE below yields a sphere-traceable field
// with genuine Kleinian foam.
//
// METHOD (Hart / Quilez distance-to-level-set):
//   potential V(p) = log(length(orbit_end(p)))   -- smooth, grows away from set
//   DE(p)        ~= |V(p)| / |grad V(p)|          -- grad by central differences
// estimate() computes this by calling kleinianPotential() 7 times (the point
// plus 6 axis offsets). This keeps the estimate(p)->float contract intact, so
// the whole raymarch pipeline, orbit-trap colour, camera and hero render are
// reused unchanged -- the cost is simply a heavier estimate().
//
// PARAMETER REUSE (shared FoldParams buffer, binding 1):
//   boxParams  = (scale, cell, minRadius, fixedRadius)
//   juliaC     = (offset.xyz, _)        -- the tiling offset that makes the set
//   rot        = (_, _, _, fudge)
//   boundSphere= (cx, cy, cz, r)

layout(std430, binding = 1) readonly buffer FoldParams {
    int   iterations;
    int   mode;             // unused
    int   juliaMode;        // unused
    int   pad0;

    vec4  boxParams;        // (scale, cell, minRadius, fixedRadius)
    vec4  surfParams;       // unused here
    vec4  juliaC;           // (offsetX, offsetY, offsetZ, _)
    vec4  rot;              // (_, _, _, fudge)
    vec4  boundSphere;      // (cx, cy, cz, r)
} fp;

// Orbit-trap accumulator (shared shading reads this; see kifs_core.glsl).
vec4 gTrap;

// One Kleinian iteration potential: invert, box-fold, scale+offset, repeat;
// return log of the final radius. Smooth and grows with distance from the
// (bounded) limit set, so its level set V=0-ish wraps the foam.
float kleinianPotential(vec3 p) {
    float scale = fp.boxParams.x;
    float cell  = fp.boxParams.y;
    float minR2 = fp.boxParams.z * fp.boxParams.z;
    float fixedR2 = fp.boxParams.w * fp.boxParams.w;
    vec3  c = fp.juliaC.xyz;

    vec3 z = p;
    for (int i = 0; i < fp.iterations; i++) {
        // Sphere inversion with inner linear zone.
        float r2 = dot(z, z);
        if (r2 < minR2) {
            z *= (fixedR2 / minR2);
        } else if (r2 < fixedR2) {
            z *= (fixedR2 / r2);
        }
        // Box fold + similarity + offset.
        z = clamp(z, -cell, cell) * 2.0 - z;
        z = z * scale + c;
    }
    return log(max(length(z), 1e-12));
}

// Numerical-gradient distance estimate: |V| / |grad V|, central differences.
// Also fills gTrap by running one orbit and tracking the usual trap minima, so
// orbit-trap colour works for this family too.
float estimate(vec3 p) {
    const float EPS = 1e-4;

    float v  = kleinianPotential(p);
    float vx = kleinianPotential(p + vec3(EPS, 0.0, 0.0))
             - kleinianPotential(p - vec3(EPS, 0.0, 0.0));
    float vy = kleinianPotential(p + vec3(0.0, EPS, 0.0))
             - kleinianPotential(p - vec3(0.0, EPS, 0.0));
    float vz = kleinianPotential(p + vec3(0.0, 0.0, EPS))
             - kleinianPotential(p - vec3(0.0, 0.0, EPS));
    vec3 grad = vec3(vx, vy, vz) / (2.0 * EPS);
    float g = length(grad);

    // Orbit traps: re-run the iteration at p, tracking the trap minima.
    {
        float scale = fp.boxParams.x;
        float cell  = fp.boxParams.y;
        float minR2 = fp.boxParams.z * fp.boxParams.z;
        float fixedR2 = fp.boxParams.w * fp.boxParams.w;
        vec3  c = fp.juliaC.xyz;
        vec3 z = p;
        gTrap = vec4(1e20);
        for (int i = 0; i < fp.iterations; i++) {
            float r2 = dot(z, z);
            if (r2 < minR2)        z *= (fixedR2 / minR2);
            else if (r2 < fixedR2) z *= (fixedR2 / r2);
            z = clamp(z, -cell, cell) * 2.0 - z;
            z = z * scale + c;
            float rz = length(z);
            gTrap.x = min(gTrap.x, rz);
            gTrap.y = min(gTrap.y, abs(z.x));
            gTrap.z = min(gTrap.z, length(z.xy));
            gTrap.w = min(gTrap.w, abs(rz - 1.0));
        }
    }

    // |V|/|grad V| is unbounded near SADDLES of V -- exactly the gaps between
    // foam spheres, where g -> 0 with v finite. An uncapped ratio (or the old
    // g~0 fallback of 1e3) takes one giant step that tunnels through geometry
    // behind the gap, punching holes/sparkle into the foam. Cap the step at
    // half a cell, the foam's natural feature scale: rays cross a gap in a few
    // short steps instead of one unbounded one.
    float stepCap = max(0.5 * fp.boxParams.y, 1e-3);
    if (g < 1e-12) return stepCap;
    return min(abs(v) / g, stepCap);
}

vec4 attractorBoundingSphere() {
    return fp.boundSphere;
}

float deFudge() {
    return fp.rot.w;
}
