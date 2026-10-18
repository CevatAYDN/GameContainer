using System;
using System.Globalization;

namespace Nexus.Firebase.Analytics
{
    public enum AnalyticsParameterKind
    {
        Unsupported,
        Text,
        Integer,
        Real,
    }

    /// <summary>
    /// Which Firebase parameter type a logged value becomes: text, a 64-bit integer or a double. Firebase has no boolean parameter, so a
    /// boolean is sent as 1 or 0. Anything else, and a number that is not finite, is not sent.
    /// </summary>
    public static class AnalyticsParameterValue
    {
        public static AnalyticsParameterKind KindOf(object value)
        {
            switch (value)
            {
                case string _:
                    return AnalyticsParameterKind.Text;
                case bool _:
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                    return AnalyticsParameterKind.Integer;
                case float number:
                    return float.IsFinite(number) ? AnalyticsParameterKind.Real : AnalyticsParameterKind.Unsupported;
                case double number:
                    return double.IsFinite(number) ? AnalyticsParameterKind.Real : AnalyticsParameterKind.Unsupported;
                default:
                    return AnalyticsParameterKind.Unsupported;
            }
        }

        public static long ToInteger(object value)
        {
            return value is bool flag ? (flag ? 1L : 0L) : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        public static double ToReal(object value)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
    }
}