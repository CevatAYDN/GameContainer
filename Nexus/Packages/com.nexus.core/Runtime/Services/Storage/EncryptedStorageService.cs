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
    ///
    /// File format (version 2):
    ///   [VERSION:1 byte] [IV:16 bytes] [HMAC-SHA256:32 bytes] [AES-256 ciphertext:N bytes]
    ///
    /// Legacy format (version 1, read-only migration):
    ///   [IV:16 bytes] [HMAC-SHA256-truncated:16 bytes] [AES-128 ciphertext:N bytes]
    ///
    /// HMAC-SHA256 is computed over (IV || ciphertext) using an independent key derived
    /// from the device-bound seed. On read, HMAC is verified first — tampered payloads
    /// are detected before decryption is even attempted.
    /// </summary>
    [Preserve]
    public class EncryptedStorageService : IPlayerPrefsService, IDisposable
    {
        /// <summary>Current on-disk format version.</summary>
        private const byte CurrentFormatVersion = 2;

        /// <summary>Legacy format has no version prefix; detected by header length.</summary>
        private const int LegacyHeaderSize = 32; // 16 IV + 16 HMAC

        /// <summary>Current header size: 1 version + 16 IV + 32 HMAC = 49 bytes.</summary>
        private const int HeaderSize = 1 + 16 + 32;

        public bool AutoSave { get; set; } = false;

        private readonly byte[] _encryptionKey;
        private readonly byte[] _hmacKey;
        // Legacy AES-128 keys, retained ONLY for one-time save-data migration.
        private readonly byte[] _legacyEncryptionKey;
        private readonly byte[] _legacyHmacKey;
        private readonly string _storageFolderPath;
        private readonly Dictionary<string, string> _filePathCache = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirtyKeys = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public EncryptedStorageService(string customSalt = "Nexus_Secure_Salt_2026")
        {
            if (string.IsNullOrEmpty(customSalt))
                customSalt = "Nexus_Secure_Salt_2026";

            // Device-bound key for seed obfuscation
            string deviceId = SystemInfo.deviceUniqueIdentifier ?? "Default_Device_ID";
            string rawKeySeed = $"{deviceId}_{customSalt}_{Application.identifier}";

            using var sha256 = SHA256.Create();
            byte[] deviceBoundKey = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKeySeed));

            byte[] seedBytes = new byte[32];
            string seedKey = "NT_StorageSeed";
            if (customSalt != "Nexus_Secure_Salt_2026")
                seedKey = $"NT_StorageSeed_{customSalt}";

            string storedObfuscatedSeed = PlayerPrefs.GetString(seedKey, null);

            if (string.IsNullOrEmpty(storedObfuscatedSeed))
            {
                // Generate a cryptographically secure random seed
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(seedBytes);

                // Obfuscate the seed using the device-bound key
                byte[] obfuscatedBytes = new byte[32];
                for (int i = 0; i < 32; i++)
                    obfuscatedBytes[i] = (byte)(seedBytes[i] ^ deviceBoundKey[i % deviceBoundKey.Length]);

                PlayerPrefs.SetString(seedKey, Convert.ToBase64String(obfuscatedBytes));
                PlayerPrefs.Save();
            }
            else
            {
                try
                {
                    byte[] obfuscatedBytes = Convert.FromBase64String(storedObfuscatedSeed);
                    for (int i = 0; i < 32; i++)
                        seedBytes[i] = (byte)(obfuscatedBytes[i] ^ deviceBoundKey[i % deviceBoundKey.Length]);
                }
                catch
                {
                    // Fallback in case of corruption: generate new
                    using var rng = RandomNumberGenerator.Create();
                    rng.GetBytes(seedBytes);

                    byte[] obfuscatedBytes = new byte[32];
                    for (int i = 0; i < 32; i++)
                        obfuscatedBytes[i] = (byte)(seedBytes[i] ^ deviceBoundKey[i % deviceBoundKey.Length]);

                    PlayerPrefs.SetString(seedKey, Convert.ToBase64String(obfuscatedBytes));
                    PlayerPrefs.Save();
                }
            }

            // Derive actual encryption & HMAC keys from the secure random seed
            byte[] finalHash = sha256.ComputeHash(seedBytes);

            // AES-256 key: full 32-byte hash
            _encryptionKey = finalHash;

            // Independent HMAC key: SHA256(seed || "hmac")
            byte[] hmacSalt = Encoding.UTF8.GetBytes("hmac");
            byte[] hmacSeed = new byte[seedBytes.Length + hmacSalt.Length];
            Buffer.BlockCopy(seedBytes, 0, hmacSeed, 0, seedBytes.Length);
            Buffer.BlockCopy(hmacSalt, 0, hmacSeed, seedBytes.Length, hmacSalt.Length);
            _hmacKey = sha256.ComputeHash(hmacSeed);

            // Legacy AES-128 key split kept for reading pre-migration save files;
            // successfully decrypted legacy payloads are re-encrypted with AES-256 (v2 format).
            _legacyEncryptionKey = new byte[16];
            _legacyHmacKey = new byte[16];
            Array.Copy(finalHash, 0, _legacyEncryptionKey, 0, 16);
            Array.Copy(finalHash, 16, _legacyHmacKey, 0, 16);

            _storageFolderPath = customSalt == "Nexus_Secure_Salt_2026"
                ? Path.Combine(Application.persistentDataPath, "SecureData")
                : Path.Combine(Application.persistentDataPath, $"SecureData_{customSalt}");

            if (!Directory.Exists(_storageFolderPath))
                Directory.CreateDirectory(_storageFolderPath);

            Application.focusChanged += OnFocusChanged;
            Application.quitting += OnQuitting;
        }

        private void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus)
            {
                // P2-14 fix: never block the main thread with bulk file IO on focus loss.
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { Save(); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Background save on focus loss failed: {ex.Message}");
                    }
                });
            }
        }

        private void OnQuitting() => Save();

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

        public void SetInt(string key, int value) => SetString(key, value.ToString());

        public bool GetBool(string key, bool defaultValue = false)
        {
            string valStr = GetString(key, null);
            return valStr != null && bool.TryParse(valStr, out bool res) ? res : defaultValue;
        }

        public void SetBool(string key, bool value) => SetString(key, value.ToString());

        public float GetFloat(string key, float defaultValue = 0f)
        {
            string valStr = GetString(key, null);
            return valStr != null && float.TryParse(valStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float res) ? res : defaultValue;
        }

        public void SetFloat(string key, float value) => SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public long GetLong(string key, long defaultValue = 0L)
        {
            string valStr = GetString(key, null);
            return valStr != null && long.TryParse(valStr, out long res) ? res : defaultValue;
        }

        public void SetLong(string key, long value) => SetString(key, value.ToString());

        public BigDouble GetBigDouble(string key, BigDouble defaultValue = default)
        {
            string valStr = GetString(key, null);
            if (valStr == null) return defaultValue;
            string[] parts = valStr.Split(';');
            if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m)
                && long.TryParse(parts[1], out long e))
            {
                return new BigDouble(m, e);
            }
            return defaultValue;
        }

        public void SetBigDouble(string key, BigDouble value)
        {
            SetString(key, $"{value.Mantissa.ToString(System.Globalization.CultureInfo.InvariantCulture)};{value.Exponent}");
        }

        public string GetString(string key, string defaultValue = "")
        {
            if (string.IsNullOrEmpty(key)) return defaultValue;

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out string cachedVal))
                    return cachedVal ?? defaultValue;

                string filePath = GetFilePath(key);
                if (!File.Exists(filePath))
                {
                    _cache[key] = null; // Cache negative result
                    return defaultValue;
                }

                try
                {
                    byte[] rawData = File.ReadAllBytes(filePath);
                    if (rawData.Length < LegacyHeaderSize)
                    {
                        _cache[key] = null;
                        return defaultValue;
                    }

                    // Detect format from header length.
                    // Version 2: 1 (version) + 16 (IV) + 32 (HMAC) = 49 bytes minimum header.
                    // Version 1 (legacy): 16 (IV) + 16 (HMAC) = 32 bytes minimum header.
                    bool isVersion2 = rawData.Length >= HeaderSize && rawData[0] == CurrentFormatVersion;

                    if (isVersion2)
                    {
                        return ReadVersion2(rawData, key, defaultValue);
                    }
                    else
                    {
                        return ReadLegacy(rawData, key, defaultValue);
                    }
                }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Failed to read/decrypt save key '{key}': {ex.Message}");
                    _cache[key] = null;
                    return defaultValue;
                }
            }
        }

        /// <summary>
        /// Reads a version-2 format file: VERSION(1) + IV(16) + HMAC(32) + cipherText.
        /// </summary>
        private string ReadVersion2(byte[] rawData, string key, string defaultValue)
        {
            // Offset: version(1) + IV(16) = 17
            const int ivOffset = 1;
            const int hmacOffset = 17; // 1 + 16
            const int cipherOffset = 49; // 1 + 16 + 32

            byte[] iv = new byte[16];
            byte[] hmac = new byte[32];
            byte[] cipherText = new byte[rawData.Length - cipherOffset];

            Buffer.BlockCopy(rawData, ivOffset, iv, 0, 16);
            Buffer.BlockCopy(rawData, hmacOffset, hmac, 0, 32);
            Buffer.BlockCopy(rawData, cipherOffset, cipherText, 0, cipherText.Length);

            // Verify HMAC-SHA256 (full 32 bytes)
            byte[] computedHmac = ComputeHmac(cipherText, iv);
            if (!CompareHashes(hmac, computedHmac))
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Save file tampering detected for key: {key}! Reverting to default.");
                _cache[key] = null;
                return defaultValue;
            }

            // Decrypt payload with AES-256
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
            string val = Encoding.UTF8.GetString(plainBytes);
            _cache[key] = val;
            return val;
        }

        /// <summary>
        /// Reads a legacy (v1) format file: IV(16) + truncated-HMAC(16) + cipherText.
        /// Attempts legacy AES-128 decryption; on success, re-encrypts with AES-256 (v2 format).
        /// </summary>
        private string ReadLegacy(byte[] rawData, string key, string defaultValue)
        {
            byte[] iv = new byte[16];
            byte[] hmac = new byte[16];
            byte[] cipherText = new byte[rawData.Length - 32];

            Buffer.BlockCopy(rawData, 0, iv, 0, 16);
            Buffer.BlockCopy(rawData, 16, hmac, 0, 16);
            Buffer.BlockCopy(rawData, 32, cipherText, 0, cipherText.Length);

            if (TryLegacyDecrypt(iv, hmac, cipherText, out string legacyVal))
            {
                // Successfully migrated: re-encrypt with AES-256 (v2 format) in-place.
                _cache[key] = legacyVal;
                SaveKeyToDisk(key, legacyVal);
                return legacyVal;
            }

            NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Save file tampering detected for key: {key}! Reverting to default.");
            _cache[key] = null;
            return defaultValue;
        }

        public void SetString(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;

            lock (_lock)
            {
                _cache.TryGetValue(key, out string oldVal);
                if (oldVal == value) return;

                _cache[key] = value;

                if (AutoSave)
                    SaveKeyToDisk(key, value);
                else
                    _dirtyKeys.Add(key);
            }
        }

        /// <summary>
        /// Writes a version-2 format file: VERSION(1) + IV(16) + HMAC-SHA256(32) + cipherText.
        /// Uses atomic write with temp file + retry for Windows handle-contention safety.
        /// </summary>
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

                // VERSION(1) + IV(16) + HMAC(32) + cipherText
                byte[] finalBuffer = new byte[HeaderSize + cipherText.Length];
                finalBuffer[0] = CurrentFormatVersion;
                Buffer.BlockCopy(aes.IV, 0, finalBuffer, 1, 16);
                Buffer.BlockCopy(hmac, 0, finalBuffer, 17, 32);
                Buffer.BlockCopy(cipherText, 0, finalBuffer, HeaderSize, cipherText.Length);

                // Atomic write with temp file backup
                string tempPath = filePath + ".tmp";
                File.WriteAllBytes(tempPath, finalBuffer);

                const int maxAttempts = 3;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(filePath)) File.Delete(filePath);
                        File.Move(tempPath, filePath);
                        break;
                    }
                    catch (IOException) when (attempt < maxAttempts - 1)
                    {
                        System.Threading.Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Save write failed for key '{key}': {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy (v1) format migration helper: verifies and decrypts a payload written
        /// with AES-128 + truncated HMAC-SHA256 (16-byte). Returns false if the payload
        /// does not match the legacy format.
        /// </summary>
        private bool TryLegacyDecrypt(byte[] iv, byte[] hmac, byte[] cipherText, out string value)
        {
            value = null;
            try
            {
                byte[] legacyHmac = ComputeHmacWithKey(cipherText, iv, _legacyHmacKey);
                if (!CompareHashes(hmac, legacyHmac)) return false;

                using var aes = Aes.Create();
                aes.Key = _legacyEncryptionKey;
                aes.IV = iv;
                using var decryptor = aes.CreateDecryptor();
                byte[] plainBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
                value = Encoding.UTF8.GetString(plainBytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string ExportEncryptedSaveData(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_lock)
            {
                Save();
                string path = GetFilePath(key);
                if (!File.Exists(path)) return null;
                byte[] raw = File.ReadAllBytes(path);
                return Convert.ToBase64String(raw);
            }
        }

        public bool ImportEncryptedSaveData(string key, string base64Data)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(base64Data)) return false;
            try
            {
                byte[] rawData = Convert.FromBase64String(base64Data);
                if (rawData.Length < HeaderSize || rawData[0] != CurrentFormatVersion) return false;

                string path = GetFilePath(key);
                File.WriteAllBytes(path, rawData);
                lock (_lock)
                {
                    _cache.Remove(key);
                }
                return true;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Save import failed for key '{key}': {ex.Message}");
                return false;
            }
        }

        public bool HasKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out string cachedVal))
                    return cachedVal != null;
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
                    try { File.Delete(path); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Delete failed for key '{key}': {ex.Message}");
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
                        SaveKeyToDisk(key, val);
                }
                _dirtyKeys.Clear();
            }
        }

        private string GetFilePath(string key)
        {
            if (_filePathCache.TryGetValue(key, out string cached)) return cached;

            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
            string hashedFileName = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".dat";
            string path = Path.Combine(_storageFolderPath, hashedFileName);
            _filePathCache[key] = path;
            return path;
        }

        /// <summary>
        /// Computes a full 32-byte HMAC-SHA256 over (IV || cipherText) using the current HMAC key.
        /// </summary>
        private byte[] ComputeHmac(byte[] data, byte[] iv)
        {
            return ComputeHmacWithKey(data, iv, _hmacKey);
        }

        /// <summary>
        /// Computes a full 32-byte HMAC-SHA256 over (IV || cipherText) using the specified key.
        /// </summary>
        private static byte[] ComputeHmacWithKey(byte[] data, byte[] iv, byte[] key)
        {
            using var hmac = new HMACSHA256(key);
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);
            hmac.TransformFinalBlock(data, 0, data.Length);
            return hmac.Hash; // Full 32-byte HMAC-SHA256 output
        }

        /// <summary>
        /// Constant-time hash comparison to prevent timing attacks.
        /// </summary>
        private static bool CompareHashes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
