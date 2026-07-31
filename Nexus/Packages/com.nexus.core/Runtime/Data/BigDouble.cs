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
                Mantissa /= 10.0;
                absMantissa /= 10.0;
                Exponent++;
            }

            while (absMantissa < 1.0 && absMantissa > 0.0)
            {
                Mantissa *= 10.0;
                absMantissa *= 10.0;
                Exponent--;
            }
        }

        public static BigDouble operator +(BigDouble a, BigDouble b)
        {
            if (a.Mantissa == 0.0) return b;
            if (b.Mantissa == 0.0) return a;

            long diff = a.Exponent - b.Exponent;
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
            return new BigDouble(a.Mantissa * b.Mantissa, a.Exponent + b.Exponent);
        }

        public static BigDouble operator /(BigDouble a, BigDouble b)
        {
            if (b.Mantissa == 0.0) throw new DivideByZeroException("Cannot divide BigDouble by Zero.");
            return new BigDouble(a.Mantissa / b.Mantissa, a.Exponent - b.Exponent);
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

        public int CompareTo(BigDouble other)
        {
            if (Mantissa == 0.0 && other.Mantissa == 0.0) return 0;
            if (Mantissa > 0 && other.Mantissa <= 0) return 1;
            if (Mantissa < 0 && other.Mantissa >= 0) return -1;

            if (Exponent > other.Exponent) return Mantissa > 0 ? 1 : -1;
            if (Exponent < other.Exponent) return Mantissa > 0 ? -1 : 1;

            return Mantissa.CompareTo(other.Mantissa);
        }

        public bool Equals(BigDouble other)
        {
            return Exponent == other.Exponent && Math.Abs(Mantissa - other.Mantissa) < 1e-12;
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

        public string ToFormattedString()
        {
            if (Mantissa == 0.0) return "0";
            if (Exponent < 3) return (Mantissa * Math.Pow(10, Exponent)).ToString("F0");

            long suffixIndex = Exponent / 3;
            long remainder = Exponent % 3;

            double displayValue = Mantissa * Math.Pow(10, remainder);

            if (suffixIndex < StandardSuffixes.Length)
            {
                return $"{displayValue:F2}{StandardSuffixes[suffixIndex]}";
            }

            return $"{Mantissa:F2}e{Exponent}";
        }

        public override string ToString()
        {
            return ToFormattedString();
        }
    }
}
