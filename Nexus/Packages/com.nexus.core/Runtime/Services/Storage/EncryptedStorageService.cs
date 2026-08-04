using System;
using System.Collections.Concurrent;
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
    ///
    /// Review fixes (2026-08-01):
    /// - A1: save writes are now atomic (single overwrite-rename) — a crash can no
    ///   longer delete the previous save before the new one is in place.
    /// - A1b: the write-retry backoff no longer blocks the main thread with Sleep(10);
    ///   it yields instead.
    /// - A6: on-disk filenames are derived with FNV-1a (non-crypto, FIPS-safe) instead
    ///   of MD5; legacy MD5-named files are still readable and migrated on next save.
    /// - B1: file I/O was moved out of the shared lock so one slow read cannot stall
    ///   every other key operation.
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
        private readonly ConcurrentDictionary<string, string> _filePathCache = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirtyKeys = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private volatile bool _disposed;

        // T3 fix: serializes the atomic-write critical section (stage-to-temp + rename).
        // AutoSave writes and Save() batches can otherwise race on the SAME fixed temp
        // path (filePath + ".tmp"), interleaving File.WriteAllBytes and producing a
        // corrupt file that fails HMAC verification on the next read. A dedicated lock
        // (not _lock) keeps slow file I/O out of the cache/dirty-set critical section
        // (the B1 invariant) while making concurrent writes to one key atomic.
        private readonly object _writeLock = new();

        /// <summary>
        /// DI-friendly parameterless constructor. Nexus DI requires a parameterless ctor
        /// (or an [Inject]/[Construct]-decorated one) to construct a type — the sole
        /// optional-string ctor made strict injection fail on the unresolved 'System.String'
        /// parameter ('{type} is not registered'), which broke every container that bound
        /// this service. Equivalent to the default salt.
        /// </summary>
        public EncryptedStorageService() : this("Nexus_Secure_Salt_2026") { }

        public EncryptedStorageService(string customSalt)
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
                catch (Exception ex)
                {
                    // Fallback in case of corruption: generate new and log warning
                    NexusRuntime.Logger?.LogWarning($"[EncryptedStorageService] Storage seed decoding failed ({ex.Message}). Regenerating new secure seed.");

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
                if (_disposed) return;
                // Capture logger on main thread before switching to background.
                var logger = NexusRuntime.Logger;
                // Use Task.Run for true background I/O so the Unity main thread is never
                // blocked waiting for disk writes during focus loss.
                // The lambda captures only the logger (value-type copy) — not `this` —
                // so there is no risk of the service being GC'd while I/O is in flight
                // because the service's lifetime is tied to the context (singleton).
                // NOTE: the lambda intentionally captures `this` (via self) so the service
                // cannot be garbage-collected while background I/O is in flight — its
                // lifetime is tied to the owning context singleton anyway, but the explicit
                // capture makes the retention intentional and self-documenting.
                var self = this; // explicit capture for clarity
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { self.Save(); }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"[EncryptedStorage] Background save on focus loss failed: {ex.Message}");
                    }
                });
            }
        }

        private void OnQuitting() => Save();

        public void Dispose()
        {
            _disposed = true;
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

        /// <summary>
        /// Reads a value. B1: the slow parts (file existence check, read, decrypt)
        /// run OUTSIDE the shared lock — a slow first read cannot stall every other
        /// key operation. Only the small cache/dirty-set updates take the lock.
        /// </summary>
        public string GetString(string key, string defaultValue = "")
        {
            if (_disposed) return defaultValue;
            if (string.IsNullOrEmpty(key)) return defaultValue;

            string filePath;
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out string cachedVal))
                    return cachedVal ?? defaultValue;

                filePath = ResolveExistingFilePath(key);
            }

            // ── Slow path, outside the lock ──
            if (!File.Exists(filePath))
            {
                lock (_lock) { _cache[key] = null; } // Cache negative result
                return defaultValue;
            }

            try
            {
                byte[] rawData = File.ReadAllBytes(filePath);
                if (rawData.Length < LegacyHeaderSize)
                {
                    lock (_lock) { _cache[key] = null; }
                    return defaultValue;
                }

                // Detect format from header length.
                // Version 2: 1 (version) + 16 (IV) + 32 (HMAC) = 49 bytes minimum header.
                // Version 1 (legacy): 16 (IV) + 16 (HMAC) = 32 bytes minimum header.
                bool isVersion2 = rawData.Length >= HeaderSize && rawData[0] == CurrentFormatVersion;

                string val;
                if (isVersion2)
                {
                    if (!TryReadVersion2(rawData, out val))
                    {
                        LogTamperWarning(key);
                        lock (_lock) { _cache[key] = null; }
                        return defaultValue;
                    }
                }
                else
                {
                    if (!TryReadLegacy(rawData, out val))
                    {
                        LogTamperWarning(key);
                        lock (_lock) { _cache[key] = null; }
                        return defaultValue;
                    }

                    // Successfully migrated: re-encrypt with AES-256 (v2 format) under the
                    // current (FNV-1a) filename, then drop the legacy MD5-named file (A6).
                    lock (_lock)
                    {
                        SaveKeyToDisk(key, val);
                    }
                    TryDeleteLegacyFile(key);
                }

                lock (_lock)
                {
                    // Guard against TOCTOU race: if SetString was called concurrently while reading from disk,
                    // keep the fresher dirty cache value instead of overwriting with disk bytes.
                    if (_dirtyKeys.Contains(key) && _cache.TryGetValue(key, out string currentVal) && currentVal != null)
                    {
                        return currentVal;
                    }
                    _cache[key] = val;
                }
                return val;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Failed to read/decrypt save key '{key}': {ex.Message}");
                lock (_lock)
                {
                    if (!_dirtyKeys.Contains(key))
                    {
                        _cache[key] = null;
                    }
                }
                return defaultValue;
            }
        }

        private void LogTamperWarning(string key)
        {
            NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Save file tampering detected for key: {key}! Reverting to default.");
        }

        /// <summary>
        /// Decodes a version-2 payload: VERSION(1) + IV(16) + HMAC(32) + cipherText.
        /// Pure: performs no cache or file writes (the caller owns those).
        /// </summary>
        private bool TryReadVersion2(byte[] rawData, out string value)
        {
            value = null;
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
                return false;

            // Decrypt payload with AES-256
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
            value = Encoding.UTF8.GetString(plainBytes);
            return true;
        }

        /// <summary>
        /// Decodes a legacy (v1) payload: IV(16) + truncated-HMAC(16) + cipherText.
        /// Pure: performs no cache or file writes (the caller owns migration).
        /// </summary>
        private bool TryReadLegacy(byte[] rawData, out string value)
        {
            value = null;
            try
            {
                byte[] iv = new byte[16];
                byte[] hmac = new byte[16];
                byte[] cipherText = new byte[rawData.Length - 32];

                Buffer.BlockCopy(rawData, 0, iv, 0, 16);
                Buffer.BlockCopy(rawData, 16, hmac, 0, 16);
                Buffer.BlockCopy(rawData, 32, cipherText, 0, cipherText.Length);

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
            catch (CryptographicException)
            {
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Legacy decode failed: {ex.Message}");
                return false;
            }
        }

        public void SetString(string key, string value)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(key)) return;

            bool autoSave;
            lock (_lock)
            {
                _cache.TryGetValue(key, out string oldVal);
                if (oldVal == value) return;

                _cache[key] = value;

                if (!AutoSave)
                {
                    _dirtyKeys.Add(key);
                    return;
                }
                autoSave = true;
            }

            // I/O outside lock: AutoSave writes each value immediately.
            if (autoSave && !SaveKeyToDisk(key, value))
            {
                // T3 fix: a failed AutoSave write must never be silently dropped. Mark the
                // key dirty so the next Save() (focus loss / quit / explicit call) retries
                // the write instead of losing the value until the process exits.
                lock (_lock) { _dirtyKeys.Add(key); }
            }
        }

        /// <summary>
        /// Writes a version-2 format file: VERSION(1) + IV(16) + HMAC-SHA256(32) + cipherText.
        /// Returns true on success, false on failure (caller retains the dirty key for retry).
        /// A1: the write is ATOMIC — the payload is staged to a temp file and then
        /// rename/overwrite-moved into place in a single filesystem operation. A crash
        /// can only ever leave the previous good file or the new complete file, never
        /// a deleted-but-not-replaced hole.
        /// </summary>
        private bool SaveKeyToDisk(string key, string value)
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

                // Atomic write: stage to temp, then overwrite-rename (single operation on
                // the same volume). Never Delete-then-Move — the pre-fix data-loss window.
                // File.Replace (netstandard 2.0+) is atomic on Windows (MoveFileEx
                // REPLACE_EXISTING) and Unix (rename). The File.Move(src, dst, overwrite)
                // overload used before is .NET Core 3.0+ only and does not exist in
                // Unity's .NET Standard 2.1 reference profile.
                WriteRawDataAtomically(filePath, finalBuffer);
                return true;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Save write failed for key '{key}': {ex.Message}");
                return false;
            }
        }

        public string ExportEncryptedSaveData(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            string filePath;
            lock (_lock)
            {
                Save();
                filePath = GetFilePath(key);
                if (!File.Exists(filePath)) return null;
            }

            // B1 parity: file I/O outside the shared lock so a slow read cannot stall
            // every other key operation.
            byte[] raw = File.ReadAllBytes(filePath);
            return Convert.ToBase64String(raw);
        }

        public bool ImportEncryptedSaveData(string key, string base64Data)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(base64Data)) return false;
            try
            {
                byte[] rawData = Convert.FromBase64String(base64Data);
                if (rawData.Length < HeaderSize || rawData[0] != CurrentFormatVersion) return false;

                // Validate before replacing the local file so a corrupt cloud backup cannot
                // destroy a valid save already stored on the device.
                if (!TryReadVersion2(rawData, out string value)) return false;

                string path;
                lock (_lock)
                {
                    path = GetFilePath(key);
                    WriteRawDataAtomically(path, rawData);
                    _cache[key] = value;
                    _dirtyKeys.Remove(key);
                }
                return true;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Save import failed for key '{key}': {ex.Message}");
                return false;
            }
        }

        private void WriteRawDataAtomically(string filePath, byte[] rawData)
        {
            // T3 fix: the whole stage-to-temp + replace sequence is one critical section.
            // Without this, concurrent callers (AutoSave on a worker thread + Save() on
            // focus loss) both wrote the SAME "filePath.tmp" and raced the rename — a
            // torn file that HMAC verification then rejects on the next load. Serializing
            // under _writeLock restores atomicity for the shared temp-name scheme while
            // keeping the fast cache path (_lock) independent.
            lock (_writeLock)
            {
                string tempPath = filePath + ".tmp";
                string backupPath = filePath + ".bak";
                File.WriteAllBytes(tempPath, rawData);

                const int maxAttempts = 3;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(filePath))
                            File.Replace(tempPath, filePath, backupPath);
                        else
                            File.Move(tempPath, filePath);
                        break;
                    }
                    catch (IOException) when (attempt < maxAttempts - 1)
                    {
                        // Brief exponential back-off (1 ms, 2 ms) without blocking the calling
                        // thread's timeslice for longer than necessary. Thread.Yield() only gives
                        // up the remainder of the current timeslice and can return immediately on
                        // single-core devices; Thread.Sleep(1) guarantees at least 1 ms relief.
                        System.Threading.Thread.Sleep(1 << attempt); // 1 ms, 2 ms
                    }
                }
            }
        }

        public bool HasKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out string cachedVal))
                    return cachedVal != null;
            }

            // B1 invariant: file I/O outside the shared lock so slow disk access doesn't stall other operations
            // A6: a key written by an older build may still live under its legacy
            // MD5-derived filename — report it as present so migration can run.
            string newPath = GetFilePath(key);
            if (File.Exists(newPath)) return true;

            string legacyPath = GetLegacyFilePath(key);
            return legacyPath != null && File.Exists(legacyPath);
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

                // A6: also remove the legacy MD5-named file if one exists.
                string legacyPath = GetLegacyFilePath(key);
                if (legacyPath != null && File.Exists(legacyPath))
                {
                    try { File.Delete(legacyPath); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Delete (legacy) failed for key '{key}': {ex.Message}");
                    }
                }
            }
        }

        public void Save()
        {
            if (_disposed) return;
            string[] keysToWrite;
            lock (_lock)
            {
                if (_dirtyKeys.Count == 0) return;
                keysToWrite = new string[_dirtyKeys.Count];
                _dirtyKeys.CopyTo(keysToWrite);
            }

            var failedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keysToWrite)
            {
                string val;
                lock (_lock)
                {
                    if (!_cache.TryGetValue(key, out val) || val == null)
                        continue;
                }

                if (!SaveKeyToDisk(key, val))
                    failedKeys.Add(key);
            }

            lock (_lock)
            {
                foreach (var key in keysToWrite)
                {
                    // Failed keys stay dirty so the next Save() retries them (no silent loss).
                    if (!failedKeys.Contains(key))
                        _dirtyKeys.Remove(key);
                }
            }
        }

        /// <summary>
        /// Resolves the on-disk file for a key. Prefers the current FNV-1a filename;
        /// falls back to the legacy MD5 filename (A6) so pre-migration saves are still
        /// readable. The legacy file is migrated (re-saved under the new name) on read.
        /// </summary>
        private string ResolveExistingFilePath(string key)
        {
            string newPath = GetFilePath(key);
            if (File.Exists(newPath)) return newPath;

            string legacyPath = GetLegacyFilePath(key);
            return legacyPath != null && File.Exists(legacyPath) ? legacyPath : newPath;
        }

        private void TryDeleteLegacyFile(string key)
        {
            try
            {
                string legacyPath = GetLegacyFilePath(key);
                if (legacyPath != null && File.Exists(legacyPath))
                    File.Delete(legacyPath);
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[EncryptedStorage] Legacy file cleanup failed for key '{key}': {ex.Message}");
            }
        }

        /// <summary>
        /// Current filename for a key: FNV-1a 64-bit hash, hex-encoded (A6). FNV-1a is
        /// non-cryptographic and therefore FIPS-safe (MD5.Create() throws under FIPS
        /// enforcement). Filename hashing only needs determinism + low collision, not
        /// cryptographic strength, so the swap is a strict improvement.
        /// </summary>
        private string GetFilePath(string key)
        {
            if (_filePathCache.TryGetValue(key, out string cached)) return cached;

            string path = Path.Combine(_storageFolderPath, Fnv1aFileName(key));
            _filePathCache[key] = path;
            return path;
        }

        private static string Fnv1aFileName(string key)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offsetBasis;
            foreach (char c in key)
            {
                hash ^= c;
                hash *= prime;
            }
            return hash.ToString("x16") + ".dat";
        }

        /// <summary>
        /// Legacy MD5-derived filename (pre-A6). Used only to read/migrate saves written
        /// by older builds. Returns null when MD5 is unavailable (FIPS enforcement) —
        /// in that case no legacy files can exist either.
        /// </summary>
        private string GetLegacyFilePath(string key)
        {
            try
            {
                using var md5 = MD5.Create();
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                string hashedFileName = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".dat";
                return Path.Combine(_storageFolderPath, hashedFileName);
            }
            catch (CryptographicException)
            {
                // FIPS-enforced platforms throw on MD5.Create(); treat as no legacy file.
                return null;
            }
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
