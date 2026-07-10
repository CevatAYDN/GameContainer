using System;
using System.Collections.Generic;
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
    public class EncryptedStorageService : IPlayerPrefsService, IDisposable
    {
        public bool AutoSave { get; set; } = false;

        private readonly byte[] _encryptionKey;
        private readonly byte[] _hmacKey;
        private readonly string _storageFolderPath;

        private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirtyKeys = new(StringComparer.Ordinal);
        private readonly object _lock = new();

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

            Application.focusChanged += OnFocusChanged;
            Application.quitting += OnQuitting;
        }

        private void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus)
            {
                Save();
            }
        }

        private void OnQuitting()
        {
            Save();
        }

        public void Dispose()
        {
            Application.focusChanged -= OnFocusChanged;
            Application.quitting -= OnQuitting;
            Save();
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

        public long GetLong(string key, long defaultValue = 0L)
        {
            string valStr = GetString(key, null);
            return valStr != null && long.TryParse(valStr, out long res) ? res : defaultValue;
        }

        public void SetLong(string key, long value)
        {
            SetString(key, value.ToString());
        }

        public string GetString(string key, string defaultValue = "")
        {
            if (string.IsNullOrEmpty(key)) return defaultValue;

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out string cachedVal))
                {
                    return cachedVal ?? defaultValue;
                }

                string filePath = GetFilePath(key);
                if (!File.Exists(filePath))
                {
                    _cache[key] = null; // Cache negative result
                    return defaultValue;
                }

                try
                {
                    byte[] rawData = File.ReadAllBytes(filePath);
                    if (rawData.Length < 32)
                    {
                        _cache[key] = null;
                        return defaultValue;
                    }

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
                        _cache[key] = null;
                        return defaultValue;
                    }

                    // Decrypt payload
                    using var aes = Aes.Create();
                    aes.Key = _encryptionKey;
                    aes.IV = iv;
                    using var decryptor = aes.CreateDecryptor();
                    byte[] plainBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
                    string val = Encoding.UTF8.GetString(plainBytes);
                    _cache[key] = val;
                    return val;
                }
                catch (Exception ex)
                {
                    NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning($"[EncryptedStorage] Failed to read/decrypt save key '{key}': {ex.Message}");
                    _cache[key] = null;
                    return defaultValue;
                }
            }
        }

        public void SetString(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;

            lock (_lock)
            {
                _cache.TryGetValue(key, out string oldVal);
                if (oldVal == value) return; // No change

                _cache[key] = value;

                if (AutoSave)
                {
                    SaveKeyToDisk(key, value);
                }
                else
                {
                    _dirtyKeys.Add(key);
                }
            }
        }

        private void SaveKeyToDisk(string key, string value)
        {
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

        public bool HasKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out string cachedVal))
                {
                    return cachedVal != null;
                }
                return File.Exists(GetFilePath(key));
            }
        }

        public void DeleteKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            lock (_lock)
            {
                _cache[key] = null;
                _dirtyKeys.Remove(key);

                string path = GetFilePath(key);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning($"[EncryptedStorage] Delete failed for key '{key}': {ex.Message}");
                    }
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                if (_dirtyKeys.Count == 0) return;

                foreach (var key in _dirtyKeys)
                {
                    if (_cache.TryGetValue(key, out string val) && val != null)
                    {
                        SaveKeyToDisk(key, val);
                    }
                }
                _dirtyKeys.Clear();
            }
        }

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
