# Nexus Core — 13 Servis Tam Audit Raporu

**Proje:** GameContainer / `com.nexus.core`
**Tarih:** 31 Temmuz 2026
**Kapsam:** `Runtime/Services/` altındaki 13 prodüksiyon motor servisi
**Hedef Platform:** Unity 6 (6000.x), C# 9.0+, .NET Standard 2.0 — mobil öncelikli, 0-GC steady-state garantili MVCS çatısı

Bu rapor, 13 servisin **tamamını** kapsayan satır satır incelemenin sonucudur. Her servis dört eksende denetlenmiştir:

1. **Hot-path tahsis** — per-frame / per-tetik tahsis var mı? (0-GC ihlali)
2. **Asimptotik karmaşıklık** — O(N²) degrade olabilen döngü var mı?
3. **Concurrency** — lock kapsamları, yarış koşulları, teardown güvenliği
4. **Anti-cheat** — RAM'de düz saklanan değerler, doğrulanabilirlik

---

## 📋 Özet Tablo

| # | Servis | Durum | Hot-path tahsis | O(N²) riski | Anti-cheat | Düzeltme gerekti mi? |
|---|--------|-------|-----------------|-------------|-----------|----------------------|
| 1 | `EncryptedStorageService` | ✅ Temiz | Yok (path cache) | Yok | AES-256 + HMAC + device-bound | Hayır |
| 2 | `ObjectPoolService` | ✅ Temiz | Yok (dictionary) | Yok | — | Hayır |
| 3 | `UIManager` | ✅ Temiz | Yok (dirty-flag cache) | Yok | — | Hayır |
| 4 | `AudioService` | ⚠️ Düzeltildi | **Yok artık** | **Kapatıldı** | — | **Evet** (havuz sınırı) |
| 5 | `HapticService` | ⚠️ Düzeltildi | **Yok artık** | Yok | — | **Evet** (JNI önbellek) |
| 6 | `FeedbackService` | ✅ Temiz | Yok | Yok | — | Hayır |
| 7 | `AdService` | ⚠️ Düzeltildi | Yok | Yok | ✅ XOR-maskeli (cooldown) | **Evet** (cooldown maskeleme) |
| 8 | `IapService` | ✅ Stub / Temiz | Yok | Yok | Kısmi | Hayır (stub) |
| 9 | `EconomyService` | ✅ Temiz | Yok | Yok | ✅ XOR-maskeli | Hayır |
| 10 | `ProgressionService` | ✅ Temiz | Yok | Yok | ✅ XOR-maskeli | Hayır |
| 11 | `TickService` | ✅ Temiz | Yok (dirty-flag) | Yok | — | Hayır |
| 12 | `LocalizationService` | ✅ Temiz | Yok | Yok | — | Hayır |
| 13 | `AnalyticsService` | ✅ Stub / Temiz | Log-time (hot değil) | Yok | — | Hayır |

**Sonuç:** 13 servis denetlendi, **3 gerçek sorun bulundu ve düzeltildi** (AudioService sınırsız havuz, HapticService JNI 0-GC ihlali, AdService düz cooldown float'ları), 10'u temiz veya stub statüsünde.

---

## 1. `EncryptedStorageService` — Storage ✅

**Dosya:** `Runtime/Services/Storage/EncryptedStorageService.cs`

- **Şifreleme:** AES-256 gerçek 32-byte anahtar (P0-6 sonrası), HMAC-SHA256 bütünsellik doğrulaması (tam 32-byte, P0-10 sonrası), device-bound seed (XOR maskeleme).
- **Bütünsellik:** `CompareHashes` sabit zamanlı (constant-time) — timing saldırısı yok.
- **Format:** V2 format: `[VERSION:1][IV:16][HMAC:32][ciphertext:N]`. V1 legacy dosyalar (16-byte trunked HMAC) otomatik migrasyonla V2'ye yükseltilir. Format version byte forward-compatibility sağlar.
- **Legacy:** AES-128 formatı tek seferlik migrasyon ile okunup AES-256'ya (V2) yeniden şifreleniyor.
- **Hot-path tahsis:** `GetFilePath` sonuçları `_filePathCache`'te — MD5.Create() churn'ü önceden kapatılmış (P2-14).
- **Concurrency:** Tek `_lock` tüm cache/disk erişimini koruyor; focus-loss save'i `Task.Run` ile worker thread'de (P2-14) — main thread asla bloklanmıyor.
- **Dayanıklılık:** Atomik temp-file + `File.Move` 3 denemeli retry (Windows handle kilitleri için).
- **Bulgular:** Yok. Hot path değil (disk IO + şifreleme), tahsisleri zaten cache'lenmiş.

## 2. `ObjectPoolService` — Pool ✅

**Dosya:** `Runtime/Services/Pool/ObjectPoolService.cs`

- **Yapı:** Prefab-ID ve instance-ID tabanlı iki dictionary — `Spawn`/`Despawn` O(1).
- **Yarış koruması:** `DespawnAfter` artık `SpawnSessionToken` (generation) ile korunuyor — bekleyen timer, re-spawn edilmiş canlı objeyi yok edemez.
- **Hot-path tahsis:** `Spawn` hot path'i dictionary lookup + `SetPositionAndRotation`, tahsis yok. `GetComponents<IPoolable>()` instance başına bir kez — kabul edilebilir.
- **Concurrency:** Mono-behaviour tabanlı olduğundan main-thread, lock yok (Unity API zaten thread-safe değil).
- **Bulgular:** Yok.

## 3. `UIManager` — UI ✅

**Dosya:** `Runtime/Services/UI/UIManager.cs`

> **2026-08-06:** Eski `WindowManager` (string-keyed legacy API) tamamen kaldırıldı —
> `UIManager` tek UI yöneticisidir. `UILayer` ve `IUIWindowLifecycle` `WindowManager.cs`'ten
> ayrı dosyalara çıkarıldı; benchmark'ın W1–W7 kanıtları U1–U7 (`UIManager`) olarak taşındı.
> NEXUS004 analyzer kuralı emekli edildi. Sahneye bağlı olmayan demo iskelesi
> (`Assets/Scripts/Demo/`) kaldırıldı — kanonik örnek `Game/Samples` (sahneye bağlı, wizard ile üretilir).

- **Kanoniik yapı:** Tip-güvenli `ScreenView` API (`OpenScreenAsync<TScreen>`), havuzlanmış örnekler, `RegisterScreenPrefab`.
- **Katmanlar:** Background → System (7 katman) canvas yığını, `sortingOrder` ile (`UICanvasSystem` paylaşır).
- **Concurrency:** `lock` korumalı; eşzamanlı çift açılış `_pendingOpens` ile reddedilir (tek instantiation).
- **Hot-path tahsis:** `UpdateLayerInteractivity` yalnızca open/close'ta çalışır — per-frame değil; aktif-GameObject görünümü dirty-flag cache'li.
- **Pooling:** Kapatılan ekran deaktive edilip havuzlanır (`MaxPooledPerScreenKey=16`); yeniden açılış aynı örneği yeniden kullanır.
- **Editör introspesiyonu:** `GetOpenScreensSnapshot`/`PendingScreenCount` non-blocking (`Monitor.TryEnter`).
- **Bulgular:** Yok.

## 4. `AudioService` — Audio ⚠️→✅ (DÜZELTİLDİ)

**Dosya:** `Runtime/Services/Audio/AudioService.cs`

- **Bulgular (önceki turda tespit):**
  - **Sınırsız SFX havuzu** — `GetAvailableSfxSource` havuz dolunca her seferinde yeni `GameObject` + interpolasyonlu isim string'i tahsis ediyordu. SFX-yoğun sahnede lineer tarama + create etkin olarak **O(N²)** ve kalıcı bellek büyümesi.
  - **Pitch guard eksik** — `pitchMin > pitchMax` olduğunda `Random.Range` exception fırlatıyordu.
- **Düzeltmeler:**
  - `MaxSfxPoolSize = 32` üst sınırı. Havuz dolunca **en eski kanal çalınıyor** (`_sfxPool[0].Stop()` + reuse) — code-review'ın işaret ettiği "çalan klibin volume/pitch/position'ı bozulur" riski `Stop()` ile kapatıldı.
  - `PlaySfx` pitch swap guard — `FeedbackService.PlayCustom` ile aynalanan koruma.
- **Doğrulama:** En kötü durum O(32), büyüme yok.

## 5. `HapticService` — Haptics ⚠️→✅ (DÜZELTİLDİ)

**Dosya:** `Runtime/Services/Haptics/HapticService.cs`

- **Bulgular (önceki turda tespit):**
  - Android `Vibrate()` hot path'i **her tetiklemede** `createOneShot` çağırıyordu → her haptic'te `AndroidJavaObject` + boxed `long`/`int` tahsisi. Servis kendi dokümantasyonunda "zero-alloc" diyordu — ihlal.
- **Düzeltmeler:**
  - 6 `VibrationEffect` **init'te ön-oluşturulup önbelleklendi** (immutable olduğu için yeniden kullanım geçerli).
  - **Explicit per-member atama** (Light/Medium/Heavy/Warning/Success/Selection) — enum'a ortaya üye eklenirse sessiz kayma olmaz.
  - `Vibrate()` önbellekten çalıyor (bounds + null guard); gelecek enum üyeleri için `createOneShot` fallback'i korundu.
  - `OnDispose` tüm cached effect'leri release ediyor.
  - Pattern tablosu tek kaynağa (`GetHapticPattern`) çekildi — pre-26 fallback ile çift bakım sona erdi.
- **Süreç notu:** İlk edit'te str_replace bir orphan kopya blok üretti (brace -2) — doğrulama adımında yakalanıp temizlendi.

## 6. `FeedbackService` — Feedback ✅

**Dosya:** `Runtime/Services/Feedback/FeedbackService.cs`

- **Yapı:** Preset → HapticType + opsiyonel AudioClip eşlemesi. Dictionary lookup + switch, tahsis yok.
- **Pitch guard:** `PlayCustom`'da `pitchMin > pitchMax` takası zaten mevcut.
- **Bulgular:** Yok.

## 7. `AdService` — Ads ⚠️→✅ (DÜZELTİLDİ)

**Dosya:** `Runtime/Services/Ads/AdService.cs`

- **Durum:** `[StubService]` — gerçek adapter (AdMob/IronSource) release öncesi bağlanmalı.
- **Cooldown mantığı:** Interstitial `Time.realtimeSinceStartup - _lastInterstitialTime.Value < _interstitialCooldownSeconds.Value` ile korunuyor.
- **Bulgular (bu turda tespit):** `_interstitialCooldownSeconds` ve `_lastInterstitialTime` **düz float** — RAM taramasıyla sıfırlanabilir (cooldown bypass + interstitial spam). Ekonomik etki sınırlı (revenue hâlâ ad network'te doğrulanıyor) ama maskeleme maliyeti yok denecek kadar az.
- **Düzeltmeler:**
  - Yeni `SecureObservableFloat` (XOR-maskeli RAM obfuscation, `SecureObservableInt`/`Long` desenine ayna — IEEE-754 bit deseni XOR ile maskelenir, union struct ile tahsissiz bit dönüşümü).
  - Her iki alan `SecureObservableFloat`'a geçirildi; `OnDispose` `ClearOnChanged` ile temizliyor.
- **Doğrulama:** 3 `SecureObservableFloat` testi (maskeli round-trip, OnChanged, SetWithoutNotify, negatif/sıfır bit koruması) + 1 AdService cooldown entegrasyon testi (ilk gösterim açık → gösterim sonrası cooldown kapalı → sıfır cooldown ile açık).

## 8. `IapService` — IAP ✅ (Stub)

**Dosya:** `Runtime/Services/IAP/IapService.cs`

- **Durum:** `[StubService]` — gerçek store adapter (Unity IAP / RevenueCat) release öncesi bağlanmalı.
- **Graceful fallback (P0.2):** Adapter yokken release build'de **asla fırlatmaz** — `onComplete(false, "store_unavailable")` ile geri çağırır; caller toast + retry kuyruğu gösterebilir. Editör/Development'ta simüle başarı.
- **Hot-path:** `RegisterProducts`/`Purchase` lock korumalı; per-frame tahsis yok.
- **Bulgular:** Yok (stub statüsünde).

## 9. `EconomyService` — Economy ✅

**Dosya:** `Runtime/Services/Economy/EconomyService.cs`

- **Anti-cheat:** `SecureObservableLong` (XOR-maskeli RAM obfuscation) — para değerleri RAM'de düz `long` değil. GameGuardian/CheatEngine taramalarına karşı.
- **Taşma koruması:** `Earn` `long.MaxValue`'da clamp — negatife sarma yok.
- **Server reconciliation:** `Spend` optimistik yerel commit + `ReconcileSpendAsync` — server reddederse geri iade (`Math.Min(prop.Value + amount, long.MaxValue)`). Ağ hatasında iade edilmez ama client/server desync kapanır (server authoritative ledger).
- **Concurrency:** `lock (_balances)` tüm mutasyonları korur; fire-and-forget task'ler try/catch ile — unobserved exception yok.
- **Hot-path:** Lock korumalı dictionary + XOR-maskeli property — tahsis yok.
- **Bulgular:** Yok.

## 10. `ProgressionService` — Progression ✅

**Dosya:** `Runtime/Services/Progression/ProgressionService.cs`

- **Anti-cheat:** `SecureObservableInt` — seviye verisi RAM'de düz `int` değil.
- **Taşma/NaN koruması:** `CalculateUpgradeCost` NaN/Infinity/overflow'da `long.MaxValue`'ya clamp (eski unchecked double→long cast extreme seviyelerde `long.MinValue`'ya sarıyordu); Linear curve `multiplier < 1`'de negatife dönemez (base cost tabanı).
- **Persist:** `OnChanged` → PlayerPrefs yazımı; `Dispose` `ClearOnChanged`.
- **Bulgular:** Yok.

## 11. `TickService` — Tick ✅

**Dosya:** `Runtime/Services/Tick/TickService.cs`

- **0-GC snapshot:** Kayıtlar dirty-flag ile **tek paslı** `ToArray()` snapshot rebuild'i — spawn storm'unda N kayıt = 1 tahsis. Unregister anında (destroyed tickable bir daha tick almaz, kaldırma tahsissiz).
- **Profiler:** Unconditional static `ProfilerMarker`'lar (`Nexus.TickService.Update/FixedUpdate/LateUpdate`) — prodüksiyonda no-op, `#if` gerekmez.
- **Dayanıklılık:** Per-tickable try/catch — bir tickable hata fırlatırsa zincir ölmez.
- **Concurrency:** `lock (_lock)` snapshot okuma/yazmayı korur; driver `DontDestroyOnLoad` GameObject.
- **Bulgular:** Yok.

## 12. `LocalizationService` — Localization ✅

**Dosya:** `Runtime/Services/Localization/LocalizationService.cs`

- **RTL doğruluğu:** `FormatRTLIfNeeded` grapheme cluster (grapheme-aware) ters çevirme — `StringInfo.ParseCombiningCharacters` ile emoji/surrogate pair ve combining mark koruması (Array.Reverse'in parçaladığı UTF-16 birimleri değil).
- **Fallback:** Dil yoksa `en` tablosuna düşer; `OnLanguageChanged` event'i.
- **Concurrency:** `_tableLock` tüm tablo erişimini korur.
- **Hot-path:** `GetString` dictionary lookup + koşullu RTL reversal (yalnızca RTL dilde). RTL reversal'da tahsis var ama string döndürmek zaten tahsis — kaçınılmaz, kabul edilebilir.
- **Bulgular:** Yok.

## 13. `AnalyticsService` — Analytics ✅ (Stub)

**Dosya:** `Runtime/Services/Analytics/AnalyticsService.cs`

- **Durum:** `[StubService]` — Firebase Analytics / Amplitude release öncesi bağlanmalı.
- **Hot-path:** `LogEvent(eventName)` log-only, tahsis yok. Parametreli overload `List<string>` + `string.Join` tahsis ediyor ama bu analitik olayı — per-frame hot path değil. YAGNI: optimize edilmedi.
- **Bulgular:** Yok (stub statüsünde).

---

## 🔍 Kalan Öneriler (öncelik sırasıyla)

| Öncelik | Öneri | Gerekçe |
|---------|-------|---------|
| Düşük | `IapService._mockOwnedProducts` bütünsellik doğrulaması | Yalnızca editor/dev mock — release'de bypass edilir; gerçek koruma store adapter'da |
| Not | `AnalyticsService` log parametre tahsisi | Per-frame değil; gerçek SDK entegrasyonunda zaten ağ paketi tahsis edilecek |
| Not | `ObjectPoolService.GetComponents<IPoolable>()` | Instance başına bir kez; 0-GC hot path hedefi değil |

## ✅ Doğrulama Yöntemi

- Her servis kaynak kodda satır satır okundu (4 eksen: tahsis / O(N²) / concurrency / anti-cheat).
- Düzeltilen 3 servis için: brace dengesi, `#if/#endif` eşleşmesi, code-reviewer incelemesi.
- `AdService` + `SecureObservableFloat` düzeltmeleri EditMode testleriyle doğrulanabilir (mevcut anti-cheat test suite'ine eklendi). Audio/Haptic düzeltmeleri platform bağımlı (`#if UNITY_ANDROID`) ve Unity audio sistemine dayalı olduğundan EditMode testleri yazılamadı — doğrulama Unity'de Play Mode + Android build ile yapılmalı.
