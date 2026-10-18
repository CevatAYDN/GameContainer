using System;
using System.Globalization;

namespace Nexus.Firebase.RemoteConfig
{
    /// <summary>
    /// Reads a raw remote value as a typed value, or returns the fallback. Numbers always use the invariant culture: the console writes
    /// 0.5, and a device set to Turkish or German must not read it as something else.
    /// </summary>
    public static class RemoteConfigParser
    {
        public static bool ToBool(string raw, bool fallback)
        {
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1")
            {
                return true;
            }

            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0")
            {
                return false;
            }

            return fallback;
        }

        public static int ToInt(string raw, int fallback)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }

        public static double ToDouble(string raw, double fallback)
        {
            bool parsed = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
            return parsed && double.IsFinite(value) ? value : fallback;
        }

        public static string ToText(string raw, string fallback)
        {
            return raw ?? fallback;
        }
    }
}
