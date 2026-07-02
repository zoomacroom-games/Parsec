// Parsec mined fold core: ridge_crescent scale=2.50 boundRadius~3.0 (coh3d=0.93 frac3d=0.23)  (genome: box1.44 ball0.73/1.12 s=2.50)
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
        z = clamp(z, -1.4438, 1.4438) * 2.0 - z;   // box fold
        r2 = dot(z, z);                                       // ball fold 0.73/1.12
        if (r2 < 0.53356)      { t = 1.26256/0.53356; z *= t; dr *= t; }
        else if (r2 < 1.26256) { t = 1.26256/r2;        z *= t; dr *= t; }
        z = scale * z + c;                        // affine
        dr = dr * abs(scale) + 1.0;
        gTrap.x = min(gTrap.x, length(z));
        gTrap.y = min(gTrap.y, abs(z.x));
        gTrap.z = min(gTrap.z, length(z.xy));
        gTrap.w = min(gTrap.w, abs(length(z) - 1.0));
    }
    return length(z) / max(dr, 1e-9);             // linear Mandelbox/KIFS DE
}

// Radius-only inflation: the C#-side BoundRadius clips this mined variant, so
// the shader widens the skip sphere. Scaling the whole vec4 would also move
// the CENTER (fine only while it is the origin); scale just .w.
vec4 attractorBoundingSphere() { return vec4(fp.boundSphere.xyz, fp.boundSphere.w * 100.0); }
float deFudge()                { return fp.rot.w; }
