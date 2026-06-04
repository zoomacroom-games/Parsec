using System.Globalization;
using System.Numerics;

namespace Parsec.Rendering.DeepZoom;

/// <summary>
/// High-precision reference orbit for perturbation-based deep zoom. The ONLY
/// arbitrary-precision component: one orbit per view, computed in binary
/// fixed-point (see <see cref="BinaryFixed"/>), each Z_n then cast to double for
/// the GPU delta pass. The Z_n are O(1), so double storage is fine; depth is
/// carried by the deltas' exponent range, never by the reference mantissa.
/// Validated against an mpmath oracle to ~1 ULP.
///
/// Four formulas share this one class -- only the recurrence, the seed, and the
/// escape radius differ:
///   0 Mandelbrot   Z' = Z^2 + C            seed 0,      parameter plane
///   1 Prospector   real 2D quadratic map   seed 0,      parameter plane
///   2 Julia        Z' = Z^2 + kappa        seed=CENTER, dynamical plane
///   3 BurningShip  X'=X^2-Y^2+Cx, Y'=2|XY|+Cy   seed 0,  parameter plane
///
/// For Julia the "center" is a seed in the dynamical plane and kappa is the
/// fixed constant; the reference is that seed's orbit, so Z[0] = center (NOT 0).
/// That is why the GPU rebasing subtracts Zref[0] -- a no-op for the seed-0
/// formulas, but required here.
/// </summary>
public sealed class ReferenceOrbit
{
    public readonly double[] Re;
    public readonly double[] Im;
    public readonly int Count;
    public readonly bool Escaped;

    private ReferenceOrbit(double[] re, double[] im, int count, bool escaped)
    {
        Re = re; Im = im; Count = count; Escaped = escaped;
    }

    /// <param name="formula">0 Mandelbrot, 1 Prospector, 2 Julia, 3 BurningShip
    /// (see class summary).</param>
    /// <param name="kappaRe">Julia constant real part (formula 2 only).</param>
    /// <param name="kappaIm">Julia constant imag part (formula 2 only).</param>
    public static ReferenceOrbit Compute(string centerRe, string centerIm,
                                         int precisionBits, int maxIter, int formula = 0,
                                         double kappaRe = 0.0, double kappaIm = 0.0)
    {
        int P = precisionBits;
        BigInteger cre = BinaryFixed.FromDecimal(centerRe, P);
        BigInteger cim = BinaryFixed.FromDecimal(centerIm, P);

        var re = new double[maxIter + 1];
        var im = new double[maxIter + 1];
        bool escaped = false;
        int n = 0;

        if (formula == 1)
        {
            // Prospector real 2D map (validated vs mpmath, exact to double):
            //   X' = Cx + 0.25*X*Y ;  Y' = Cy - 3*X^2 + 0.25*Y^2
            BigInteger zre = BigInteger.Zero, zim = BigInteger.Zero;     // seed 0
            BigInteger quarter = BigInteger.One << (P - 2);              // 0.25
            BigInteger esc = new BigInteger(1_000_000) << P;            // escapeR2 = 1e6
            while (n <= maxIter)
            {
                re[n] = BinaryFixed.ToDouble(zre, P);
                im[n] = BinaryFixed.ToDouble(zim, P);
                BigInteger xx = BinaryFixed.MulShift(zre, zre, P);
                BigInteger yy = BinaryFixed.MulShift(zim, zim, P);
                if (xx + yy > esc) { escaped = true; n++; break; }
                BigInteger xy = BinaryFixed.MulShift(zre, zim, P);
                zre = cre + BinaryFixed.MulShift(xy, quarter, P);
                zim = cim - 3 * xx + BinaryFixed.MulShift(yy, quarter, P);
                n++;
            }
        }
        else if (formula == 2)
        {
            // Julia Z^2 + kappa (complex), DYNAMICAL plane: the reference orbit is
            // the orbit of the view CENTER seed, so Z[0] = center. kappa is O(1)
            // and fixed; FromDecimal on its round-trip string places its full 53
            // bits at the top of the P-bit fixed-point word (lower bits zero).
            BigInteger zre = cre, zim = cim;                            // seed = center
            BigInteger kre = BinaryFixed.FromDecimal(
                kappaRe.ToString("R", CultureInfo.InvariantCulture), P);
            BigInteger kim = BinaryFixed.FromDecimal(
                kappaIm.ToString("R", CultureInfo.InvariantCulture), P);
            BigInteger four = BigInteger.One << (P + 2);                // 4.0
            while (n <= maxIter)
            {
                re[n] = BinaryFixed.ToDouble(zre, P);
                im[n] = BinaryFixed.ToDouble(zim, P);
                BigInteger reSq = BinaryFixed.MulShift(zre, zre, P);
                BigInteger imSq = BinaryFixed.MulShift(zim, zim, P);
                if (reSq + imSq > four) { escaped = true; n++; break; }
                BigInteger reim = BinaryFixed.MulShift(zre, zim, P);
                zre = reSq - imSq + kre;        // Re(Z^2 + kappa)
                zim = (reim << 1) + kim;        // Im(Z^2 + kappa)
                n++;
            }
        }
        else if (formula == 3)
        {
            // Burning Ship: X' = X^2 - Y^2 + Cx ;  Y' = 2|X*Y| + Cy.  Seed 0.
            // The x-equation has no abs (|X|^2 == X^2); only the cross term folds.
            BigInteger zre = BigInteger.Zero, zim = BigInteger.Zero;     // seed 0
            BigInteger four = BigInteger.One << (P + 2);                // 4.0
            while (n <= maxIter)
            {
                re[n] = BinaryFixed.ToDouble(zre, P);
                im[n] = BinaryFixed.ToDouble(zim, P);
                BigInteger reSq = BinaryFixed.MulShift(zre, zre, P);
                BigInteger imSq = BinaryFixed.MulShift(zim, zim, P);
                if (reSq + imSq > four) { escaped = true; n++; break; }
                BigInteger reim = BinaryFixed.MulShift(zre, zim, P);
                zre = reSq - imSq + cre;                    // X^2 - Y^2 + Cx
                zim = (BigInteger.Abs(reim) << 1) + cim;    // 2|X*Y| + Cy
                n++;
            }
        }
        else
        {
            // Mandelbrot Z^2 + C (complex).  Seed 0.
            BigInteger zre = BigInteger.Zero, zim = BigInteger.Zero;
            BigInteger four = BigInteger.One << (P + 2);
            while (n <= maxIter)
            {
                re[n] = BinaryFixed.ToDouble(zre, P);
                im[n] = BinaryFixed.ToDouble(zim, P);
                BigInteger reSq = BinaryFixed.MulShift(zre, zre, P);
                BigInteger imSq = BinaryFixed.MulShift(zim, zim, P);
                if (reSq + imSq > four) { escaped = true; n++; break; }
                BigInteger reim = BinaryFixed.MulShift(zre, zim, P);
                zre = reSq - imSq + cre;
                zim = (reim << 1) + cim;
                n++;
            }
        }

        if (n < re.Length) { Array.Resize(ref re, n); Array.Resize(ref im, n); }
        return new ReferenceOrbit(re, im, n, escaped);
    }

    /// <summary>Interleaved [re0, im0, re1, im1, ...] for upload to a dvec2 SSBO.</summary>
    public double[] ToInterleaved()
    {
        var buf = new double[Count * 2];
        for (int i = 0; i < Count; i++) { buf[2 * i] = Re[i]; buf[2 * i + 1] = Im[i]; }
        return buf;
    }

    /// <summary>Suggested fixed-point bits for a zoom whose view radius is ~1e-digits:
    /// digits * log2(10) plus a safety margin (longer orbits / deeper zooms want more).</summary>
    public static int RecommendedPrecisionBits(int zoomDepthDecimalDigits, int marginBits = 32)
        => (int)Math.Ceiling(zoomDepthDecimalDigits * 3.321928094887362) + marginBits;
}

// ============================================================================
// VALIDATION FIXTURES (fixed-point mirror matched to an mpmath oracle):
//
// Mandelbrot (formula 0):
//   Compute("-0.743643887037158704752191506114774",
//           "0.131825904205311970493132056385139", 200, 2000);
//   Count == 2001; Escaped == false;
//   Re[1] == -7.43643887037158668e-01;  Im[1] == 1.31825904205311983e-01;
//   Re[4] == -2.72462266139682607e-01;  Im[4] == -9.15717851505672975e-02;
//   checksum(sum Re + sum Im) == -9.16195433262730035e+02;
//
// Prospector (formula 1):
//   Compute("0.8821942088", "-1.369419868", 200, 2000, formula: 1);
//   Count == 2001; Escaped == false;
//   Re[1] == 8.82194208799999990e-01;  Im[1] == -1.36941986800000004e+00;
//   Re[4] == 9.06734987120528024e-01;  Im[4] == -1.86681091536507426e+00;
//   checksum == -2.25445097371564771e+03;
//
// Julia (formula 2), seed (0,0), kappa = (-0.8, 0.156):
//   Compute("0.0", "0.0", 200, 2000, formula: 2, kappaRe: -0.8, kappaIm: 0.156);
//   Count == 253; Escaped == true;
//   Re[1] == -8.00000000000000044e-01;  Im[1] == 1.56000000000000000e-01;  // == kappa
//   Re[4] == -2.36007276969445595e-01;  Im[4] == -1.39203567249440274e-01;
//   checksum == -1.24263514812683709e+02;
//
// Burning Ship (formula 3), C = (-1.62542, -0.00417):
//   Compute("-1.62542", "-0.00417", 200, 2000, formula: 3);
//   Count == 2001; Escaped == false;
//   Re[1] == -1.62542000000000009e+00;  Im[1] == -4.17000000000000010e-03;  // == C
//   Re[4] == -1.27502619684151863e+00;  Im[4] == 1.34905111759336480e-02;
//   checksum == -9.66894369177143744e+02;
// ============================================================================
