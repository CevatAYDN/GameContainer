using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Lightweight 0-GC struct for Idle & Incremental games supporting numbers up to 1e(9*10^18).
    /// Stores number as mantissa * 10^exponent with auto-normalization and Idle suffix formatting (K, M, B, T, aa, ab...).
    /// </summary>
    [Serializable]
    [Preserve]
    public struct BigDouble : IComparable, IComparable<BigDouble>, IEquatable<BigDouble>
    {
        public double Mantissa;
        public long Exponent;

        public static readonly BigDouble Zero = new BigDouble(0.0, 0);
        public static readonly BigDouble One = new BigDouble(1.0, 0);
        public static readonly BigDouble MaxValue = new BigDouble(9.99999999999999, long.MaxValue);
        public static readonly BigDouble MinValue = new BigDouble(-9.99999999999999, long.MaxValue);

        private static readonly string[] StandardSuffixes = new string[]
        {
            "", "K", "M", "B", "T", "aa", "ab", "ac", "ad", "ae", "af", "ag", "ah", "ai", "aj",
            "ak", "al", "am", "an", "ao", "ap", "aq", "ar", "as", "at", "au", "av", "aw", "ax", "ay", "az"
        };

        public BigDouble(double mantissa, long exponent = 0)
        {
            Mantissa = mantissa;
            Exponent = exponent;
            Normalize();
        }

        public BigDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value == 0.0)
            {
                Mantissa = 0.0;
                Exponent = 0;
            }
            else
            {
                Exponent = (long)Math.Floor(Math.Log10(Math.Abs(value)));
                Mantissa = value / Math.Pow(10, Exponent);
                Normalize();
            }
        }

        public BigDouble(long value) : this((double)value) { }
        public BigDouble(int value) : this((double)value) { }

        private void Normalize()
        {
            if (Mantissa == 0.0 || double.IsNaN(Mantissa) || double.IsInfinity(Mantissa))
            {
                Mantissa = 0.0;
                Exponent = 0;
                return;
            }

            double absMantissa = Math.Abs(Mantissa);

            while (absMantissa >= 10.0)
            {
                // P2 fix: clamp the exponent so a saturated value (e.g. MaxValue * 10,
                // where SaturateAddExponent already returned long.MaxValue) cannot
                // overflow to long.MinValue and silently corrupt the number. Once the
                // exponent is pinned at the boundary, further normalization is a no-op.
                if (Exponent == long.MaxValue) break;
                Mantissa /= 10.0;
                absMantissa /= 10.0;
                Exponent++;
            }

            while (absMantissa < 1.0 && absMantissa > 0.0)
            {
                if (Exponent == long.MinValue) break;
                Mantissa *= 10.0;
                absMantissa *= 10.0;
                Exponent--;
            }
        }

        public static BigDouble operator +(BigDouble a, BigDouble b)
        {
            if (a.Mantissa == 0.0) return b;
            if (b.Mantissa == 0.0) return a;

            // R2026-C1 fix: saturate the exponent difference instead of overflowing long.
            // a.Exponent - b.Exponent wraps when a.Exponent is near long.MaxValue and
            // b.Exponent is negative (e.g. MaxValue + (-0.1) silently returned -0.1).
            long diff = SaturateSubExponent(a.Exponent, b.Exponent);
            if (diff > 15) return a;
            if (diff < -15) return b;

            if (diff >= 0)
            {
                double m = a.Mantissa + (b.Mantissa / Math.Pow(10, diff));
                return new BigDouble(m, a.Exponent);
            }
            else
            {
                double m = b.Mantissa + (a.Mantissa / Math.Pow(10, -diff));
                return new BigDouble(m, b.Exponent);
            }
        }

        public static BigDouble operator -(BigDouble a, BigDouble b)
        {
            return a + (-b);
        }

        public static BigDouble operator -(BigDouble a)
        {
            return new BigDouble(-a.Mantissa, a.Exponent);
        }

        public static BigDouble operator *(BigDouble a, BigDouble b)
        {
            if (a.Mantissa == 0.0 || b.Mantissa == 0.0) return Zero;
            // B1-fix: saturate the exponent sum instead of overflowing long (MaxValue * anything
            // would otherwise wrap to a negative exponent and silently corrupt the value).
            return new BigDouble(a.Mantissa * b.Mantissa, SaturateAddExponent(a.Exponent, b.Exponent));
        }

        public static BigDouble operator /(BigDouble a, BigDouble b)
        {
            if (b.Mantissa == 0.0) throw new DivideByZeroException("Cannot divide BigDouble by Zero.");
            // B1-fix: saturate the exponent difference instead of underflowing long.
            return new BigDouble(a.Mantissa / b.Mantissa, SaturateSubExponent(a.Exponent, b.Exponent));
        }

        /// <summary>clamped exponent addition; never overflows long.</summary>
        private static long SaturateAddExponent(long a, long b)
        {
            if (b > 0 && a > long.MaxValue - b) return long.MaxValue;
            if (b < 0 && a < long.MinValue - b) return long.MinValue;
            return a + b;
        }

        /// <summary>clamped exponent subtraction (a - b); never overflows long.</summary>
        private static long SaturateSubExponent(long a, long b)
        {
            if (b < 0 && a > long.MaxValue + b) return long.MaxValue; // a - (negative) → a + |b|
            if (b > 0 && a < long.MinValue + b) return long.MinValue;
            return a - b;
        }

        public static bool operator >(BigDouble a, BigDouble b) => a.CompareTo(b) > 0;
        public static bool operator <(BigDouble a, BigDouble b) => a.CompareTo(b) < 0;
        public static bool operator >=(BigDouble a, BigDouble b) => a.CompareTo(b) >= 0;
        public static bool operator <=(BigDouble a, BigDouble b) => a.CompareTo(b) <= 0;
        public static bool operator ==(BigDouble a, BigDouble b) => a.Equals(b);
        public static bool operator !=(BigDouble a, BigDouble b) => !a.Equals(b);

        public static implicit operator BigDouble(double value) => new BigDouble(value);
        public static implicit operator BigDouble(long value) => new BigDouble(value);
        public static implicit operator BigDouble(int value) => new BigDouble(value);
        public static explicit operator double(BigDouble val) => val.Mantissa * Math.Pow(10, val.Exponent);

        public int CompareTo(object obj)
        {
            if (obj is BigDouble other) return CompareTo(other);
            throw new ArgumentException("Object is not a BigDouble.");
        }

        public int CompareTo(BigDouble other)
        {
            // R2026-L3 note: ordering logic, spelled out for clarity:
            //  1. Two zeros are equal (Normalize pins zero to mantissa 0/exponent 0).
            //  2. Any positive exceeds any non-positive; any negative is below any non-negative.
            //  3. Same sign → larger exponent wins; for NEGATIVE values the direction flips
            //     (-1e5 < -1e3), hence the Mantissa > 0 ternary on the exponent comparisons.
            //  4. Same sign + same exponent → plain mantissa comparison.
            if (Mantissa == 0.0 && other.Mantissa == 0.0) return 0;
            if (Mantissa > 0 && other.Mantissa <= 0) return 1;
            if (Mantissa < 0 && other.Mantissa >= 0) return -1;

            if (Exponent > other.Exponent) return Mantissa > 0 ? 1 : -1;
            if (Exponent < other.Exponent) return Mantissa > 0 ? -1 : 1;

            return Mantissa.CompareTo(other.Mantissa);
        }

        // B1/B2-fix: Equals, GetHashCode and CompareTo now agree on a single exact
        // equality predicate (same Exponent AND exactly equal Mantissa). The previous
        // 1e-12 tolerance made Equals(a,b) true while GetHashCode(a) != GetHashCode(b),
        // violating the .NET object contract and corrupting Dictionary/HashSet lookups;
        // CompareTo also returned non-zero for values Equals considered equal, breaking
        // SortedSet/binary-search invariants. Exact comparison is consistent with
        // Normalize (mantissa always lands in [1,10)) and with the bit-exact
        // BitConverter round-trip used by SecureObservableBigDouble.
        public bool Equals(BigDouble other)
        {
            return Exponent == other.Exponent && Mantissa == other.Mantissa;
        }

        public override bool Equals(object obj)
        {
            return obj is BigDouble other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Mantissa, Exponent);
        }

        public static BigDouble Clamp(BigDouble value, BigDouble min, BigDouble max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // R2026-M7 fix: dynamic suffix generation for exponents beyond the static table
        // (aa..az). Idle games routinely exceed 1e90; generating ba, bb, ... keeps the
        // formatted output human-readable instead of falling back to scientific notation.
        private static string GetSuffix(long suffixIndex)
        {
            if (suffixIndex < StandardSuffixes.Length)
                return StandardSuffixes[suffixIndex];

            // Base-26 alphabetic suffix: after "az" comes "ba", "bb", ...
            // suffixIndex 30 -> "ba", 31 -> "bb", etc.
            long adjusted = suffixIndex - 4; // K=1,M=2,B=3,T=4 use the table; aa starts at 5
            var sb = new System.Text.StringBuilder(4);
            while (adjusted > 0)
            {
                adjusted--;
                sb.Insert(0, (char)('a' + (adjusted % 26)));
                adjusted /= 26;
            }
            return sb.ToString();
        }

        public string ToFormattedString()
        {
            if (Mantissa == 0.0) return "0";
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            // P3 fix: a very negative exponent (e.g. -300) makes Math.Pow(10, Exponent)
            // underflow to 0.0, so the old code printed "0" for a genuinely non-zero
            // value. Route any exponent that would underflow the double through the
            // scientific fallback instead of the fixed-point path.
            if (Exponent < 3)
            {
                if (Exponent < -15)
                {
                    return $"{Mantissa.ToString("F2", culture)}e{Exponent}";
                }
                // Guard against Math.Pow underflow: if Exponent is very negative such
                // that 10^Exponent is subnormal/zero, fall back to the scientific format
                // which preserves non-zero magnitude for display purposes.
                double pow10 = Math.Pow(10, Exponent);
                if (pow10 == 0.0)
                {
                    return $"{Mantissa.ToString("F2", culture)}e{Exponent}";
                }
                return (Mantissa * pow10).ToString("F0", culture);
            }

            long suffixIndex = Exponent / 3;
            long remainder = Exponent % 3;

            double displayValue = Mantissa * Math.Pow(10, remainder);

            // R2026-M7: cap dynamic suffixes at a sane bound — beyond ~1e(26^6) the
            // suffix string itself becomes meaningless; scientific notation is clearer.
            const long MaxDynamicSuffixIndex = 1000000;
            if (suffixIndex <= MaxDynamicSuffixIndex)
            {
                return $"{displayValue.ToString("F2", culture)}{GetSuffix(suffixIndex)}";
            }

            return $"{Mantissa.ToString("F2", culture)}e{Exponent}";
        }

        public override string ToString()
        {
            return ToFormattedString();
        }
    }
}
