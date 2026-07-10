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
        bool HasKey(string key);
        void DeleteKey(string key);
        void Save();
    }
}
