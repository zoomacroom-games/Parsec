#version 430 core

// ===========================================================================
// Deep-zoom 2D escape-time -- delta pass (fp64). Four formulas, two paths.
//
// FORMULAS (DeepParams.formula):
//   0 Mandelbrot   z' = z^2 + c               (parameter plane, seed 0)
//   1 Prospector   X'=Cx+0.25XY, Y'=Cy-3X^2+0.25Y^2   (real 2D, seed 0)
//   2 Julia        z' = z^2 + kappa           (DYNAMICAL plane: pixel = seed,
//                                              kappa fixed; reference orbit is
//                                              the seed-orbit, so it starts at
//                                              the center, NOT at 0)
//   3 BurningShip  x'=x^2-y^2+Cx, y'=2|xy|+Cy (parameter plane, seed 0)
//
// PATHS:
//   directMode==1 : iterate the pixel's own orbit in fp64 from cd+offset. Exact
//       at shallow zoom (center representable as a double, radius > ~1e-10).
//       Used for all formulas when shallow; the ONLY correct path for Burning
//       Ship there -- perturbation on the abs map is unreliable when the delta
//       is large (validated: glitches at r>~1e-3, clean below).
//   directMode==0 : perturbation + rebasing against the high-precision reference
//       orbit Zref. Used for deep zoom. Mandelbrot/Julia share the complex z^2
//       recurrence; the parameter/dynamical split is just where the pixel offset
//       enters -- per-step (parameter) vs the initial delta (Julia). Validated
//       vs an mpmath oracle: Julia exact to 1e-13 at all depths; Burning Ship
//       (diffabs) exact with a bounded reference (gross=0 at 1e-18).
//
// REBASING carries a +Zref[0] correction (dz = z - Zref[0]) so it is correct
// for Julia whose reference starts at the seed; for the seed-0 formulas
// Zref[0]==0 and it reduces to the classic dz=z.
//
// Output: one float per pixel = smooth iteration count, or -1.0 in-set.
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
    int   directMode;    // 1 = direct fp64 iteration (shallow); 0 = perturbation
    dvec2 refDc;         // reference offset from view center (0 if ref==center)
    dvec2 pixelDx;       // dc per +1 pixel X = (spacing,0)
    dvec2 pixelDy;       // dc per +1 pixel Y = (0,-spacing)
    dvec2 jitter;        // sub-pixel SSAA offset (px)
    dvec2 kappa;         // Julia constant (formula 2)
    dvec2 cd;            // view center as a double (direct mode)
    double escapeR2;     // escape |z|^2
    double _pad2;
};

double diffabs(double c, double d)   // exact |c+d| - |c|  (burning-ship primitive)
{
    if (c >= 0.0LF) return (c + d >= 0.0LF) ? d : -(2.0LF * c + d);
    else            return (c + d >  0.0LF) ?  (2.0LF * c + d) : -d;
}

float smoothMu(int esc, double z2)
{
    float lz = log(float(sqrt(z2)));
    return float(esc) + 1.0 - log2(max(lz, 1e-20));
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
    dvec2  pixelOff = fx * pixelDx + fy * pixelDy;   // offset from view center
    dvec2  dc = refDc + pixelOff;                    // offset from reference (perturb)

    int    esc = maxIter;
    double z2  = 0.0LF;

    // ---------------------------------------------------------- direct fp64
    if (directMode == 1)
    {
        dvec2 p = cd + pixelOff;                     // absolute pixel coordinate
        if (formula == 2)
        {
            // Julia: seed = pixel, constant = kappa
            double x = p.x, y = p.y;
            for (int n = 0; n < maxIter; n++) {
                double m2 = x * x + y * y;
                if (m2 > escapeR2) { esc = n; z2 = m2; break; }
                double nx = x * x - y * y + kappa.x;
                double ny = 2.0LF * x * y + kappa.y;
                x = nx; y = ny;
            }
        }
        else if (formula == 1)
        {
            double X = 0.0LF, Y = 0.0LF;
            for (int n = 0; n < maxIter; n++) {
                double m2 = X * X + Y * Y;
                if (m2 > escapeR2) { esc = n; z2 = m2; break; }
                double nX = p.x + 0.25LF * X * Y;
                double nY = p.y - 3.0LF * X * X + 0.25LF * Y * Y;
                X = nX; Y = nY;
            }
        }
        else if (formula == 3)
        {
            double x = 0.0LF, y = 0.0LF;
            for (int n = 0; n < maxIter; n++) {
                double m2 = x * x + y * y;
                if (m2 > escapeR2) { esc = n; z2 = m2; break; }
                double nx = x * x - y * y + p.x;
                double ny = 2.0LF * abs(x * y) + p.y;
                x = nx; y = ny;
            }
        }
        else
        {
            // Mandelbrot
            double x = 0.0LF, y = 0.0LF;
            for (int n = 0; n < maxIter; n++) {
                double m2 = x * x + y * y;
                if (m2 > escapeR2) { esc = n; z2 = m2; break; }
                double nx = x * x - y * y + p.x;
                double ny = 2.0LF * x * y + p.y;
                x = nx; y = ny;
            }
        }
        mu[outIdx] = (esc >= maxIter) ? -1.0 : smoothMu(esc, z2);
        return;
    }

    // ---------------------------------------------------- perturbation + rebase
    // Julia seeds the delta with dc and adds nothing per step; the parameter-plane
    // formulas start dz at 0 and add dc each step.
    dvec2 dz     = (formula == 2) ? dc : dvec2(0.0LF);
    dvec2 stepDc = (formula == 2) ? dvec2(0.0LF) : dc;
    int   m      = 0;

    for (int n = 0; n < maxIter; n++)
    {
        dvec2  Zr  = Zref[m];
        dvec2  z   = Zr + dz;
        double az2 = z.x * z.x + z.y * z.y;
        if (az2 > escapeR2) { esc = n; z2 = az2; break; }

        double adz2 = dz.x * dz.x + dz.y * dz.y;
        if (az2 < adz2 || m >= refCount - 1)
        {
            dz = z - Zref[0];          // rebase (note -Zref[0]: correct for Julia)
            m  = 0;
            Zr = Zref[0];
        }

        if (formula == 0 || formula == 2)
        {
            // complex z^2 (Mandelbrot & Julia share this; stepDc differs)
            dvec2 twoZrDz = dvec2(2.0LF * (Zr.x * dz.x - Zr.y * dz.y),
                                  2.0LF * (Zr.x * dz.y + Zr.y * dz.x));
            dvec2 dzSq    = dvec2(dz.x * dz.x - dz.y * dz.y,
                                  2.0LF * dz.x * dz.y);
            dz = twoZrDz + dzSq + stepDc;
        }
        else if (formula == 1)
        {
            // Prospector real 2D map
            double X = Zr.x, Y = Zr.y, dx = dz.x, dy = dz.y;
            double ndx = stepDc.x + 0.25LF * (Y * dx + X * dy + dx * dy);
            double ndy = stepDc.y - 3.0LF * (2.0LF * X * dx + dx * dx)
                                  + 0.25LF * (2.0LF * Y * dy + dy * dy);
            dz = dvec2(ndx, ndy);
        }
        else
        {
            // Burning Ship: x has no abs; y uses diffabs for 2|x||y|
            double X = Zr.x, Y = Zr.y, dx = dz.x, dy = dz.y;
            double da = diffabs(X, dx);
            double db = diffabs(Y, dy);
            double ndx = 2.0LF * X * dx - 2.0LF * Y * dy + dx * dx - dy * dy + stepDc.x;
            double ndy = 2.0LF * abs(Y) * da + 2.0LF * abs(X) * db
                       + 2.0LF * da * db + stepDc.y;
            dz = dvec2(ndx, ndy);
        }
        m++;
    }

    mu[outIdx] = (esc >= maxIter) ? -1.0 : smoothMu(esc, z2);
}
