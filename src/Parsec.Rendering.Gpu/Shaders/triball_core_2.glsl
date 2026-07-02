// Parsec mined fold core: sibling_crescent  scale=1.62 boundRadius~3.8  (genome: ([('box', 1.71), ('ball', 0.37, 1.11), ('ball', 0.44, 1.13)], 1.62))
// Mandelbox-family. Linear DE: dr -> |scale|*(fold factors)*dr + 1, DE = |z|/dr,
// freezing each point on bailout. z0 = c = sample point (Mandelbrot convention).
//   boxParams = (scale, bailout, _, _) ; rot.w = DE fudge ; boundSphere = skip sphere
layout(std430, binding = 1) readonly buffer FoldParams {
    int iterations; int mode; int juliaMode; int pad0;
    vec4 boxParams; vec4 surfParams; vec4 juliaC; vec4 rot; vec4 boundSphere;
} fp;

vec4 gTrap;

float estimate(vec3 p) {
    float scale   = fp.boxParams.x;
    float bailout = fp.boxParams.y;
    vec3  z = p;
    vec3  c = p;
    float dr = 1.0;
    float r2, t;
    gTrap = vec4(1e20);

    for (int i = 0; i < fp.iterations; i++) {
        if (length(z) > bailout) break;          // freeze escaped points
        z = clamp(z, -1.7100, 1.7100) * 2.0 - z;   // box fold
        r2 = dot(z, z);                                       // ball fold 0.37/1.11
        if (r2 < 0.13690)      { t = 1.23210/0.13690; z *= t; dr *= t; }
        else if (r2 < 1.23210) { t = 1.23210/r2;        z *= t; dr *= t; }
        r2 = dot(z, z);                                       // ball fold 0.44/1.13
        if (r2 < 0.19360)      { t = 1.27690/0.19360; z *= t; dr *= t; }
        else if (r2 < 1.27690) { t = 1.27690/r2;        z *= t; dr *= t; }
        z = scale * z + c;                        // affine
        dr = dr * abs(scale) + 1.0;
        gTrap.x = min(gTrap.x, length(z));
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(length(z) - 1.0));
    }
    return length(z) / max(dr, 1e-9);             // linear Mandelbox/KIFS DE
}

// Radius-only inflation (see triball_core.glsl): scaling the whole vec4 would
// also move the CENTER; scale just .w.
vec4 attractorBoundingSphere() { return vec4(fp.boundSphere.xyz, fp.boundSphere.w * 50.0); }
float deFudge()                { return fp.rot.w; }
