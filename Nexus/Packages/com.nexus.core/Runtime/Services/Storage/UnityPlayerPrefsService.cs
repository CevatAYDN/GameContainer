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

        public bool HasKey(string key)
        {
            return PlayerPrefs.HasKey(key);
        }

        public void DeleteKey(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }

        public void Save()
        {
            PlayerPrefs.Save();
        }
    }
}
