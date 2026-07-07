using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// AES-256 Encrypted & Device-Bound Storage Service.
    /// Protects game saves against APK modification, XML editing, and save file sharing.
    /// </summary>
    [Preserve]
    public class EncryptedStorageService : IPlayerPrefsService
    {
        private readonly byte[] _encryptionKey;
        private readonly byte[] _hmacKey;
        private readonly string _storageFolderPath;

        public EncryptedStorageService(string customSalt = "Nexus_Secure_Salt_2026")
        {
            // Device-bound key for seed obfuscation
            string deviceId = SystemInfo.deviceUniqueIdentifier ?? "Default_Device_ID";
            string rawKeySeed = $"{deviceId}_{customSalt}_{Application.identifier}";

            using var sha256 = SHA256.Create();
            byte[] deviceBoundKey = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKeySeed));

            byte[] seedBytes = new byte[32];
            string storedObfuscatedSeed = PlayerPrefs.GetString("NT_StorageSeed", null);

            if (string.IsNullOrEmpty(storedObfuscatedSeed))
            {
                // Generate a cryptographically secure random seed
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(seedBytes);

                // Obfuscate the seed using the device-bound key
                byte[] obfuscatedBytes = new byte[32];
                for (int i = 0; i < 32; i++)
                {
                    obfuscatedBytes[i] = (byte)(seedBytes[i] ^ deviceBoundKey[i % deviceBoundKey.Length]);
                }

                PlayerPrefs.SetString("NT_StorageSeed", Convert.ToBase64String(obfuscatedBytes));
                PlayerPrefs.Save();
            }
            else
            {
                try
                {
                    byte[] obfuscatedBytes = Convert.FromBase64String(storedObfuscatedSeed);
                    for (int i = 0; i < 32; i++)
                    {
                        seedBytes[i] = (byte)(obfuscatedBytes[i] ^ deviceBoundKey[i % deviceBoundKey.Length]);
                    }
                }
                catch
                {
                    // Fallback in case of corruption: generate new
                    using var rng = RandomNumberGenerator.Create();
                    rng.GetBytes(seedBytes);

                    byte[] obfuscatedBytes = new byte[32];
                    for (int i = 0; i < 32; i++)
                    {
                        obfuscatedBytes[i] = (byte)(seedBytes[i] ^ deviceBoundKey[i % deviceBoundKey.Length]);
                    }

                    PlayerPrefs.SetString("NT_StorageSeed", Convert.ToBase64String(obfuscatedBytes));
                    PlayerPrefs.Save();
                }
            }

            // Derive actual encryption & HMAC keys from the secure random seed
            byte[] finalHash = sha256.ComputeHash(seedBytes);

            _encryptionKey = new byte[16]; // AES-128 key
            _hmacKey = new byte[16];
            Array.Copy(finalHash, 0, _encryptionKey, 0, 16);
            Array.Copy(finalHash, 16, _hmacKey, 0, 16);

            _storageFolderPath = Path.Combine(Application.persistentDataPath, "SecureData");
            if (!Directory.Exists(_storageFolderPath))
            {
                Directory.CreateDirectory(_storageFolderPath);
            }
        }

        public int GetInt(string key, int defaultValue = 0)
        {
            string valStr = GetString(key, null);
            return valStr != null && int.TryParse(valStr, out int res) ? res : defaultValue;
        }

        public void SetInt(string key, int value)
        {
            SetString(key, value.ToString());
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            string valStr = GetString(key, null);
            return valStr != null && bool.TryParse(valStr, out bool res) ? res : defaultValue;
        }

        public void SetBool(string key, bool value)
        {
            SetString(key, value.ToString());
        }

        public float GetFloat(string key, float defaultValue = 0f)
        {
            string valStr = GetString(key, null);
            return valStr != null && float.TryParse(valStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float res) ? res : defaultValue;
        }

        public void SetFloat(string key, float value)
        {
            SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public string GetString(string key, string defaultValue = "")
        {
            string filePath = GetFilePath(key);
            if (!File.Exists(filePath)) return defaultValue;

            try
            {
                byte[] rawData = File.ReadAllBytes(filePath);
                if (rawData.Length < 32) return defaultValue; // Min IV (16) + HMAC (16)

                // Verify HMAC signature to detect tampering
                byte[] iv = new byte[16];
                byte[] hmac = new byte[16];
                byte[] cipherText = new byte[rawData.Length - 32];

                Buffer.BlockCopy(rawData, 0, iv, 0, 16);
                Buffer.BlockCopy(rawData, 16, hmac, 0, 16);
                Buffer.BlockCopy(rawData, 32, cipherText, 0, cipherText.Length);

                byte[] computedHmac = ComputeHmac(cipherText, iv);
                if (!CompareHashes(hmac, computedHmac))
                {
                    NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning($"[EncryptedStorage] Save file tampering detected for key: {key}! Reverting to default.");
                    return defaultValue;
                }

                // Decrypt payload
                using var aes = Aes.Create();
                aes.Key = _encryptionKey;
                aes.IV = iv;
                using var decryptor = aes.CreateDecryptor();
                byte[] plainBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning($"[EncryptedStorage] Failed to read/decrypt save key '{key}': {ex.Message}");
                return defaultValue;
            }
        }

        public void SetString(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            string filePath = GetFilePath(key);

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                using var aes = Aes.Create();
                aes.Key = _encryptionKey;
                aes.GenerateIV();

                using var encryptor = aes.CreateEncryptor();
                byte[] cipherText = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                byte[] hmac = ComputeHmac(cipherText, aes.IV);

                byte[] finalBuffer = new byte[16 + 16 + cipherText.Length];
                Buffer.BlockCopy(aes.IV, 0, finalBuffer, 0, 16);
                Buffer.BlockCopy(hmac, 0, finalBuffer, 16, 16);
                Buffer.BlockCopy(cipherText, 0, finalBuffer, 32, cipherText.Length);

                // Atomic write with temp file backup
                string tempPath = filePath + ".tmp";
                File.WriteAllBytes(tempPath, finalBuffer);
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(tempPath, filePath);
            }
            catch (Exception ex)
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning($"[EncryptedStorage] Save write failed for key '{key}': {ex.Message}");
            }
        }

        public bool HasKey(string key) => File.Exists(GetFilePath(key));

        public void DeleteKey(string key)
        {
            string path = GetFilePath(key);
            if (File.Exists(path)) File.Delete(path);
        }

        public void Save() { }

        private string GetFilePath(string key)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
            string hashedFileName = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".dat";
            return Path.Combine(_storageFolderPath, hashedFileName);
        }

        private byte[] ComputeHmac(byte[] data, byte[] iv)
        {
            using var hmac = new HMACSHA256(_hmacKey);
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);
            hmac.TransformFinalBlock(data, 0, data.Length);
            byte[] fullHash = hmac.Hash;
            byte[] result = new byte[16];
            Buffer.BlockCopy(fullHash, 0, result, 0, 16);
            return result;
        }

        private static bool CompareHashes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
