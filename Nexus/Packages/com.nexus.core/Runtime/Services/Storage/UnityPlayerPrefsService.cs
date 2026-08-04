using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Gerçek Unity PlayerPrefs implementasyonu.
    /// Tüm SetInt/SetBool çağrılarında hemen disk'e yazmak yerine,
    /// oyuncu önemli bir eylem yaptığında (level tamamlama, tema değiştirme vb.)
    /// çağrılabilecek bir Save() metodu sunar. SetInt'lerde otomatik Save çağrılır
    /// çünkü mevcut modeller bu davranışa bağımlı; isteyen NoopPlayerPrefs ile değiştirebilir.
    /// </summary>
    public sealed class UnityPlayerPrefsService : IPlayerPrefsService
    {
        public int GetInt(string key, int defaultValue = 0)
        {
            return PlayerPrefs.GetInt(key, defaultValue);
        }

        public void SetInt(string key, int value)
        {
            PlayerPrefs.SetInt(key, value);
            PlayerPrefs.Save();
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            return PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;
        }

        public void SetBool(string key, bool value)
        {
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
        }

        public string GetString(string key, string defaultValue = "")
        {
            return PlayerPrefs.GetString(key, defaultValue);
        }

        public void SetString(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        public float GetFloat(string key, float defaultValue = 0f)
        {
            return PlayerPrefs.GetFloat(key, defaultValue);
        }

        public void SetFloat(string key, float value)
        {
            PlayerPrefs.SetFloat(key, value);
            PlayerPrefs.Save();
        }

        public long GetLong(string key, long defaultValue = 0L)
        {
            string stringValue = PlayerPrefs.GetString(key, null);
            if (stringValue != null && long.TryParse(stringValue, out long val))
            {
                return val;
            }
            return defaultValue;
        }

        public void SetLong(string key, long value)
        {
            PlayerPrefs.SetString(key, value.ToString());
            PlayerPrefs.Save();
        }

        public BigDouble GetBigDouble(string key, BigDouble defaultValue = default)
        {
            string stringValue = PlayerPrefs.GetString(key, null);
            if (stringValue == null) return defaultValue;
            string[] parts = stringValue.Split(';');
            if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m)
                && long.TryParse(parts[1], out long e))
            {
                return new BigDouble(m, e);
            }
            return defaultValue;
        }

        public void SetBigDouble(string key, BigDouble value)
        {
            PlayerPrefs.SetString(key, $"{value.Mantissa.ToString(System.Globalization.CultureInfo.InvariantCulture)};{value.Exponent}");
            PlayerPrefs.Save();
        }

        public bool HasKey(string key)
        {
            return PlayerPrefs.HasKey(key);
        }

        public void DeleteKey(string key)
        {
            PlayerPrefs.DeleteKey(key);
        }

        public void Save()
        {
            PlayerPrefs.Save();
        }
    }
}
