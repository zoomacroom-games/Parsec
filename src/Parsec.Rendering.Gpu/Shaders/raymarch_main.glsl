// Raymarching entry point. Concatenated after de_core.glsl by the loader.
// Renders one pixel per thread by sphere-tracing the distance estimator and
// shading hits with Lambert + soft shadows + AO. Accumulates LINEAR-LIGHT
// color into a vec4 buffer for SSAA; the finalize pass (RaymarchPipeline)
// divides, clamps, and sRGB-encodes. Authored colors (palette, environment,
// background) are sRGB, so they are decoded to linear before shading -- a
// fully lit surface reproduces its authored color exactly.
//
// Now also supports optional glossy self-reflection via a fixed-depth bounce
// loop (hero-only, per-fractal opt-in through reflectParams). When reflections
// are off (reflectParams.x == 0) the path reduces exactly to the old behavior.

layout(local_size_x = 8, local_size_y = 8) in;

// -----------------------------------------------------------------------------
// Render parameters (binding 4)
// -----------------------------------------------------------------------------

layout(std430, binding = 4) readonly buffer RenderParams {
    int   imageWidth;
    int   imageHeight;
    int   rowOffset;
    int   rowCount;

    vec4  camPos;
    vec4  camForward;
    vec4  camRight;
    vec4  camUp;
    vec4  tanFov;

    vec4  lightDir;
    vec4  background;
    vec4  surface;

    vec4  marchA;        // (hitEpsilon, maxDistance, normalEpsilon, shadowSoftness)
    vec4  marchB;        // (aoStepDistance, aoIntensity, _, _)
    ivec4 marchI;        // (maxSteps, shadowSteps, aoSamples, flags)

    vec4  palBase;
    vec4  palAmp;
    vec4  palPhase;
    vec4  trapMix;

    vec4  subpixelJitter; // (jx, jy, _, _)

    // Glossy reflection controls (hero-only, per-fractal opt-in):
    //   x = enable (0/1), y = max bounces, z = gloss [0,1], w = fresnel F0
    vec4  reflectParams;
} rp;

layout(std430, binding = 5) buffer Accum {
    vec4 colors[];
} accum;

// -----------------------------------------------------------------------------
// Shading helpers
// -----------------------------------------------------------------------------

// Decode an authored (sRGB) color to linear light. Lighting math happens in
// linear space; the finalize pass re-encodes with the inverse transfer.
vec3 srgbToLinear(vec3 c) {
    vec3 lo = c / 12.92;
    vec3 hi = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(lo, hi, step(vec3(0.04045), c));
}

vec3 estimateNormal(vec3 p, float eps, vec3 viewDir, out bool degenerate) {
    vec2 k = vec2(1.0, -1.0);
    vec3 n =
        k.xyy * estimate(p + k.xyy * eps) +
        k.yyx * estimate(p + k.yyx * eps) +
        k.yxy * estimate(p + k.yxy * eps) +
        k.xxx * estimate(p + k.xxx * eps);
    float len = length(n);
    degenerate = !(len > 1e-6);
    return degenerate ? -viewDir : n / len;
}

// Shadow and AO rays apply the same deFudge() the primary march uses: cores
// whose DE overestimates rely on fudge < 1 to not overstep, and an unfudged
// shadow ray steps straight through the thin filigree that the primary ray
// resolves -- light leaks and detached contact shadows.
float softShadow(vec3 origin, vec3 dir, float hitEps, float maxDist,
                 int steps, float softness) {
    float result = 1.0;
    float fudge = deFudge();
    float t = hitEps * 4.0;
    for (int i = 0; i < steps; i++) {
        vec3 p = origin + dir * t;
        float d = estimate(p) * fudge;
        if (d < hitEps) return 0.0;
        result = min(result, softness * d / t);
        t += d;
        if (t > maxDist) break;
    }
    return max(0.0, result);
}

float ambientOcclusion(vec3 p, vec3 normal, float stepDist, float intensity, int samples) {
    float occ = 0.0;
    float weight = 1.0;
    float fudge = deFudge();
    for (int i = 1; i <= samples; i++) {
        float stepLen = float(i) * stepDist;
        vec3 samplePoint = p + normal * stepLen;
        float d = estimate(samplePoint) * fudge;
        occ += (stepLen - d) * weight;
        weight *= 0.5;
    }
    return clamp(1.0 - intensity * max(0.0, occ), 0.0, 1.0);
}

bool intersectSphereForward(vec3 ro, vec3 rd, vec3 center, float radius, out float t) {
    vec3 oc = ro - center;
    float b = dot(oc, rd);
    float c = dot(oc, oc) - radius * radius;
    if (c <= 0.0) { t = 0.0; return true; }
    float disc = b * b - c;
    if (disc < 0.0) { t = 0.0; return false; }
    float sq = sqrt(disc);
    float t0 = -b - sq;
    if (t0 >= 0.0) { t = t0; return true; }
    float t1 = -b + sq;
    if (t1 >= 0.0) { t = t1; return true; }
    t = 0.0;
    return false;
}

vec3 cosPalette(float t, vec3 a, vec3 b, vec3 c, vec3 d) {
    return a + b * cos(6.28318530718 * (c * t + d));
}

vec3 trapAlbedo(vec4 trap) {
    // trapMix is (origin, axis, plane) but the core family packs gTrap as
    // (origin, PLANE |z.x|, AXIS |z.xy|, shell) -- so axis weight rides trap.z
    // and plane weight rides trap.y. Deliberate cross-mapping, not a bug.
    float tIn = rp.trapMix.x * trap.x
              + rp.trapMix.y * trap.z
              + rp.trapMix.z * trap.y;
    float t = fract(tIn * rp.palAmp.a);

    float freq = rp.palBase.a;
    vec3 col = cosPalette(t,
        rp.palBase.rgb,
        rp.palAmp.rgb,
        vec3(freq),
        rp.palPhase.rgb);

    float shell = 1.0 - clamp(trap.w * 2.0, 0.0, 1.0);
    col = mix(col, vec3(0.95, 0.93, 0.88), shell * rp.palPhase.a);
    // Palette colors are authored in sRGB; decode so the lighting multiply
    // happens in linear space.
    return srgbToLinear(clamp(col, 0.0, 1.0));
}

// -----------------------------------------------------------------------------
// Reflection environment. Sampled only when a REFLECTION ray (bounce > 0) misses
// the fractal -- gives glossy surfaces something to reflect at edges where they
// tilt away from the body. Primary-ray misses still return the flat background,
// so the composition (fractal on dark bg) is unchanged. Tunable gradient:
// warm low, cool high, plus a soft specular sun toward the key light.
// -----------------------------------------------------------------------------
vec3 environment(vec3 rd) {
    float h = clamp(rd.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 low  = vec3(0.30, 0.22, 0.16);   // warm horizon (authored sRGB)
    vec3 high = vec3(0.12, 0.16, 0.24);   // cool zenith (authored sRGB)
    vec3 env = srgbToLinear(mix(low, high, h));
    float sun = max(0.0, dot(rd, rp.lightDir.xyz));
    float intensity = max(rp.lightDir.w, 0.0);
    env += srgbToLinear(vec3(0.60, 0.50, 0.40)) * pow(sun, 48.0) * intensity;
    return env;
}

// -----------------------------------------------------------------------------
// Hit record + ray tracer. traceRay sphere-skips, marches, and (on hit) fills
// position/normal/albedo. Pulled out of main() so the bounce loop can call it
// for both the primary ray and each reflection ray.
// -----------------------------------------------------------------------------
struct Hit {
    bool  hit;
    vec3  pos;
    vec3  normal;
    vec3  albedo;
    float t;
};

Hit traceRay(vec3 ro, vec3 rd, float hitEps, float maxDist, float normalEps, int maxSteps) {
    Hit h;
    h.hit = false; h.pos = vec3(0.0); h.normal = vec3(0.0);
    h.albedo = vec3(0.0); h.t = maxDist;

    vec4 bsphere = attractorBoundingSphere();
    float tEnter;
    if (!intersectSphereForward(ro, rd, bsphere.xyz, bsphere.w, tEnter)) return h;

    float t = max(0.0, tEnter);
    bool hit = false;
    float fudge = deFudge();
    int i;
    float lastD = 1e9;
    for (i = 0; i < maxSteps; i++) {
        vec3 p = ro + rd * t;
        float d = estimate(p) * fudge;
        // Cone-scaled hit epsilon: stop when the surface is within ~half a
        // pixel's world-size at this distance, so detail resolves with
        // resolution/zoom instead of a fixed world threshold. hitEps acts as a
        // floor (finest allowed / float-noise guard). Low-res preview yields a
        // naturally coarse eps (stays cheap); high-res hero sharpens itself.
        float pixelWorld = (2.0 * rp.tanFov.y / float(rp.imageHeight)) * t;
        float effectiveEps = max(hitEps, 0.5 * pixelWorld);
        if (d < effectiveEps) { hit = true; break; }
        lastD = d;
        t += d;
        if (t > maxDist) break;
    }
    // Step-exhaustion rescue: a ray that ran out of steps while already
    // converged to within a few pixel-footprints counts as a hit. Uses the
    // same cone-scaled epsilon as hit acceptance -- against the raw hitEps
    // floor (1e-6 default), distant converged rays read as misses and speckle
    // far detail with background pinholes.
    if (!hit && i >= maxSteps && t <= maxDist) {
        float pixelWorldEnd = (2.0 * rp.tanFov.y / float(rp.imageHeight)) * t;
        float effEpsEnd = max(hitEps, 0.5 * pixelWorldEnd);
        if (lastD < effEpsEnd * 4.0) hit = true;
    }
    if (!hit) return h;

    vec3 hitPoint = ro + rd * t;
    // Capture the orbit trap at the hit BEFORE normal estimation (which calls
    // estimate() four times and clobbers gTrap).
    estimate(hitPoint);
    vec3 albedo = trapAlbedo(gTrap);
    bool degenerate;
    // Match the normal sampling radius to the pixel footprint at the hit, so
    // relief finer than a fixed normalEps isn't averaged away at deep zoom.
    // normalEps is the floor (float-noise guard; normals sparkle below it).
    float pixelWorldHit = (2.0 * rp.tanFov.y / float(rp.imageHeight)) * t;
    float nEps = max(normalEps, 0.5 * pixelWorldHit);
    vec3 normal = estimateNormal(hitPoint, nEps, rd, degenerate);

    h.hit = true; h.pos = hitPoint; h.normal = normal; h.albedo = albedo; h.t = t;
    return h;
}

// Direct lighting at a hit: Lambert + soft shadow + AO + ambient. (The shading
// that used to live inline in main().)
vec3 shadeDirect(Hit h, float hitEps, float maxDist) {
    float normalEps  = rp.marchA.z;  // unused here but kept for symmetry
    float shadowSoft = rp.marchA.w;
    float aoStep     = rp.marchB.x;
    float aoIntensity = rp.marchB.y;
    int   shadowSteps = rp.marchI.y;
    int   aoSamples  = rp.marchI.z;
    int   flags      = rp.marchI.w;
    bool  softShadowsOn = (flags & 1) != 0;
    bool  aoOn       = (flags & 2) != 0;

    vec3 offsetPoint = h.pos + h.normal * hitEps * 4.0;
    float lambert = clamp(dot(h.normal, rp.lightDir.xyz), 0.0, 1.0);

    float shadow = 1.0;
    if (softShadowsOn && lambert > 0.0)
        shadow = softShadow(offsetPoint, rp.lightDir.xyz, hitEps, maxDist,
                            shadowSteps, shadowSoft);

    float ao = 1.0;
    if (aoOn)
        ao = ambientOcclusion(offsetPoint, h.normal, aoStep, aoIntensity, aoSamples);

    // Key-light intensity rides in lightDir.w (unused .w slot, no struct change).
    // Used directly: 0 really means "key light off, ambient fill only" (the UI
    // slider goes to 0), negatives clamp to 0.
    float intensity = max(rp.lightDir.w, 0.0);
    // AO attenuates only the ambient (indirect) term -- the key light is already
    // gated by its own shadow ray, and multiplying it by AO too double-darkens
    // every lit pixel. No clamp here: the accumulator is HDR, so intensity > 1
    // has real headroom; the finalize pass clamps and sRGB-encodes.
    const float ambient = 0.25;
    float lighting = ambient * ao + (1.0 - ambient) * lambert * shadow * intensity;
    return h.albedo * lighting;
}

void main() {
    uint gx = gl_GlobalInvocationID.x;
    uint gy = gl_GlobalInvocationID.y;
    int px = int(gx);
    int py = int(rp.rowOffset) + int(gy);

    if (px >= rp.imageWidth || gy >= uint(rp.rowCount) || py >= rp.imageHeight) return;

    float hitEps    = rp.marchA.x;
    float maxDist   = rp.marchA.y;
    float normalEps = rp.marchA.z;
    int   maxSteps  = rp.marchI.x;

    // Primary ray (with sub-pixel jitter for SSAA).
    float u = (float(px) + 0.5 + rp.subpixelJitter.x) / float(rp.imageWidth);
    float v = 1.0 - (float(py) + 0.5 + rp.subpixelJitter.y) / float(rp.imageHeight);
    float x = (2.0 * u - 1.0) * rp.tanFov.x;
    float y = (2.0 * v - 1.0) * rp.tanFov.y;
    vec3 ro = rp.camPos.xyz;
    vec3 rd = normalize(rp.camForward.xyz + x * rp.camRight.xyz + y * rp.camUp.xyz);

    // Reflection controls.
    bool  reflectOn  = rp.reflectParams.x > 0.5;
    int   maxBounces = int(rp.reflectParams.y);
    float gloss      = rp.reflectParams.z;
    float F0         = rp.reflectParams.w;

    vec3 color = vec3(0.0);
    vec3 throughput = vec3(1.0);

    // Fixed-depth bounce loop. With reflectOn == false this runs exactly once
    // and reduces to the original single-bounce shading.
    for (int bounce = 0; bounce <= maxBounces; bounce++) {
        Hit h = traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps);

        if (!h.hit) {
            // Primary miss -> flat background (keeps the composition). Reflection
            // miss -> environment gradient (so glossy edges reflect a "world").
            color += throughput * (bounce == 0 ? rp.background.rgb : environment(rd));
            break;
        }

        vec3 direct = shadeDirect(h, hitEps, maxDist);

        // Last allowed bounce, or reflections disabled: deposit direct and stop.
        if (!reflectOn || bounce == maxBounces) {
            color += throughput * direct;
            break;
        }

        // Schlick fresnel: more reflective at grazing angles. cosTheta uses the
        // incoming direction against the surface normal.
        float cosTheta = clamp(dot(-rd, h.normal), 0.0, 1.0);
        float fresnel = F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
        float reflectWeight = clamp(gloss * fresnel, 0.0, 1.0);

        // Split: diffuse part shaded now, reflective part carried to next bounce.
        // Reflections are left untinted (dielectric / ceramic look) -- a metal
        // tint would multiply throughput by albedo here.
        color += throughput * (1.0 - reflectWeight) * direct;
        throughput *= reflectWeight;

        // Offset along the normal to avoid immediately re-hitting this surface.
        ro = h.pos + h.normal * hitEps * 4.0;
        rd = reflect(rd, h.normal);

        // Negligible-throughput early-out.
        if (max(throughput.r, max(throughput.g, throughput.b)) < 0.01) break;
    }

    int pixelIndex = py * rp.imageWidth + px;
    accum.colors[pixelIndex] += vec4(color, 1.0);
}
