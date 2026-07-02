// =============================================================================
// Parsec IFS distance estimator - shared core
// =============================================================================
//
// This is an includable chunk (no #version, no main). Both the DE-validation
// shader and the raymarching shader concatenate this after their own #version
// line and before their own main(). It defines:
//   - the IFS SSBO (binding 0) and Query SSBO (binding 9)
//   - affine transform ops on packed 3x4 matrices
//   - estimate(vec3): the Hart-style branch-and-bound distance estimator
//   - gTrap: the orbit-trap analog raymarch_main's trapAlbedo() consumes
//
// Keeping this in one file means the DE has a single source of truth; the
// validation harness and the renderer can never drift apart.
//
// The algorithm matches Parsec.Rendering.Raymarching.IFS3DDistanceEstimator
// on the CPU side. Key fact: child i of word w has accumulated transform
// (T_w composed with T_i), with T_i on the INNER side. Non-commutative, so
// order matters.

// -----------------------------------------------------------------------------
// IFS data (uploaded as SSBO)
// -----------------------------------------------------------------------------

struct IFSMap {
    vec4 row0;       // (M00, M01, M02, Tx)
    vec4 row1;       // (M10, M11, M12, Ty)
    vec4 row2;       // (M20, M21, M22, Tz)
    vec4 sigmaPad;   // (sigma, _, _, _) - pad to 16-byte alignment
};

layout(std430, binding = 0) readonly buffer IFSData {
    IFSMap maps[];
} ifs;

// Binding 9: the raymarch composite runs under RaymarchPipeline, which owns
// binding 1 (FoldParams), 4/5 (render params / accumulator) and 2/3 (clear +
// finalize); the attractor family owns 6/7/8. 9 is the first free slot that
// both the raymarch and validation composites can share.
layout(std430, binding = 9) readonly buffer Query {
    int   pointCount;     // used only by the validation shader
    int   numMaps;
    int   maxDepth;
    int   pad0;
    vec4  attractorSphere;   // (cx, cy, cz, r)
    vec4  detailEps;         // (eps, _, _, _) - only x is used
} q;

// Orbit-trap analog for the affine IFS DE, read by raymarch_main's
// trapAlbedo(). There is no escape-time orbit here, so the fields derive from
// the nearest LEAF sphere of the branch-and-bound descent (in attractor-sphere
// units, center C, radius R):
//   x = |leaf - C| / R    origin-trap analog (radial bands)
//   y = |rel.y|           axis trap
//   z = |rel.z|           plane trap
//   w = depth / maxDepth  shell glaze weight (shallow leaves glaze)
vec4 gTrap = vec4(0.0, 0.0, 0.0, 1.0);

// -----------------------------------------------------------------------------
// Affine ops on packed 3x4 transforms
// -----------------------------------------------------------------------------

struct Affine {
    vec4 r0, r1, r2;  // row vectors with translation in .w
};

Affine identityAffine() {
    return Affine(
        vec4(1.0, 0.0, 0.0, 0.0),
        vec4(0.0, 1.0, 0.0, 0.0),
        vec4(0.0, 0.0, 1.0, 0.0));
}

vec3 affineApply(Affine a, vec3 p) {
    return vec3(
        dot(a.r0.xyz, p) + a.r0.w,
        dot(a.r1.xyz, p) + a.r1.w,
        dot(a.r2.xyz, p) + a.r2.w);
}

// affineCompose(outer, inner) applies inner first, then outer.
// Same semantics as inner.Then(outer) on the C# side.
Affine affineCompose(Affine outer, Affine inner) {
    Affine r;
    r.r0 = vec4(
        dot(outer.r0.xyz, vec3(inner.r0.x, inner.r1.x, inner.r2.x)),
        dot(outer.r0.xyz, vec3(inner.r0.y, inner.r1.y, inner.r2.y)),
        dot(outer.r0.xyz, vec3(inner.r0.z, inner.r1.z, inner.r2.z)),
        dot(outer.r0.xyz, vec3(inner.r0.w, inner.r1.w, inner.r2.w)) + outer.r0.w);
    r.r1 = vec4(
        dot(outer.r1.xyz, vec3(inner.r0.x, inner.r1.x, inner.r2.x)),
        dot(outer.r1.xyz, vec3(inner.r0.y, inner.r1.y, inner.r2.y)),
        dot(outer.r1.xyz, vec3(inner.r0.z, inner.r1.z, inner.r2.z)),
        dot(outer.r1.xyz, vec3(inner.r0.w, inner.r1.w, inner.r2.w)) + outer.r1.w);
    r.r2 = vec4(
        dot(outer.r2.xyz, vec3(inner.r0.x, inner.r1.x, inner.r2.x)),
        dot(outer.r2.xyz, vec3(inner.r0.y, inner.r1.y, inner.r2.y)),
        dot(outer.r2.xyz, vec3(inner.r0.z, inner.r1.z, inner.r2.z)),
        dot(outer.r2.xyz, vec3(inner.r0.w, inner.r1.w, inner.r2.w)) + outer.r2.w);
    return r;
}

Affine mapAt(int i) {
    return Affine(ifs.maps[i].row0, ifs.maps[i].row1, ifs.maps[i].row2);
}

float sigmaAt(int i) {
    return ifs.maps[i].sigmaPad.x;
}

// -----------------------------------------------------------------------------
// DE proper - stack-based iterative descent
// -----------------------------------------------------------------------------

const int MAX_STACK = 14;
const int MAX_MAPS = 64;

struct Frame {
    Affine acc;
    vec3   center;
    float  radius;
    int    depth;
    uvec2  visited;
};

bool bitmaskGet(uvec2 m, int i) {
    if (i < 32) return ((m.x >> uint(i)) & 1u) != 0u;
    else        return ((m.y >> uint(i - 32)) & 1u) != 0u;
}

uvec2 bitmaskSet(uvec2 m, int i) {
    if (i < 32) m.x |= (1u << uint(i));
    else        m.y |= (1u << uint(i - 32));
    return m;
}

float distancePointToSphere(vec3 p, vec3 c, float r) {
    return max(0.0, length(p - c) - r);
}

// Accessor used by raymarch_main: the attractor's bounding sphere (xyz, radius).
vec4 attractorBoundingSphere() {
    return q.attractorSphere;
}

// Accessor used by raymarch_main: DE fudge factor. The affine IFS DE is a
// true lower bound, so no fudge is needed.
float deFudge() {
    return 1.0;
}

float estimate(vec3 p) {
    vec3 baseCenter = q.attractorSphere.xyz;
    float baseRadius = q.attractorSphere.w;
    gTrap = vec4(0.0, 0.0, 0.0, 1.0);
    float startDist = length(p - baseCenter) - baseRadius;
    if (startDist > baseRadius * 2.0) return startDist;

    Frame stack[MAX_STACK];
    int sp = 0;
    stack[0].acc     = identityAffine();
    stack[0].center  = baseCenter;
    stack[0].radius  = baseRadius;
    stack[0].depth   = 0;
    stack[0].visited = uvec2(0u, 0u);

    float best = 3.4028235e+38;  // FLT_MAX
    float epsDetail = q.detailEps.x;
    int maxDepth = q.maxDepth;
    int N = q.numMaps;

    int safetyIter = 0;
    const int SAFETY_CAP = 200000;

    while (sp >= 0) {
        if (++safetyIter > SAFETY_CAP) break;

        Frame f = stack[sp];

        if (f.visited.x == 0u && f.visited.y == 0u) {
            float distToSphere = distancePointToSphere(p, f.center, f.radius);
            if (distToSphere >= best) { sp--; continue; }
            if (f.depth >= maxDepth ||
                f.radius < epsDetail * max(distToSphere, f.radius))
            {
                if (distToSphere < best) {
                    best = distToSphere;
                    vec3 rel = (f.center - baseCenter) / baseRadius;
                    gTrap = vec4(length(rel), abs(rel.y), abs(rel.z),
                                 float(f.depth) / float(max(maxDepth, 1)));
                }
                sp--;
                continue;
            }
        }

        int bestChild = -1;
        float bestChildDist = best;
        for (int i = 0; i < N; i++) {
            if (bitmaskGet(f.visited, i)) continue;
            Affine childAcc = affineCompose(f.acc, mapAt(i));
            vec3 childCenter = affineApply(childAcc, baseCenter);
            float childRadius = f.radius * sigmaAt(i);
            float d = distancePointToSphere(p, childCenter, childRadius);
            if (d < bestChildDist) {
                bestChildDist = d;
                bestChild = i;
            }
        }

        if (bestChild < 0) { sp--; continue; }

        stack[sp].visited = bitmaskSet(f.visited, bestChild);

        if (sp + 1 >= MAX_STACK) { sp--; continue; }

        Affine childAcc = affineCompose(f.acc, mapAt(bestChild));
        vec3 childCenter = affineApply(childAcc, baseCenter);
        float childRadius = f.radius * sigmaAt(bestChild);

        sp++;
        stack[sp].acc     = childAcc;
        stack[sp].center  = childCenter;
        stack[sp].radius  = childRadius;
        stack[sp].depth   = f.depth + 1;
        stack[sp].visited = uvec2(0u, 0u);
    }

    return best;
}
