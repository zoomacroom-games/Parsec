// Parsec mined fold core: sibling_rosette  scale=1.36 boundRadius~6.0  (genome: ([('ball', 0.76, 1.29), ('box', 1.18), ('ball', 0.54, 1.41), ('ball', 0.67, 1.07)], 1.36))
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
        r2 = dot(z, z);                                       // ball fold 0.76/1.29
        if (r2 < 0.57760)      { t = 1.66410/0.57760; z *= t; dr *= t; }
        else if (r2 < 1.66410) { t = 1.66410/r2;        z *= t; dr *= t; }
        z = clamp(z, -1.1800, 1.1800) * 2.0 - z;   // box fold
        r2 = dot(z, z);                                       // ball fold 0.54/1.41
        if (r2 < 0.29160)      { t = 1.98810/0.29160; z *= t; dr *= t; }
        else if (r2 < 1.98810) { t = 1.98810/r2;        z *= t; dr *= t; }
        r2 = dot(z, z);                                       // ball fold 0.67/1.07
        if (r2 < 0.44890)      { t = 1.14490/0.44890; z *= t; dr *= t; }
        else if (r2 < 1.14490) { t = 1.14490/r2;        z *= t; dr *= t; }
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
