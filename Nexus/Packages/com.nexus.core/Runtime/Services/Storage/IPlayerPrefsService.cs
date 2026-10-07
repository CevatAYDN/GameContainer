namespace Nexus.Core.Services
{
    /// <summary>
    /// Oyunda kalıcı küçük veri saklamak için ince bir soyutlama.
    /// Modellerin doğrudan UnityEngine.PlayerPrefs'e bağımlı olmasını engeller;
    /// EditMode testlerde in-memory fake implementasyonla değiştirilebilir.
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
        bool HasKey(string key);
        void DeleteKey(string key);
        void Save();
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
