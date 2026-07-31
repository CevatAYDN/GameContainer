# Nexus Core — Rapor Maddesi Doğrulama ve Çözüm Takip Dokümanı

**Proje:** GameContainer / `com.nexus.core`
**Tarih:** 31 Temmuz 2026
**Amaç:** 31 Temmuz 2026 tarihli "Detaylı Kod İnceleme ve Risk Denetim Raporu"ndaki 4 aksiyon maddesinin mevcut koda karşı doğrulanması, çözüm durumu ve kanıtları.

Bu doküman, rapor maddelerinin her birinin **gerçek koda karşı** nasıl doğrulandığını ve hangi turda çözüldüğünü takip eder. Her madde için: durum, kanıt (dosya:satır), ilgili testler ve (varsa) uygulanan düzeltme.

---

## 📋 Özet Tablo

| # | Rapor Maddesi | Rapor Önceliği | Durum | Çözüm Turu | Kanıt |
|---|---|---|---|---|---|
| 1 | `NetworkSignalBus` O(N²) Prune | HIGH | ✅ Zaten çözülmüş | Önceki tur | `Prune`/`RemoveSignalsAfter` → O(N) 0-alloc in-place compaction |
| 2 | `Root.async void` main-thread ihlali | HIGH | ✅ Zaten çözülmüş | Önceki tur | `_mainThreadId` guard + `ThreadStateException` + deterministik dispose |
| 3 | `ViewBinder` mediator reset hijyeni | MEDIUM | ✅ Bu turda çözüldü | Bu tur | `GetMediator` pop'ta `(mediator as IResettable)?.Reset()` |
| 4 | Retry asenkron timeout eksikliği | MEDIUM | ✅ Zaten mevcut | (zaten vardı) | `[CommandTimeout]` + linked CTS + `FireAsyncWithTimeout` |

---

## 1. `NetworkSignalBus` — O(N²) Prune (Rapor: HIGH)

**Rapor iddiası:** `Prune(int tick)` ters döngüde `_signals.RemoveAt(i)` ile tek tek siliyor → O(K×N). `RemoveAll` veya iki indeksli kaydırma öneriliyor.

**Doğrulama sonucu:** Rapor **eski kodu** tarıyordu. Mevcut kod (önceki turda düzeltildi):

- `Runtime/Netcode/NetworkSignalBus.cs` — `Prune(int confirmedTick)` ve `RemoveSignalsAfter(int tick)` artık **tek geçişli in-place compaction**: `write` indeksi hayatta kalanları öne kopyalar, `RemoveRange(write, Count - write)` kuyruğu kırpar. O(N), **sıfır tahsis** (predicate yok, struct kopyalama değer bazlı).
- Raporun önerdiği `RemoveAll(predicate)` tahsis ederdi (delegate + O(N) predicate çağrısı) — 0-GC garantisini bozardı. Manuel compaction daha iyi.
- `Prune` → `Tick > confirmedTick` tutar; `RemoveSignalsAfter` → `Tick <= tick` tutar (rollback semantiğiyle birebir).

**Testler:** `Tests/Runtime/NetcodeTests.cs`
- `Prune_PreservesRelativeOrderOfSurvivors`
- `Prune_PrunesEverythingWhenAllOlderThanConfirmedTick`
- `RemoveSignalsAfter_KeepsOnlyAtOrBeforeTick`
- `Prune_SteadyState_ZeroAllocations` (500 döngü × 1000 sinyal, ≤512 byte)

---

## 2. `Root.async void Start()` — Main-Thread İhlali (Rapor: HIGH)

**Rapor iddiası:** `async void Start()` içinde `await Context.InitializeLifecycleAsync(...)` sonrası kullanıcı kodu `ConfigureAwait(false)` kullanırsa continuation worker thread'de sürer; `catch`'teki `Context.Dispose()`/`Destroy` Unity API'sini main thread dışında çağırıp çökertebilir.

**Doğrulama sonucu:** Rapor **eski kodu** tarıyordu. Mevcut kod (önceki turda düzeltildi):

- `Runtime/Core/Root.cs` — `Awake`'te `_mainThreadId = Thread.CurrentThread.ManagedThreadId` yakalanır.
- `await Context.InitializeLifecycleAsync(...)` sonrası `Thread.CurrentThread.ManagedThreadId != _mainThreadId` ise net mesajlı `ThreadStateException` fırlatılır → mevcut `catch` context'i deterministik şekilde dispose eder ve `IsInitialized = false` bırakır. Unity API asla worker thread'de çağrılmaz.
- Normal play-mode'da asla tetiklenmez (ID eşleşir) — tamamen savunmacı koruma.
- Çift loglama yok: guard kendi `LogError`'ını çağırmaz, `ThreadStateException.Message` kök neden ipucunu taşır, tek log kaynağı `catch`'tir.

**Testler:** Kod içi guard (editör ortamında tetiklenmesi Play Mode'a bağlı; batch/headless test ortamında `async void Start()` çağrılmaz). Doğrulama Unity Play Mode'da yapılmalı.

---

## 3. `ViewBinder` — Mediator Reset Hijyeni (Rapor: MEDIUM) ✅ BU TURDA ÇÖZÜLDÜ

**Rapor iddiası:** `GetMediator` havuzdan pop ederken yalnızca `_container.Inject(mediator)` çağırıyor; `Reset()` yalnızca `ReturnMediator → ClearInjectedReferences` yolunda çalışıyor → havuzdan alınan mediator'ın private state'i temiz değil.

**Doğrulama sonucu:** **Gerçek boşluktu.** Doğrulandı:
- `ViewBinder.GetMediator` (pop) sadece `Inject` çağırıyordu; `NexusDI.Clearer.ClearInjectedReferences` (return) `IResettable.Reset()` çağırıyordu — tek yönlü hijyen.

**Düzeltme:** `Runtime/Lifecycle/ViewBinder.cs` — `GetMediator` artık pop'ta `(mediator as IResettable)?.Reset()` çağırıyor (Inject'ten ÖNCE):
```
Reset → Inject → Bind  (sıralama güvenli)
```
- `ClearInjectedReferences`'ın dokunmadığı `[Inject]` dışı private state'i de kapsar.
- Taze mediator'lar (Resolve yolu) resetlenmez — constructor durumu temiz.

**Derinleştirme (aynı turda):** `Runtime/Lifecycle/Mediator.cs` — `Mediator<TView>` artık **`IResettable` uyguluyor** + `public void Reset()` + `protected virtual void OnReset()` hook'u. Böylece havuz hijyeni **tüm mediator'lar için zorunlu** — `IResettable` isteğe bağlı olmaktan çıktı. `Reset()` kalıcı subscription'ları dispose eder, `View`/`SignalBus`'ı null'lar, `OnReset()`'i çağırır (idempotent).

**Testler:**
- `Tests/Runtime/ViewBindingTests.cs` — `GetMediator_ResetsPooledMediatorOnReuse` (taze 0 → return 1 → pop 2, aynı instance) ve `MediatorBase_ResetCalledOnPoolReturnAndPop` (taban sınıf `IResettable` kapsamı, `OnReset` hook'u).

---

## 4. Retry Asenkron Timeout (Rapor: MEDIUM)

**Rapor iddiası:** `ExecuteWithRetryAsync`'te bağımsız timeout yoksa askıda kalan komut tüm sinyal hattını bloke edebilir; `[CommandTimeout]` + linked CTS öneriliyor.

**Doğrulama sonucu:** Bu mekanizma **zaten mevcut** (rapor dosya adında bile hatalı — gerçek dosya `Recovery.cs` değil, retry yürütme `SignalBus.cs`'te):

- `Runtime/Attributes/Attributes.cs` — `CommandTimeoutAttribute` tanımlı.
- `Runtime/Core/SignalBus.cs` `RegisterCommand` — `[CommandTimeout]` okunur, `CommandHandlerInfo.TimeoutMs`'e yazılır (P0-5 fix).
- `ExecuteCommandAsync` — `handler.TimeoutMs > 0` ise `CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(handler.TimeoutMs)` ile komuta iptal edilebilir token verilir.
- `FireAsyncWithTimeout` — çağıran taraf için ekstra timeout katmanı.
- Zaman aşımında `OperationCanceledException` yeniden fırlatılır (P1-3: `ExceptionDispatchInfo` ile stack korunur) — **retry döngüsüne girmez**, hattı bloke etmez.

**Testler (bu turda eklendi):** `Tests/Runtime/RecoveryTests.cs`
- `CommandTimeout_CancelsHangingCommand_DoesNotBlockRetryLoop` — `[CommandTimeout(50)]` askıda kalan async komut: `OperationCanceledException` <5 sn içinde fırlatılır, `ThrowCount == 1` (timeout'tan sonra retry YOK), retry stratejisi `Retry(10)` olsa bile sonsuz döngü olmaz.

---

## 🔬 Bu Turda Eklenen İyileştirmeler (rapor dışı, takip dokümanının parçası)

### 4a. `IapService` mock bütünselliği — `_mockOwnedProducts` checksum
- `Runtime/Services/IAP/IapService.cs` — `_mockOwnedProducts` HashSet'i artık checksum ile korunuyor. İlk turda deterministik session-stabil char-hash kullanıldı; sonraki turda **salted + dönen XOR mask** ile sertleştirildi: per-instance `_mockOwnedSalt`, her başarılı doğrulamada dönen (`*31+17`) `_mockOwnedMask`, saklanan değer `hash ^ mask`. Böylece instance'lar arası değer taraması ve gözlemlenen (checksum, mask) snapshot'ının replaysı bir sonraki okumada yakalanır.
- `PurchaseProduct` her eklemeden ÖNCE doğrular (tampered set'i meşrulaştırmaz); `IsProductOwned` okumadan önce `VerifyMockOwnedChecksum()` doğrular — RAM taramasıyla enjekte edilen sahte ürün checksum'ı bozar → set temizlenir, sahte sahiplik reddedilir (fail-closed; tüm set — meşru ürünler dahil — temizlenir, tasarım gereği).
- **Testler:** `Tests/Editor/EncryptedStorageAndAntiCheatTests.cs` — `IapService_MockOwnedIntegrity_TamperDetectedAndSetCleared`, `IapService_MockOwned_NormalFlowUnaffected`, `IapService_MockOwned_PurchasePathRejectsTamperedSet`, `IapService_MockOwned_ReadBeforeAnyPurchaseIsStable`, `IapService_MockOwned_ChecksumRotatesAcrossReads`, `IapService_MockOwned_SnapshotReplayOfChecksumIsDetected`.

### 4b. `SecureObservableFloat` + `AdService` cooldown maskeleme
- `Runtime/Models/SecureObservableProperty.cs` — `SecureObservableFloat` (XOR-maskeli IEEE-754 bit deseni, union struct ile tahsissiz dönüşüm).
- `Runtime/Services/Ads/AdService.cs` — `_interstitialCooldownSeconds` / `_lastInterstitialTime` artık XOR-maskeli; `OnDispose` `ClearOnChanged`.
- **Testler:** `Tests/Editor/EncryptedStorageAndAntiCheatTests.cs` — 3 `SecureObservableFloat` + 1 AdService cooldown entegrasyon testi.

---

## 📌 Sonuç

| Rapor Maddesi | Durum | Kanıt |
|---|---|---|
| 1. O(N²) Prune | ✅ Zaten çözülmüş | compaction + 4 test |
| 2. Main-thread ihlali | ✅ Zaten çözülmüş | thread guard + deterministik dispose |
| 3. Mediator reset hijyeni | ✅ **Bu turda çözüldü** | GetMediator Reset + Mediator<TView> IResettable + 2 test |
| 4. Retry timeout | ✅ Zaten mevcut | `[CommandTimeout]` + linked CTS + 1 yeni test |

Raporun 4 maddesinden 3'ü önceki turlarda çözülmüştü; gerçek boşluk olan 1 madde (ViewBinder reset hijyeni) bu turda hem düzeltildi hem de taban sınıf seviyesinde zorunlu kılındı ve testlerle koruma altına alındı. Ek olarak rapor dışı 2 anti-cheat iyileştirmesi (IapService checksum, AdService cooldown maskeleme) tamamlandı.
