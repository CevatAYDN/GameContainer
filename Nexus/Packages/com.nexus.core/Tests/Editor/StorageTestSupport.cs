using System.IO;

namespace Nexus.Editor.Tests
{
    /// <summary>What an EncryptedStorageService test leaves on the machine: the salt's seed in PlayerPrefs and its folder in persistentDataPath.</summary>
    internal static class StorageTestSupport
    {
        public static void ForgetStore(string salt)
        {
            UnityEngine.PlayerPrefs.DeleteKey($"NT_StorageSeed_{salt}");
            UnityEngine.PlayerPrefs.DeleteKey($"NT_StorageSeed_{salt}_App");
            var folder = Path.Combine(UnityEngine.Application.persistentDataPath, $"SecureData_{salt}");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
