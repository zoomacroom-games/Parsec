#version 430 core

// ===========================================================================
// Deep-zoom 2D escape-time -- delta pass (floatexp deltas). Deep-only sibling of
// deepzoom_delta.glsl: identical SSBOs / DeepParams / output, but dz is carried
// as floatexp (double mantissa + int exponent) to survive past the ~1.5e-154
// fp64 dz^2 underflow wall. There is no direct path here (direct fp64 is only
// used shallow); DeepParams.directMode / cd are declared solely so the std430
// layout matches the fp64 shader and the C# DeepParamsGpu struct.
//
// Formulas: 0 Mandelbrot, 1 Prospector, 2 Julia (dynamical), 3 Burning Ship.
// Mandelbrot/Julia share the complex z^2 recurrence; the pixel offset enters
// per-step (parameter plane) or as the initial delta (Julia). Burning Ship uses
// a floatexp diffabs. Rebasing subtracts Zref[0] (correct for Julia's seed-anchored
// reference; a no-op for the seed-0 formulas). Validated vs mpmath oracle.
// ===========================================================================

layout(local_size_x = 8, local_size_y = 8) in;

layout(std430, binding = 1) readonly buffer RefOrbit { dvec2 Zref[]; };
layout(std430, binding = 2) writeonly buffer MuOut   { float mu[]; };

layout(std430, binding = 4) readonly buffer DeepParams {
    int   width;
    int   height;
    int   rowOffset;
    int   rowCount;
    int   refCount;
    int   maxIter;
    int   formula;       // 0 Mandelbrot, 1 Prospector, 2 Julia, 3 BurningShip
    int   directMode;    // unused here (declared for layout parity)
    dvec2 refDc;
    dvec2 pixelDx;
    dvec2 pixelDy;
    dvec2 jitter;
    dvec2 kappa;         // unused here (baked into the reference for Julia)
    dvec2 cd;            // unused here (declared for layout parity)
    double escapeR2;
    double _pad2;
};

struct FloatExp { double m; int e; };
FloatExp feNorm(double m, int e) {
    if (m == 0.0LF) return FloatExp(0.0LF, 0);
    int de; double fm = frexp(m, de); return FloatExp(fm, e + de);
}
FloatExp feFrom(double x) {
    if (x == 0.0LF) return FloatExp(0.0LF, 0);
    int e; double m = frexp(x, e); return FloatExp(m, e);
}
double feTo(FloatExp a) { return (a.m == 0.0LF) ? 0.0LF : ldexp(a.m, a.e); }
FloatExp feMul(FloatExp a, FloatExp b) {
    if (a.m == 0.0LF || b.m == 0.0LF) return FloatExp(0.0LF, 0);
    return feNorm(a.m * b.m, a.e + b.e);
}
FloatExp feMulD(FloatExp a, double x) {
    if (a.m == 0.0LF || x == 0.0LF) return FloatExp(0.0LF, 0);
    return feNorm(a.m * x, a.e);
}
FloatExp feSqr(FloatExp a) {
    if (a.m == 0.0LF) return FloatExp(0.0LF, 0);
    return feNorm(a.m * a.m, a.e + a.e);
}
FloatExp feNeg(FloatExp a) { return FloatExp(-a.m, a.e); }
FloatExp feAdd(FloatExp a, FloatExp b) {
    if (a.m == 0.0LF) return b;
    if (b.m == 0.0LF) return a;
    int de = a.e - b.e;
    if (de >= 0) { if (de > 54) return a; return feNorm(a.m + ldexp(b.m, -de), a.e); }
    else         { if (-de > 54) return b; return feNorm(b.m + ldexp(a.m, de), b.e); }
}
FloatExp feSub(FloatExp a, FloatExp b) { return feAdd(a, feNeg(b)); }
bool feLess(FloatExp a, FloatExp b) {
    if (a.m == 0.0LF) return b.m != 0.0LF;
    if (b.m == 0.0LF) return false;
    if (a.e != b.e) return a.e < b.e;
    return abs(a.m) < abs(b.m);
}
// exact |c + d| - |c|, with c a plain double (reference, O(1)) and d floatexp
FloatExp feDiffabs(double c, FloatExp d) {
    FloatExp cpd = feAdd(feFrom(c), d);
    double s = feTo(cpd);
    if (c >= 0.0LF) return (s >= 0.0LF) ? d : feNeg(feAdd(feFrom(2.0LF * c), d));
    else            return (s >  0.0LF) ? feAdd(feFrom(2.0LF * c), d) : feNeg(d);
}

void main()
{
    uint gx = gl_GlobalInvocationID.x;
    uint gy = gl_GlobalInvocationID.y;
    if (gx >= uint(width) || gy >= uint(rowCount)) return;
    int  py = rowOffset + int(gy);
    if (py >= height) return;
    int  outIdx = py * width + int(gx);

    double fx = double(gx) - 0.5LF * double(width)  + jitter.x;
    double fy = double(py) - 0.5LF * double(height) + jitter.y;
    dvec2  dcD = refDc + fx * pixelDx + fy * pixelDy;
    FloatExp dcRe = feFrom(dcD.x);
    FloatExp dcIm = feFrom(dcD.y);

    // Julia seeds the delta with dc and adds nothing per step; parameter-plane
    // formulas start at 0 and add dc each step.
    bool julia = (formula == 2);
    FloatExp dzRe = julia ? dcRe : FloatExp(0.0LF, 0);
    FloatExp dzIm = julia ? dcIm : FloatExp(0.0LF, 0);
    FloatExp stepRe = julia ? FloatExp(0.0LF, 0) : dcRe;
    FloatExp stepIm = julia ? FloatExp(0.0LF, 0) : dcIm;

    int    m   = 0;
    int    esc = maxIter;
    double z2  = 0.0LF;

    for (int n = 0; n < maxIter; n++)
    {
        dvec2 Zr = Zref[m];
        FloatExp zRe = feAdd(feFrom(Zr.x), dzRe);
        FloatExp zIm = feAdd(feFrom(Zr.y), dzIm);
        double zrd = feTo(zRe), zid = feTo(zIm);
        double az2 = zrd * zrd + zid * zid;
        if (az2 > escapeR2) { esc = n; z2 = az2; break; }

        FloatExp azf  = feAdd(feSqr(zRe),  feSqr(zIm));
        FloatExp adzf = feAdd(feSqr(dzRe), feSqr(dzIm));
        if (feLess(azf, adzf) || m >= refCount - 1)
        {
            dzRe = feSub(zRe, feFrom(Zref[0].x));   // rebase -Zref[0] (Julia-correct)
            dzIm = feSub(zIm, feFrom(Zref[0].y));
            m  = 0;
            Zr = Zref[0];
        }

        if (formula == 0 || formula == 2)
        {
            // complex z^2 (Mandelbrot & Julia; stepRe/Im is 0 for Julia)
            FloatExp twoZrDzRe = feSub(feMulD(dzRe, 2.0LF * Zr.x), feMulD(dzIm, 2.0LF * Zr.y));
            FloatExp twoZrDzIm = feAdd(feMulD(dzRe, 2.0LF * Zr.y), feMulD(dzIm, 2.0LF * Zr.x));
            FloatExp dz2Re = feSub(feSqr(dzRe), feSqr(dzIm));
            FloatExp dz2Im = feMulD(feMul(dzRe, dzIm), 2.0LF);
            dzRe = feAdd(feAdd(twoZrDzRe, dz2Re), stepRe);
            dzIm = feAdd(feAdd(twoZrDzIm, dz2Im), stepIm);
        }
        else if (formula == 1)
        {
            // Prospector real 2D map
            FloatExp sumX = feAdd(feAdd(feMulD(dzRe, Zr.y), feMulD(dzIm, Zr.x)),
                                  feMul(dzRe, dzIm));
            FloatExp ndx  = feAdd(stepRe, feMulD(sumX, 0.25LF));
            FloatExp termA = feMulD(feAdd(feMulD(dzRe, 2.0LF * Zr.x), feSqr(dzRe)), -3.0LF);
            FloatExp termB = feMulD(feAdd(feMulD(dzIm, 2.0LF * Zr.y), feSqr(dzIm)),  0.25LF);
            FloatExp ndy  = feAdd(stepIm, feAdd(termA, termB));
            dzRe = ndx; dzIm = ndy;
        }
        else
        {
            // Burning Ship: x has no abs; y uses diffabs for 2|x||y|
            double X = Zr.x, Y = Zr.y;
            FloatExp da = feDiffabs(X, dzRe);
            FloatExp db = feDiffabs(Y, dzIm);
            FloatExp ndx = feAdd(feAdd(feSub(feMulD(dzRe, 2.0LF * X), feMulD(dzIm, 2.0LF * Y)),
                                       feSub(feSqr(dzRe), feSqr(dzIm))), stepRe);
            FloatExp ndy = feAdd(feAdd(feAdd(feMulD(da, 2.0LF * abs(Y)),
                                             feMulD(db, 2.0LF * abs(X))),
                                       feMulD(feMul(da, db), 2.0LF)), stepIm);
            dzRe = ndx; dzIm = ndy;
        }
        m++;
    }

    if (esc >= maxIter) { mu[outIdx] = -1.0; }
    else {
        float lz = log(float(sqrt(z2)));
        mu[outIdx] = float(esc) + 1.0 - log2(max(lz, 1e-20));
    }
}
