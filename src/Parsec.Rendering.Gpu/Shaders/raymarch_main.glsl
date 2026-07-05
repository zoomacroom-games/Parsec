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

    // Environment: procedural skybox + reflective floor plane (both optional;
    // zeroed = legacy flat background, no floor, byte-identical output).
    vec4  skyParams;     // (skyMode 0/1, sun intensity, sun sharpness, floor enable 0/1)
    vec4  skyZenith;     // (zenith rgb authored sRGB, floor height)
    vec4  skyHorizon;    // (horizon rgb authored sRGB, floor reflectivity 0..1)
    vec4  skyGround;     // (ground rgb authored sRGB, floor checker scale, 0 = plain)
    vec4  floorColor;    // (floor rgb authored sRGB, _)
} rp;

layout(std430, binding = 5) buffer Accum {
    vec4 colors[];
} accum;

// -----------------------------------------------------------------------------
// Placeable orb lights (binding 10). Each orb is BOTH an emissive sphere the
// ray can hit directly (a visible luminous ball) and a point light that adds
// diffuse illumination to every fractal hit. The active count rides
// rp.marchB.z (a previously spare slot); RaymarchPipeline uploads all slots
// every render, zeroed past the count.
// -----------------------------------------------------------------------------

struct OrbLight {
    vec4 posRad;    // (center.xyz, radius)
    vec4 colorLum;  // (rgb authored-sRGB tint, luminosity)
};

layout(std430, binding = 10) readonly buffer OrbData {
    OrbLight orbs[];
} ob;

int orbCount() {
    return int(rp.marchB.z + 0.5);
}

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
// Procedural skybox (skyParams.x == 1). Replaces BOTH the primary-miss
// background and the reflection environment, so the sky seen behind the
// fractal is the same sky its reflections see. Three authored colors blended
// by ray elevation, plus a sun glow + disc tracking the key light direction.
// -----------------------------------------------------------------------------
vec3 skyColor(vec3 rd) {
    vec3 zen = srgbToLinear(rp.skyZenith.rgb);
    vec3 hor = srgbToLinear(rp.skyHorizon.rgb);
    vec3 gnd = srgbToLinear(rp.skyGround.rgb);
    // Below-horizon exponent kept gentle near 0 so the strip of sky between
    // the horizon and the floor's distance-fog stays horizon-colored (a
    // steeper curve leaves a visible seam against the fogged floor).
    vec3 sky = (rd.y >= 0.0)
        ? mix(hor, zen, pow(clamp(rd.y, 0.0, 1.0), 0.45))
        : mix(hor, gnd, pow(clamp(-rd.y, 0.0, 1.0), 1.5));
    float sun = max(0.0, dot(rd, rp.lightDir.xyz));
    float sharp = max(rp.skyParams.z, 1.0);
    float glow = pow(sun, sharp);           // broad halo
    float disc = pow(sun, sharp * 32.0);    // hot core
    sky += (vec3(1.0, 0.85, 0.70) * glow + vec3(1.0, 0.92, 0.80) * disc * 3.0)
           * max(rp.skyParams.y, 0.0);
    return sky;
}

// -----------------------------------------------------------------------------
// Hit record + ray tracer. traceRay sphere-skips, marches, and (on hit) fills
// position/normal/albedo. Pulled out of main() so the bounce loop can call it
// for both the primary ray and each reflection ray.
// -----------------------------------------------------------------------------
struct Hit {
    bool  hit;
    bool  emissive;   // hit an orb light: shade as emission, terminate path
    bool  isFloor;    // hit the analytic floor plane (own reflectivity)
    vec3  pos;
    vec3  normal;
    vec3  albedo;
    vec3  emission;   // linear-light radiance when emissive
    float t;
};

// Nearest forward orb intersection in (0, tLimit). Skips orbs the origin is
// inside, so the camera (or a reflection origin) can see out of an orb it
// happens to sit in. Returns updated tLimit and writes the emissive fields.
void intersectOrbs(vec3 ro, vec3 rd, inout Hit h, float tLimit) {
    int n = orbCount();
    for (int i = 0; i < n; i++) {
        vec3  oc   = ob.orbs[i].posRad.xyz;
        float orad = ob.orbs[i].posRad.w;
        if (orad <= 0.0) continue;
        if (length(ro - oc) <= orad) continue;   // inside: see through it
        float tOrb;
        if (!intersectSphereForward(ro, rd, oc, orad, tOrb)) continue;
        if (tOrb <= 0.0 || tOrb >= tLimit) continue;
        tLimit = tOrb;
        vec3 pos = ro + rd * tOrb;
        vec3 nrm = normalize(pos - oc);
        // Limb falloff: full luminosity face-on, dimmer toward the edge, so
        // the disc reads as a glowing sphere rather than a flat cutout.
        float ndv = clamp(dot(nrm, -rd), 0.0, 1.0);
        float lum = ob.orbs[i].colorLum.a;
        h.hit = true; h.emissive = true;
        h.t = tOrb; h.pos = pos; h.normal = nrm;
        h.emission = srgbToLinear(ob.orbs[i].colorLum.rgb) * lum * (0.35 + 0.65 * ndv);
    }
}

Hit traceRay(vec3 ro, vec3 rd, float hitEps, float maxDist, float normalEps, int maxSteps) {
    Hit h;
    h.hit = false; h.emissive = false; h.isFloor = false;
    h.pos = vec3(0.0); h.normal = vec3(0.0);
    h.albedo = vec3(0.0); h.emission = vec3(0.0); h.t = maxDist;

    vec4 bsphere = attractorBoundingSphere();
    float tEnter;
    if (intersectSphereForward(ro, rd, bsphere.xyz, bsphere.w, tEnter)) {
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
            // resolution/zoom instead of a fixed world threshold. hitEps acts as
            // a floor (finest allowed / float-noise guard). Low-res preview
            // yields a naturally coarse eps (stays cheap); high-res hero
            // sharpens itself.
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
        // floor (1e-6 default), distant converged rays read as misses and
        // speckle far detail with background pinholes.
        if (!hit && i >= maxSteps && t <= maxDist) {
            float pixelWorldEnd = (2.0 * rp.tanFov.y / float(rp.imageHeight)) * t;
            float effEpsEnd = max(hitEps, 0.5 * pixelWorldEnd);
            if (lastD < effEpsEnd * 4.0) hit = true;
        }

        if (hit) {
            vec3 hitPoint = ro + rd * t;
            // Capture the orbit trap at the hit BEFORE normal estimation (which
            // calls estimate() four times and clobbers gTrap).
            estimate(hitPoint);
            vec3 albedo = trapAlbedo(gTrap);
            bool degenerate;
            // Match the normal sampling radius to the pixel footprint at the
            // hit, so relief finer than a fixed normalEps isn't averaged away at
            // deep zoom. normalEps is the floor (float-noise guard; normals
            // sparkle below it).
            float pixelWorldHit = (2.0 * rp.tanFov.y / float(rp.imageHeight)) * t;
            float nEps = max(normalEps, 0.5 * pixelWorldHit);
            vec3 normal = estimateNormal(hitPoint, nEps, rd, degenerate);

            h.hit = true; h.pos = hitPoint; h.normal = normal;
            h.albedo = albedo; h.t = t;
        }
    }

    // Orb lights: analytic spheres, tested independently of the DE march so
    // they are visible outside the attractor's bounding sphere and where the
    // fractal was missed. A nearer orb overrides the fractal hit.
    intersectOrbs(ro, rd, h, h.hit ? h.t : maxDist);

    // Floor plane (skyParams.w == 1): analytic horizontal plane at
    // y = skyZenith.w, exact intersection (no marching), two-sided. A nearer
    // plane hit overrides whatever the ray found so far. The fractal's DE
    // still drives the floor's shadows and contact AO via shadeDirect.
    if (rp.skyParams.w > 0.5 && abs(rd.y) > 1e-6) {
        float floorH = rp.skyZenith.w;
        float tPlane = (floorH - ro.y) / rd.y;
        float tLimit = h.hit ? h.t : maxDist;
        if (tPlane > hitEps * 4.0 && tPlane < tLimit) {
            vec3 pos = ro + rd * tPlane;
            vec3 fc = srgbToLinear(rp.floorColor.rgb);
            float checker = rp.skyGround.w;
            if (checker > 0.0) {
                vec2 q = floor(pos.xz * checker);
                fc *= mix(0.70, 1.0, mod(q.x + q.y, 2.0));
            }
            h.hit = true; h.emissive = false; h.isFloor = true;
            h.t = tPlane; h.pos = pos;
            h.normal = vec3(0.0, (ro.y >= floorH) ? 1.0 : -1.0, 0.0);
            h.albedo = fc;
        }
    }
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

    // Placeable orb lights: diffuse point lights with inverse-square falloff,
    // shadowed by the same soft-shadow march as the key light (and skipped in
    // preview along with it). The shadow march is capped at the orb's surface
    // so a light never occludes itself.
    vec3 orbLight = vec3(0.0);
    int n = orbCount();
    for (int i = 0; i < n; i++) {
        float orad = ob.orbs[i].posRad.w;
        float lum  = ob.orbs[i].colorLum.a;
        if (orad <= 0.0 || lum <= 0.0) continue;
        vec3 toL = ob.orbs[i].posRad.xyz - h.pos;
        float dist = length(toL);
        if (dist < 1e-5) continue;
        vec3 L = toL / dist;
        float lam = clamp(dot(h.normal, L), 0.0, 1.0);
        if (lam <= 0.0) continue;
        float atten = lum / (1.0 + dist * dist);
        float sh = 1.0;
        if (softShadowsOn) {
            float reach = max(dist - orad - hitEps * 8.0, 0.0);
            sh = softShadow(offsetPoint, L, hitEps, reach, shadowSteps, shadowSoft);
        }
        orbLight += srgbToLinear(ob.orbs[i].colorLum.rgb) * (lam * sh * atten);
    }

    return h.albedo * (vec3(lighting) + orbLight);
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

    // Thin-lens depth of field. tanFov.z = focus distance, tanFov.w = aperture
    // radius; subpixelJitter.zw = this AA sample's unit-disc lens point (zeroed
    // by the pipeline at 1 sample, so previews stay a sharp pinhole). All rays
    // through the lens converge on the focal PLANE (the forward-projection
    // divide keeps it flat), so geometry there is sharp and blur grows with
    // distance from it. Averaging over the SSAA samples produces the bokeh.
    float aperture = rp.tanFov.w;
    vec2 lens = rp.subpixelJitter.zw;
    if (aperture > 0.0 && (lens.x != 0.0 || lens.y != 0.0)) {
        float ft = rp.tanFov.z / max(dot(rd, rp.camForward.xyz), 1e-4);
        vec3 focusPoint = ro + rd * ft;
        ro += (rp.camRight.xyz * lens.x + rp.camUp.xyz * lens.y) * aperture;
        rd = normalize(focusPoint - ro);
    }

    // Reflection controls.
    bool  reflectOn  = rp.reflectParams.x > 0.5;
    int   maxBounces = int(rp.reflectParams.y);
    float gloss      = rp.reflectParams.z;
    float F0         = rp.reflectParams.w;
    bool  skyOn      = rp.skyParams.x > 0.5;

    // The floor's reflectivity works independently of the fractal's glossy
    // reflections: a reflective floor gets at least one bounce so it can
    // mirror the fractal even with reflections otherwise disabled.
    bool floorReflective = rp.skyParams.w > 0.5 && rp.skyHorizon.w > 0.0;
    int effMaxBounces = max(reflectOn ? maxBounces : 0, floorReflective ? 1 : 0);

    vec3 color = vec3(0.0);
    vec3 throughput = vec3(1.0);

    // Fixed-depth bounce loop. With reflections and the floor both off this
    // runs exactly once and reduces to the original single-bounce shading.
    for (int bounce = 0; bounce <= effMaxBounces; bounce++) {
        Hit h = traceRay(ro, rd, hitEps, maxDist, normalEps, maxSteps);

        if (!h.hit) {
            // With the skybox on, misses see the same sky everywhere (primary
            // and reflections). Legacy: primary miss -> flat background,
            // reflection miss -> environment gradient.
            color += throughput * (skyOn ? skyColor(rd)
                                 : (bounce == 0 ? rp.background.rgb : environment(rd)));
            break;
        }

        // Orb lights are pure emitters: deposit their radiance and terminate
        // the path (visible directly and in reflections; nothing bounces off).
        if (h.emissive) {
            color += throughput * h.emission;
            break;
        }

        vec3 direct = shadeDirect(h, hitEps, maxDist);

        // Distance fog on the floor: an infinite plane otherwise ends in a
        // hard circle where t exceeds maxDist, with a visible seam against
        // the sky. Fade distant floor into the horizon (at this azimuth) so
        // it melts into the sky instead; the fog also damps reflectivity so
        // the haze isn't mirror-sharp.
        float floorFog = 0.0;
        if (h.isFloor) {
            floorFog = smoothstep(0.25, 1.0, h.t / maxDist);
            if (floorFog > 0.0) {
                vec3 hazeCol;
                if (skyOn) {
                    vec2 az = rd.xz;
                    float l = max(length(az), 1e-4);
                    hazeCol = skyColor(vec3(az.x / l, 0.0, az.y / l));
                } else {
                    hazeCol = rp.background.rgb;
                }
                direct = mix(direct, hazeCol, floorFog);
            }
        }

        // Per-surface reflectivity: the floor uses its own strength (Schlick
        // against water-like F0, blended toward full mirror as the slider
        // rises); fractal surfaces use the glossy-reflection controls.
        float reflectWeight = 0.0;
        if (bounce < effMaxBounces) {
            float cosTheta = clamp(dot(-rd, h.normal), 0.0, 1.0);
            if (h.isFloor) {
                float refl = clamp(rp.skyHorizon.w, 0.0, 1.0);
                float fresnel = 0.04 + 0.96 * pow(1.0 - cosTheta, 5.0);
                reflectWeight = clamp(refl * mix(fresnel, 1.0, refl), 0.0, 1.0)
                              * (1.0 - floorFog);
            } else if (reflectOn) {
                float fresnel = F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
                reflectWeight = clamp(gloss * fresnel, 0.0, 1.0);
            }
        }

        // No reflective continuation: deposit direct and stop.
        if (reflectWeight <= 0.0) {
            color += throughput * direct;
            break;
        }

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
