using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The real Unity PlayerPrefs implementation.
    /// Instead of writing to disk on every SetInt/SetBool call, it offers a Save() method to call
    /// when the player does something that matters (completes a level, changes the theme, and so on).
    /// Set* calls change only the in-memory PlayerPrefs value; a point that needs the value kept must
    /// call Save() (Unity also saves when the application quits).
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
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            return PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;
        }

        public void SetBool(string key, bool value)
        {
            PlayerPrefs.SetInt(key, value ? 1 : 0);
        }

        public string GetString(string key, string defaultValue = "")
        {
            return PlayerPrefs.GetString(key, defaultValue);
        }

        public void SetString(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
        }

        public float GetFloat(string key, float defaultValue = 0f)
        {
            return PlayerPrefs.GetFloat(key, defaultValue);
        }

        public void SetFloat(string key, float value)
        {
            PlayerPrefs.SetFloat(key, value);
        }

        public long GetLong(string key, long defaultValue = 0L)
        {
            string stringValue = PlayerPrefs.GetString(key, null);
            if (stringValue != null && IntegerStorageCodec.TryDecode(stringValue, out long val))
            {
                return val;
            }
            return defaultValue;
        }

        public void SetLong(string key, long value)
        {
            PlayerPrefs.SetString(key, IntegerStorageCodec.Encode(value));
        }

        public BigDouble GetBigDouble(string key, BigDouble defaultValue = default)
        {
            return BigDoubleStorageCodec.Decode(PlayerPrefs.GetString(key, null), defaultValue);
        }

        public void SetBigDouble(string key, BigDouble value)
        {
            PlayerPrefs.SetString(key, BigDoubleStorageCodec.Encode(value));
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
