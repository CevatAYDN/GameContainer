namespace Nexus.Core.Services
{
    /// <summary>What a read found (issue #147).</summary>
    public enum StorageReadStatus
    {
        /// <summary>The key holds a value.</summary>
        Present,
        /// <summary>The key was never written, or was deleted: a first run for that key.</summary>
        Absent,
        /// <summary>The key's data is there but cannot be read: tampered, written under another seed or on another phone, or damaged.</summary>
        Unreadable,
    }

    /// <summary>
    /// A thin abstraction for small persistent game data: models do not depend on UnityEngine.PlayerPrefs, and EditMode tests
    /// replace it with an in-memory fake.
    /// </summary>
    public interface IPlayerPrefsService
    {
        int GetInt(string key, int defaultValue = 0);
        void SetInt(string key, int value);
        bool GetBool(string key, bool defaultValue = false);
        void SetBool(string key, bool value);
        string GetString(string key, string defaultValue = "");
        void SetString(string key, string value);
        float GetFloat(string key, float defaultValue = 0f);
        void SetFloat(string key, float value);
        long GetLong(string key, long defaultValue = 0L);
        void SetLong(string key, long value);
        // Older adapters already implement string persistence. Default numeric support
        // must round-trip through that contract instead of silently discarding writes.
        BigDouble GetBigDouble(string key, BigDouble defaultValue = default)
        {
            return BigDoubleStorageCodec.Decode(GetString(key, null), defaultValue);
        }
        void SetBigDouble(string key, BigDouble value)
        {
            SetString(key, BigDoubleStorageCodec.Encode(value));
        }
        /// <summary>
        /// Reads a value and says which case it is: <see cref="StorageReadStatus.Present"/>, <see cref="StorageReadStatus.Absent"/>
        /// or <see cref="StorageReadStatus.Unreadable"/>. A store whose values are always readable (PlayerPrefs, memory) answers
        /// Present or Absent; <c>EncryptedStorageService</c> reports a value that fails its check as Unreadable (issue #147).
        /// </summary>
        StorageReadStatus TryGetString(string key, out string value)
        {
            value = HasKey(key) ? GetString(key, null) : null;
            return value != null ? StorageReadStatus.Present : StorageReadStatus.Absent;
        }

        bool HasKey(string key);
        void DeleteKey(string key);
        void Save();
    }

    /// <summary>
    /// Integer save format: invariant culture, so a value written under one device locale reads
    /// under another (a culture whose NegativeSign is U+2212 wrote "−5", which "-" cultures cannot parse).
    /// Reads fall back to the current culture, then to any culture's minus sign, for values saved before the format was fixed.
    /// </summary>
    internal static class IntegerStorageCodec
    {
        internal static string Encode(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        internal static bool TryDecode(string stored, out long value)
            => long.TryParse(stored, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value)
               || long.TryParse(stored, out value)
               || (stored != null && long.TryParse(NormalizeLegacySign(stored), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value));

        // Legacy negatives: U+2212 MINUS SIGN (sv, nb, fi, ...) and bidi marks before the sign (ar U+061C, fa/he U+200E/U+200F).
        private static string NormalizeLegacySign(string stored)
            => stored.Replace("؜", "").Replace("‎", "").Replace("‏", "").Replace('−', '-');
    }

    /// <summary>Shared, finite-only codec for the existing mantissa;exponent save format.</summary>
    internal static class BigDoubleStorageCodec
    {
        internal static string Encode(BigDouble value)
        {
            if (double.IsNaN(value.Mantissa) || double.IsInfinity(value.Mantissa))
                throw new System.ArgumentOutOfRangeException(nameof(value), "Stored numbers must be finite.");
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return value.Mantissa.ToString("R", culture) + ";" + value.Exponent.ToString(culture);
        }

        internal static BigDouble Decode(string stored, BigDouble defaultValue)
        {
            if (stored == null) return defaultValue;
            string[] parts = stored.Split(';');
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out double mantissa)
                || double.IsNaN(mantissa) || double.IsInfinity(mantissa)
                || !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, culture, out long exponent)) return defaultValue;
            return new BigDouble(mantissa, exponent);
        }
    }
}
