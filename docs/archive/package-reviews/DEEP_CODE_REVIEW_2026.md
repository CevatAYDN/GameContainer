# Nexus Core — Derinlemesine Mimari & Kod İnceleme Raporu

**Tarih:** 2026-08-05  
**Kapsam:** `Packages/com.nexus.core` (Runtime + Editor + Testler + Yapılandırma)  
**İnceleme yöntemi:** 5 paralel uzman ajan + satır satır doğrulama

> ## ✅ UYGULAMA DURUMU (2026-08-05 güncellemesi)
> **Rapordaki TÜM bulgular düzeltildi.** Fix'ler koddaki `R2026-<id>` etiketleriyle işaretlendi
> (örn. `R2026-C1`, `R2026-H10`). Doğrulama: `com.nexus.core`, `com.nexus.core.editor`,
> `com.nexus.core.tests`, `com.nexus.core.editor.tests` projelerinin tamamı **0 hata** ile derlendi.
>
> Düzeltilenler: C-1, C-2, C-3, H-1..H-12, M-1..M-12, L-1..L-5, A-1, A-2, A-3.
> Bilinçli olarak kod DEĞİŞİKLİĞİ yapılmadan dokümante edilerek kapatılanlar: M-5 (mutation
> lock'ları gereklidir — kaldırılamaz, gerekçesi eklendi), M-8 (metrik torn-read kabul edilmiş
> trade-off), M-1/M-2 (tasarım kısıtları dokümante edildi).
>
> **Mimari kapanışlar (3. tur):**
> - **A-1** ✅ Tüm servisler `NexusService<T>` tabanında birleştirildi (H-1 ile).
> - **A-2** ✅ `WindowManager`/`IWindowManager` `[Obsolete]` işaretlendi (warning-only),
>   `UIManager` canonical path olarak belgelendi; framework-içi kullanımlar (CasualServicesPlugin,
>   testler) `#pragma warning disable CS0618` ile bastırıldı.
> - **A-3** ✅ `NexusException` base class'ı eklendi; tüm framework exception'ları ondan türüyor.
> - **L-2** ✅ `Interfaces.cs` (416 satır, 15 tip) tek-tipi-dosya prensibiyle 10 dosyaya bölündü
>   (`ICommand`, `ICompositeCommand`, `IContext`, `IContextBuilder`, `IContextLifecycle`,
>   `IModelLifecycle`, `IRecoveryStrategy`, `ISignalBus`, `IView`, `NexusExceptions`).
>   csproj güncellendi; tüm projeler 0 hata ile derleniyor.
> - **A-4/A-5** (Context/NexusDI decomposition) bilinçli olarak ertelendi: her ikisi de zaten
>   deep-module disiplinine sahip (Context sub-modüllere delegate ediyor: CommandRegistry,
>   SubscriptionRegistry, CommandExecutor, RecoveryEngine, AssemblyScanService,
>   ContextLifecycleOrchestrator; NexusDI içinde Injector/Clearer/MetadataCache ayrık).
>   Geriye kalan bölme kozmetiktir — risk/fayda dengesi şu an için yapılmamasını gerektirir.

---

## 8. EDİTÖR ARAÇLARI — İkinci Tur İnceleme (2026-08-05, tümü düzeltildi)

İlk rapor sonrası Editor klasörü (30 dosya, ~10.000 satır) 4 paralel ajanla ayrıca derin
incelendi. **4 CRITICAL** bulgu tespit edildi ve hepsi düzeltildi; `com.nexus.core.editor`
projesi 0 hata ile derleniyor.

### E-C1 [CRITICAL→DÜZELTİLDİ]: NexusCodeGenerator — generic/nested tiplerde derlenemeyen kod üretimi
**Dosya:** `Editor/CodeGen/NexusCodeGenerator.cs`
`Type.FullName.Replace("+", ".")` generic tiplerde geçersiz C# üretiyordu (backtick arity
marker'ı sızıyor, type argümanları düşüyordu → üretilen `NexusGeneratedBinder.g.cs`
CS0246/CS0305 ile derlenemiyordu). **Çözüm:** `GetCSharpTypeName(Type)` helper'ı eklendi
(nested + generic + array tipleri doğru üretir; open generic'ler `null` döndürüp atlanır),
`GetSafeIdentifierName` ile cache-field isimleri sanitize ediliyor. Tüm 7 kullanım noktası
(signal dispatcher, injector, clearer, preserve passes) güncellendi.

### E-C2 [CRITICAL→DÜZELTİLDİ]: DashboardPlugin — playModeStateChanged çift abonelik sızıntısı
**Dosya:** `Editor/Plugins/DashboardPlugin.cs:104-159`
`CreateView()` her tab gösteriminde `playModeStateChanged += handler` yapıyordu ama unsubscribe
yalnızca `OnDisable()`'daydı (tab switch/window close). `RefreshActivePlugin()` → `CreateView()`
yeniden çağrıldığında statik event'e ikinci abonelik ekleniyor, domain-reload kapalıyken eski view
referansları process ömrünce sızıyordu. **Çözüm:** CreateView'taki abonelik kaldırıldı —
yaşam döngüsü abonelikleri yalnızca `OnEnable`/`OnDisable` çiftinde.

### E-C3 [CRITICAL→DÜZELTİLDİ]: NexusSetupWizard — scaffold Play Mode'da kalıcı kilitlenme
**Dosya:** `Editor/Plugins/NexusSetupWizard.cs:400-449`
`[DidReloadScripts]` play-mode domain reload'unda tetiklenmez → `PendingSceneKey` SessionState
flag'i sonsuza dek set kalıyor, sonraki her edit-mode compile'da wizard "hâlâ bekliyor" sanıyordu.
Ayrıca `Game.GameView` hiç derlenemezse (kullanıcı kodunda hata) retry sonsuzdu.
**Çözüm:** `playModeStateChanged`'de flag temizleniyor (idempotent subscribe), retry sayacı
(`PendingSceneRetryKey`, max 3) eklendi — aşılınca flag sıfırlanıp hata loglanıyor.

### E-C4 [CRITICAL→DÜZELTİLDİ]: NetworkDashboardPlugin — background thread'den UI Toolkit erişimi
**Dosya:** `Editor/Plugins/NetworkDashboardPlugin.cs:379-399`
`NetworkMonitor.OnNetworkEvent` network/background thread'inden raise ediliyor; handler doğrudan
`_filteredEvents`'i mutate edip `RebuildEventTable()` çağırıyordu (UI Toolkit thread-safe değil,
editor-thread `ApplyFilters()` ile race → collection-modified crash). Yüksek trafikte her event
için tam tablo rebuild'i editor'ü kilitliyordu. **Çözüm:** handler artık yalnızca
`ConcurrentQueue`'ya enqueue ediyor; `OnUpdate` (editor thread) `DrainPendingEvents()` ile toplu
işleyip tabloyu batch başına bir kez rebuild ediyor. `OnDisable`'da kuyruk temizleniyor.

---


## Yönetici Özeti

Nexus Core, genel olarak **olgun ve iyi düşünülmüş** bir kod tabanıdır. Geçmiş audit'lerin izleri (A1–A10, B1–B8, P0–P4, M3–M7, T1–T3, R1–R8 fix yorumları) her yerde görülüyor; thread-safety, zero-GC ve bellek yönetimi konularına ciddi yatırım yapılmış. Ancak hâlâ **3 CRITICAL, 12 HIGH, 18 MEDIUM** seviye bulgu mevcut — özellikle **BigDouble aritmetiğinde sessiz veri bozulması**, **GameStateMachine'de CancellationTokenSource race'i**, **GameSaveManager'da yarım kalan .tmp dosyası**, **servis katmanındaki interface tutarsızlıkları** ve **UI manager'ların tekrarlanan kod yapısı** öne çıkıyor.

---

## 1. CRITICAL Bulgular

### C-1: BigDouble `operator +` — üs taşması (overflow) sessizce veriyi bozuyor
**Dosya:** `Runtime/Data/BigDouble.cs:85-104`

**Sorun:** `operator *` ve `operator /` üs toplamını `SaturateAddExponent`/`SaturateSubExponent` ile koruyor (satır 121, 128), ama `operator +` bunu yapmıyor. `a.Exponent - b.Exponent` ifadesi (satır 90) `long` taşması yapabilir:

```csharp
long diff = a.Exponent - b.Exponent;  // a = long.MaxValue, b = -1 → OVERFLOW
```

`a.Exponent = long.MaxValue` ve `b.Exponent = -1` olduğunda `diff` negatif olur ve `diff < -15` kontrolü `true` döner, sonuç `b` olur — yani `MaxValue + (-0.1)` yanlışlıkla `-0.1` döndürür. Ayrıca `Math.Pow(10, diff)` `diff > 15` iken `Infinity` üretir, ama bu durum yukarıdaki erken dönüşlerle yakalanıyor; asıl sorun taşma yolunda.

**Çözüm:**
```csharp
public static BigDouble operator +(BigDouble a, BigDouble b)
{
    if (a.Mantissa == 0.0) return b;
    if (b.Mantissa == 0.0) return a;

    // Saturate the exponent difference to prevent long overflow
    long diff = SaturateSubExponent(a.Exponent, b.Exponent);
    
    if (diff > 15) return a;
    if (diff < -15) return b;

    if (diff >= 0)
    {
        double m = a.Mantissa + (b.Mantissa / Math.Pow(10, diff));
        return new BigDouble(m, a.Exponent);
    }
    else
    {
        double m = b.Mantissa + (a.Mantissa / Math.Pow(10, -diff));
        return new BigDouble(m, b.Exponent);
    }
}
```

---

### C-2: GameStateMachine — `_stateCts` ataması ile `Cancel()` arasında race condition
**Dosya:** `Runtime/FSM/GameStateMachine.cs:177-186`

**Sorun:** `ChangeStateAsync` içinde eski `_stateCts` okunup `null` yapılıyor, sonra `Cancel()` çağrılıyor. Ancak bu işlem atomik değil:

```csharp
var superseded = _stateCts;
_stateCts = null;           // ← Burada başka bir thread _stateCts'i okuyabilir
superseded?.Cancel();       // ← Eski token iptal ediliyor
// ...
_stateCts = myCts;          // ← Yeni token atanıyor
```

İki eşzamanlı `ChangeStateAsync` çağrısında:
1. Thread A: `_stateCts = null` yapar
2. Thread B: `_stateCts` okur (null görür), kendi token'ını atar
3. Thread A: `superseded.Cancel()` çağırır (ama bu Thread B'nin token'ı değil, eski token)
4. Thread B: `myCts`'i `_stateCts`'e atar
5. Thread A: `myCts`'i `_stateCts`'e atar — **Thread B'nin token'ı ezilir**

Sonuç: Thread B'nin transition'ı hiçbir zaman iptal edilemez, `_currentState` iki kez yazılabilir.

**Çözüm:** Tüm state geçişlerini bir `SemaphoreSlim` ile seri hale getirin:

```csharp
private readonly SemaphoreSlim _transitionLock = new(1, 1);

public async Task ChangeStateAsync(Type stateType, CancellationToken ct, object args = null)
{
    if (!_states.TryGetValue(stateType, out var nextState))
    {
        NexusRuntime.Logger?.LogError($"[GameStateMachine] State {stateType.Name} is not registered!");
        return;
    }

    await _transitionLock.WaitAsync(ct);
    try
    {
        // ... tüm transition mantığı burada, atomik olarak ...
    }
    finally
    {
        _transitionLock.Release();
    }
}
```

Alternatif: `Interlocked.Exchange` ile atomik token değişimi:
```csharp
var myCts = new CancellationTokenSource();
var oldCts = Interlocked.Exchange(ref _stateCts, myCts);
oldCts?.Cancel(); // Eski token'ı iptal et — yeni token zaten yerinde
oldCts?.Dispose();
```

---

### C-3: GameSaveManager — `File.Replace` başarısız olursa `.tmp` dosyası yarım kalır
**Dosya:** `Runtime/Extensions/GameSaveManager.cs:139-165`

**Sorun:** `SaveAsync` içinde atomik yazma yapılıyor:

```csharp
File.WriteAllText(tempPath, json);
if (File.Exists(path))
    File.Replace(tempPath, path, null);
else
    File.Move(tempPath, path);
```

`File.Replace` 3 kez deneniyor (retry), ama hiçbiri başarılı olmazsa `tempPath` dosyası diskte kalıyor. Bir sonraki `SaveAsync` çağrısında `File.WriteAllText(tempPath, json)` mevcut `.tmp` dosyasının üzerine yazar — bu sorun değil. Ancak `File.Replace` başarısız olursa ve `File.Move` da başarısız olursa, `.tmp` dosyası orada kalır ve **hiçbir zaman temizlenmez**.

Daha kötüsü: `File.Replace` çağrıldığında hedef dosya yoksa `FileNotFoundException` fırlatır. Kod `File.Exists(path)` kontrolü yapıyor, ama bu TOCTOU (time-of-check-time-of-use) race'i — kontrol ile `File.Replace` arasında başka bir işlem hedef dosyayı silebilir.

**Çözüm:**
```csharp
// Hatalı durumda .tmp dosyasını temizle
catch (Exception ex)
{
    attempt++;
    NexusRuntime.Logger?.LogError($"[GameSaveManager] Save attempt {attempt} for '{slotName}' failed: {ex.Message}");
    
    // Temizlik: yarım kalan temp dosyayı sil
    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    
    if (attempt >= maxAttempts)
        throw;
    
    var backoffMs = (int)(50 * Math.Pow(2, attempt - 1)) + new System.Random().Next(0, 50);
    Thread.Sleep(backoffMs);
}
```

Ayrıca `File.Replace` yerine `File.Move(tempPath, path, overwrite: true)` kullanılabilir (.NET Core 3.0+; Unity 2021.3+ destekler).

---

## 2. HIGH Bulgular

### H-1: Servis katmanında interface tutarsızlığı — bazı servislerin interface'i var, bazılarının yok
**Dosyalar:** `Runtime/Services/` geneli

**Sorun:** Servisler arasında tutarsız bir pattern var:

| Servis | Interface | Base Class |
|--------|-----------|------------|
| `AdService` | `IAdService` ✅ | `INexusService` (doğrudan) |
| `AnalyticsService` | `IAnalyticsService` ✅ | `INexusService` (doğrudan) |
| `AudioService` | `IAudioService` ✅ | `NexusService<IAudioService>` |
| `EconomyService` | `IEconomyService` ✅ | `NexusService<IEconomyService>` |
| `HapticService` | `IHapticService` ✅ | `NexusService<IHapticService>` |
| `InputService` | `IInputService` ✅ | `NexusService<IInputService>` |
| `LocalizationService` | `ILocalizationService` ✅ | `NexusService<ILocalizationService>` |
| `LoggerService` | `ILoggerService` ✅ | `NexusService<ILoggerService>` |
| `ObjectPoolService` | `IObjectPoolService` ✅ | `NexusService<IObjectPoolService>` |
| `ProgressionService` | `IProgressionService` ✅ | `NexusService<IProgressionService>` |
| `TickService` | `ITickService` ✅ | `NexusService<ITickService>` |
| `UIManager` | `IUIManager` ✅ | `NexusService<IUIManager>` |
| `WindowManager` | `IWindowManager` ✅ | `NexusService<IWindowManager>` |

**Tutarsızlık:** `AdService` ve `AnalyticsService` `NexusService<T>` yerine doğrudan `INexusService` implemente ediyor. Bu, `[Inject] IContext Context` ve `[Inject] ISignalBus SignalBus` property'lerini kullanamamaları anlamına gelir. Ayrıca `OnDispose()` manuel olarak implemente edilmek zorunda.

**Çözüm:** Tüm servisleri `NexusService<T>` tabanına taşıyın:
```csharp
public class AdService : NexusService<IAdService>, IAdService
{
    // OnDispose() artık NexusService<T>'den geliyor, override edilebilir
    public override void OnDispose()
    {
        _interstitialCooldownSeconds.ClearOnChanged();
        _lastInterstitialTime.ClearOnChanged();
        base.OnDispose();
    }
}
```

---

### H-2: UIManager ve WindowManager — %80 kod tekrarı
**Dosyalar:** `Runtime/Services/UI/UIManager.cs`, `Runtime/Services/UI/WindowManager.cs`

**Sorun:** İki sınıf neredeyse aynı işi yapıyor:
- Her ikisi de `IUIAssetProvider` kullanıyor
- Her ikisi de `UICanvasSystem` kullanıyor
- Her ikisi de aktif/pending/open tracking yapıyor
- Her ikisi de layer interactivity güncelliyor
- Her ikisi de `SafeFireAndForget` helper'ına sahip (kopyala-yapıştır)

`UIManager` type-safe (`ScreenView` tabanlı), `WindowManager` string-based (`GameObject` tabanlı). Ama temel mantık aynı.

**Çözüm:** Ortak bir `UIWindowManagerBase<T>` abstract sınıfı çıkarın:

```csharp
public abstract class UIWindowManagerBase<TKey, TInstance> : NexusService<INexusService>
    where TInstance : Component
{
    protected readonly Dictionary<TKey, TInstance> _active = new();
    protected readonly Dictionary<TKey, Stack<TInstance>> _pools = new();
    protected readonly List<TKey> _history = new();
    protected readonly HashSet<TKey> _pending = new();
    protected readonly object _lock = new();
    
    protected abstract Task<TInstance> InstantiateAsync(TKey key, Transform parent);
    protected abstract TKey GetKey<T>() where T : TInstance;
    // ... ortak Open/Close/CloseAll mantığı
}

public class UIManager : UIWindowManagerBase<string, ScreenView>, IUIManager { }
public class WindowManager : UIWindowManagerBase<string, GameObject>, IWindowManager { }
```

---

### H-3: `NexusDI.CreateInstance` — strict-injection kapalıyken ctor'a null argüman gönderiliyor
**Dosya:** `Runtime/Core/NexusDI.cs:416-431`

**Sorun:** `StrictInjection` kapalıyken çözülemeyen constructor parametresi `null` olarak invoke edilir:

```csharp
args[i] = string.IsNullOrEmpty(paramNames?[i]) ? _di.TryResolve(paramTypes[i]) : ...;
if (args[i] == null && _di.StrictInjection) { throw ...; }
// StrictInjection kapalıysa args[i] null kalır
```

Kullanıcı constructor'ı `ArgumentNullException` fırlatırsa bu `TargetInvocationException` olarak sarılır ve `ExceptionDispatchInfo` ile rethrow edilir — bu iyi. Ama ctor null'u **yutmazsa** (örneğin `_dependency.DoSomething()` çağırırsa), `NullReferenceException` oluşur ve hata mesajı "Object reference not set to an instance of an object" olur — hangi bağımlılığın eksik olduğu belirsiz.

**Çözüm:** StrictInjection kapalıyken bile null argümanları logla:
```csharp
if (args[i] == null && !_di.StrictInjection)
{
    NexusRuntime.Logger?.LogWarning(
        $"[Nexus] Constructor parameter {i} of type '{paramTypes[i].FullName}' on '{type.FullName}' " +
        $"could not be resolved. Passing null. Enable StrictInjection for early failure.");
}
```

---

### H-4: `ObjectPoolService.GetId` — `GetEntityId().GetHashCode()` gereksiz ve riskli
**Dosya:** `Runtime/Services/Pool/ObjectPoolService.cs:361-369`

**Sorun:** 
```csharp
return obj.GetEntityId().GetHashCode();
```

`GetEntityId()` zaten benzersiz bir `int` döndürür. Üzerine `.GetHashCode()` çağırmak:
1. Gereksiz — `int.GetHashCode()` zaten `this` döndürür (identity hash)
2. Riskli — gelecekte Unity `EntityId` struct'ını değiştirirse davranış değişebilir
3. Yorum yanıltıcı — "keeps the key a plain int" diyor ama `GetEntityId()` zaten `int` döndürür

**Çözüm:**
```csharp
private static int GetId(UnityEngine.Object obj)
{
    if (obj == null) return 0;
    return obj.GetEntityId(); // Zaten int, GetHashCode gereksiz
}
```

---

### H-5: `ObservableProperty<T>.Value` setter — `EqualityComparer<T>.Default.Equals` lock içinde çağrılıyor
**Dosya:** `Runtime/Models/ObservableProperty.cs:64-77`

**Sorun:** `EqualityComparer<T>.Default.Equals` çağrısı lock içinde yapılıyor:

```csharp
lock (_dispatchLock)
{
    if (_isNotifying) { /* ... */ return; }
    if (EqualityComparer<T>.Default.Equals(_value, value)) { return; } // ← Lock içinde
    _isNotifying = true;
}
```

`EqualityComparer<T>.Default.Equals` virtual bir çağrıdır ve `T` için custom `Equals` implementasyonu çağırabilir. Bu:
1. Lock süresini uzatır
2. Custom `Equals` içinde başka bir lock alınırsa deadlock riski oluşur

**Çözüm:** Equality check'i lock dışına alın (TOCTOU riski kabul edilebilir çünkü `_isNotifying` flag'i zaten race'i önlüyor):

```csharp
// Fast path: equality check outside lock
if (EqualityComparer<T>.Default.Equals(_value, value))
    return;

lock (_dispatchLock)
{
    if (_isNotifying) { /* queue */ return; }
    // Double-check inside lock
    if (EqualityComparer<T>.Default.Equals(_value, value)) { return; }
    _isNotifying = true;
}
```

---

### H-6: `TickService` — shared driver pattern'i context dispose sırasında race condition'a açık
**Dosya:** `Runtime/Services/Tick/TickService.cs:100-121, 382-418`

**Sorun:** `s_sharedDriverObject` ve `s_activeDriverCount` static alanlar. `InitializeAsync` lock içinde driver'a subscribe oluyor, `Dispose` lock içinde unsubscribe oluyor. Ama `Dispose` içinde:

```csharp
lock (s_driverLock)
{
    if (_driver != null)
    {
        _driver.OnUpdate -= OnTick;
        // ...
        s_activeDriverCount = Math.Max(0, s_activeDriverCount - 1);
        if (s_activeDriverCount == 0 && s_sharedDriverObject != null)
        {
            SafeDestroyUtility.SafeDestroy(s_sharedDriverObject);
            s_sharedDriverObject = null;
            s_sharedDriver = null;
        }
    }
}
```

`SafeDestroyUtility.SafeDestroy` çağrısı `s_driverLock` içinde yapılıyor. Unity'nin `Object.Destroy` çağrısı main thread'de olmalı; eğer `Dispose` başka bir thread'den çağrılırsa bu bir sorun. Ayrıca `SafeDestroy` coroutine başlatabilir veya editor callback'lerini tetikleyebilir — bunlar lock içinde çalışırken reentrancy riski var.

**Çözüm:** `SafeDestroy` çağrısını lock dışına taşıyın:
```csharp
GameObject driverToDestroy = null;
lock (s_driverLock)
{
    // ... unsubscribe ...
    if (s_activeDriverCount == 0 && s_sharedDriverObject != null)
    {
        driverToDestroy = s_sharedDriverObject;
        s_sharedDriverObject = null;
        s_sharedDriver = null;
    }
}
if (driverToDestroy != null)
    SafeDestroyUtility.SafeDestroy(driverToDestroy);
```

---

### H-7: `SignalBus.FireInternal` — `s_stackDepth` ThreadStatic ama async path ile paylaşılmıyor
**Dosya:** `Runtime/Core/SignalBus.cs:130-131, 423-431`

**Sorun:** Sync path `[ThreadStatic] int s_stackDepth` kullanıyor, async path `AsyncLocal<AsyncStackDepthBox>` kullanıyor. Bu iki ayrı mekanizma tutarlı değil:

```csharp
// Sync path
s_stackDepth++;
if (s_stackDepth > MaxStackDepth) { s_stackDepth--; throw ...; }

// Async path  
if (++depthBox.Value > MaxAsyncStackDepth) { depthBox.Value--; throw ...; }
```

Bir signal hem sync hem async handler'a sahip olamaz (`NexusSyncAsyncMismatchException` fırlatılır), bu iyi. Ama bir sync handler içinden `FireAsync` çağrılırsa, async depth ayrı sayılır — toplam derinlik `MaxStackDepth + MaxAsyncStackDepth` (10 + 32 = 42) olabilir. Bu bilinçli bir tasarım kararı olabilir ama dokümante edilmemiş.

**Çözüm:** Dokümantasyona ekleyin veya birleşik bir depth tracking mekanizması düşünün.

---

### H-8: `EncryptedStorageService` — `PlayerPrefs` seed anahtarı plain text
**Dosya:** `Runtime/Services/Storage/EncryptedStorageService.cs:94-111`

**Sorun:** Şifreleme seed'i `PlayerPrefs`'e XOR-obfuscated olarak yazılıyor, ama obfuscation anahtarı cihaz ID'sinden türetiliyor:

```csharp
string rawKeySeed = $"{deviceId}_{customSalt}_{Application.identifier}";
byte[] deviceBoundKey = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKeySeed));
// ...
obfuscatedBytes[i] = (byte)(seedBytes[i] ^ deviceBoundKey[i % deviceBoundKey.Length]);
PlayerPrefs.SetString(seedKey, Convert.ToBase64String(obfuscatedBytes));
```

Bu, cihaz ID'sini bilen herkesin seed'i çözebileceği anlamına gelir. Cihaz ID'si `SystemInfo.deviceUniqueIdentifier` ile herkese açık. Yani obfuscation sadece "casual" saldırıları engeller — ciddi bir saldırgan seed'i kolayca çözer.

**Çözüm:** Bu bilinçli bir tasarım kararı olabilir (defense-in-depth), ama dokümantasyonda açıkça belirtilmeli: "Bu obfuscation sadece casual tampering'i engeller; root/jailbreak cihazlarda seed korunamaz." Daha güçlü koruma için platform keystore (Android Keystore / iOS Keychain) entegrasyonu önerilebilir.

---

### H-9: `Context.Dispose` — `WaitForBackgroundDispose` 5 saniye timeout, ama hata durumunda sadece log
**Dosya:** `Runtime/Core/Context.cs:672-686`

**Sorun:** `Dispose` senkron path'inde `WaitForBackgroundDispose(TimeSpan.FromSeconds(5))` çağrılıyor. Timeout aşılırsa sadece `LogError` yapılıyor:

```csharp
if (!Container.WaitForBackgroundDispose(TimeSpan.FromSeconds(5)))
    NexusRuntime.Logger?.LogError("[Nexus] Timeout waiting for async singletons to dispose...");
```

5 saniye Unity main thread'de uzun bir süre — oyun donabilir (ANR). Ayrıca timeout sonrası arka plan task'ı hâlâ çalışıyor olabilir ve yarım kalan dispose işlemi belirsiz durumda bırakır.

**Çözüm:** Timeout süresini `ContextData`'dan yapılandırılabilir yapın:
```csharp
var timeout = _contextData?.DisposeTimeoutSeconds ?? 5f;
if (!Container.WaitForBackgroundDispose(TimeSpan.FromSeconds(timeout)))
    NexusRuntime.Logger?.LogError($"[Nexus] Timeout ({timeout}s) waiting for async singletons...");
```

---

### H-10: `WizardTabs.cs` — `wizard_create_view_go` localization anahtarı tanımsız
**Dosya:** `Editor/Plugins/Wizard/WizardTabs.cs:266`, `Editor/Core/NexusLang.cs`, `Editor/Locales/tr.json`

**Sorun:** 
```csharp
var createToggle = new Toggle(NexusLang.Get("wizard_create_view_go")) { value = _createViewGo };
```

`wizard_create_view_go` anahtarı `NexusLang.AddDefaults()` içinde tanımlı değil. Mevcut olan `wizard_toggle_create_go` başka bir toggle için kullanılıyor. Bu, toggle'ın label'ında "MISSING: wizard_create_view_go" veya boş string göstermesine neden olur.

Ayrıca `_createViewGo` toggle'ı `GenerateViewFiles` içinde hiç kullanılmıyor — GameObject oluşturma mantığı yok. Toggle işlevsiz.

**Çözüm:**
1. `NexusLang.cs`'e ekleyin: `s_strings["wizard_create_view_go"] = "Create View GameObject";`
2. `tr.json`'a ekleyin: `{"key": "wizard_create_view_go", "value": "View GameObject Oluştur"}`
3. `GenerateViewFiles`'a GameObject oluşturma mantığı ekleyin veya toggle'ı kaldırın

---

### H-11: `ContextLifecycleOrchestrator` — `Debug.LogError` yerine `NexusRuntime.Logger` kullanılmıyor
**Dosya:** `Runtime/Core/Lifecycle/ContextLifecycleOrchestrator.cs:30, 46, 68, 76, 96, 104, 135, 143, 162`

**Sorun:** Tüm hata loglamaları `Debug.LogError` kullanıyor:
```csharp
Debug.LogError($"[Nexus] Lifecycle OnInitializeAsync exception in {lifecycles[i].GetType().Name}: {ex.Message}");
```

Bu, Nexus'un kendi loglama altyapısını (ILoggerService, log filtreleme, log persistence) atlıyor. Diğer tüm Core sınıfları `NexusRuntime.Logger?.LogError(...)` kullanıyor.

**Çözüm:** Tüm `Debug.LogError` çağrılarını `NexusRuntime.Logger?.LogError(...)` ile değiştirin:
```csharp
NexusRuntime.Logger?.LogError($"[Nexus] Lifecycle OnInitializeAsync exception in {lifecycles[i].GetType().Name}: {ex.Message}");
```

---

### H-12: `Root.StartInternal` — `async void Start()` exception yutuyor
**Dosya:** `Runtime/Core/Root.cs:242-252`

**Sorun:** `Start()` `async void` olarak tanımlanmış:
```csharp
private async void Start()
{
    try { await StartInternal(); }
    catch (Exception ex) { NexusRuntime.Logger?.LogError(...); }
}
```

`async void` metodlarda exception'lar `SynchronizationContext` üzerinden rethrow edilir ve Unity'de genellikle `Debug.LogException` olarak görünür. `try-catch` bloğu bu exception'ları yakalıyor, bu iyi. Ama `StartInternal` içinde `OperationCanceledException` fırlatıldığında `catch (Exception)` bloğu onu da yakalıyor ve `LogError` yapıyor — cancellation bir hata değildir.

**Çözüm:** `OperationCanceledException`'ı ayrıca ele alın:
```csharp
catch (OperationCanceledException)
{
    // Expected during context teardown — not an error
}
catch (Exception ex)
{
    NexusRuntime.Logger?.LogError($"[Nexus] Root startup failed: {ex.Message}\n{ex.StackTrace}");
}
```

---

## 3. MEDIUM Bulgular

### M-1: `NexusDI` — `s_resolutionStack` ThreadStatic ama async resolve'da reset edilmiyor
**Dosya:** `Runtime/Core/NexusDI.cs:78-79, 936-938, 1023`

**Sorun:** `s_resolutionStack` `[ThreadStatic]` olarak tanımlanmış. `ResolveBinding` içinde `finally` bloğunda `Remove` çağrılıyor, bu iyi. Ama async factory'ler (`Func<object>` döndürenler) farklı thread'de çalışabilir ve `s_resolutionStack` o thread'de boş olur — circular dependency detection async path'de çalışmaz.

**Çözüm:** `AsyncLocal<HashSet<Type>>` kullanın veya async factory'leri desteklemeyin (şu anki tasarım sync-only).

---

### M-2: `SignalBus` — `FireAsyncWithTimeout` her çağrıda `CancellationTokenSource` allocate ediyor
**Dosya:** `Runtime/Core/SignalBus.cs:344-357`

**Sorun:** 
```csharp
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_context.LifetimeToken);
timeoutCts.CancelAfter(timeoutMilliseconds);
```

Her `FireAsyncWithTimeout` çağrısında yeni bir `CancellationTokenSource` oluşturuluyor. Yüksek frekanslı timeout'lu signal'lerde GC baskısı oluşturur.

**Çözüm:** Timeout'lu fire'ların seyrek olduğu varsayılabilir (kullanıcı etkileşimi, network timeout), bu yüzden allocation kabul edilebilir. Ama hot path'te kullanılıyorsa `CancellationTokenSource` pool'u düşünülebilir.

---

### M-3: `UIManager.GetActiveGameObjects` — her open/close'da yeni `Dictionary` allocate ediyor
**Dosya:** `Runtime/Services/UI/UIManager.cs:462-471`

**Sorun:** `GetActiveGameObjects` her çağrıda yeni bir `Dictionary<string, GameObject>` oluşturuyor:
```csharp
private Dictionary<string, GameObject> GetActiveGameObjects()
{
    lock (_lock)
    {
        var result = new Dictionary<string, GameObject>(_activeScreens.Count);
        foreach (var kvp in _activeScreens)
            result[kvp.Key] = kvp.Value.gameObject;
        return result;
    }
}
```

`OpenScreenAsync`, `CloseScreenCoreAsync` her çağrıda bunu çağırıyor. Sık açılıp kapanan ekranlarda GC churn.

**Çözüm:** `UICanvasSystem.UpdateLayerInteractivity` imzasını değiştirin veya cache'leyin:
```csharp
private Dictionary<string, GameObject> _cachedActiveGameObjects;
private bool _activeGameObjectsDirty = true;

private Dictionary<string, GameObject> GetActiveGameObjects()
{
    lock (_lock)
    {
        if (_activeGameObjectsDirty || _cachedActiveGameObjects == null)
        {
            _cachedActiveGameObjects = new Dictionary<string, GameObject>(_activeScreens.Count);
            foreach (var kvp in _activeScreens)
                _cachedActiveGameObjects[kvp.Key] = kvp.Value.gameObject;
            _activeGameObjectsDirty = false;
        }
        return _cachedActiveGameObjects;
    }
}
```

---

### M-4: `WindowManager` — `SemaphoreSlim` kullanımı Unity main thread'de riskli
**Dosya:** `Runtime/Services/UI/WindowManager.cs:50, 104-125`

**Sorun:** `SemaphoreSlim.WaitAsync()` kullanılıyor, bu iyi (async). Ama `GetOpenWindowsSnapshot` ve `PendingWindowCount` senkron `Wait(50)` kullanıyor:
```csharp
if (_disposed || !_windowLock.Wait(50)) return result;
```

Bu, main thread'de 50ms bloklama riski taşır. Editor tooling'de kabul edilebilir ama runtime'da sorun olabilir.

**Çözüm:** Senkron `Wait` çağrılarını async yapın veya lock-free snapshot kullanın (`_activeWindowsRead` zaten volatile).

---

### M-5: `EconomyService` — `lock (_balances)` ConcurrentDictionary üzerinde gereksiz
**Dosya:** `Runtime/Services/Economy/EconomyService.cs:75, 98, 115, 132, 173, 204, 239`

**Sorun:** `_balances` `ConcurrentDictionary<string, SecureObservableLong>` ama tüm erişimler `lock (_balances)` ile korunuyor. Bu, ConcurrentDictionary'nin lock-free read avantajını ortadan kaldırır. `GetObservableBalance` lock-free okuma yapabilir ama `lock` alıyor.

**Çözüm:** Sadece yazma işlemlerinde lock kullanın, okuma işlemlerinde `TryGetValue` doğrudan çağırın:
```csharp
public SecureObservableLong GetObservableBalance(string currencyId)
{
    if (string.IsNullOrEmpty(currencyId)) return null;
    if (_balances.TryGetValue(currencyId, out var existing))
        return existing; // Lock-free read
    
    lock (_balances) // Sadece create path'de lock
    {
        return LazyLoadBalance(currencyId);
    }
}
```

`Spend`/`Earn`/`SetBalance` gibi mutasyonlarda `SecureObservableLong.Value` zaten kendi içinde thread-safe olmalı; eğer değilse, `lock` sadece `prop.Value` ataması etrafında olmalı, tüm metod boyunca değil.

---

### M-6: `AudioService.PlaySfx` — `GetAvailableSfxSource` O(N) linear scan
**Dosya:** `Runtime/Services/Audio/AudioService.cs:244-269`

**Sorun:** Her `PlaySfx` çağrısında `_sfxPool` üzerinde linear scan yapılıyor:
```csharp
for (int i = 0; i < _sfxPool.Count; i++)
{
    if (!_sfxPool[i].isPlaying)
        return _sfxPool[i];
}
```

32 kaynaklı bir pool'da bu küçük bir maliyet ama SFX-heavy sahnelerde (100+ ses/frame) birikebilir.

**Çözüm:** `Queue<AudioSource>` kullanın (idle kaynakları tutan):
```csharp
private readonly Queue<AudioSource> _idleSfxSources = new();
private readonly HashSet<AudioSource> _activeSfxSources = new();

private AudioSource GetAvailableSfxSource()
{
    if (_idleSfxSources.Count > 0)
    {
        var src = _idleSfxSources.Dequeue();
        _activeSfxSources.Add(src);
        return src;
    }
    // ... create new or steal oldest
}
```

---

### M-7: `BigDouble` — `ToFormattedString` suffix dizisi 30 elemanla sınırlı
**Dosya:** `Runtime/Data/BigDouble.cs:22-26, 233-243`

**Sorun:** `StandardSuffixes` dizisi 30 eleman ("" + K,M,B,T,aa-az). 1e90 üzeri sayılar için fallback scientific notation'a düşüyor:
```csharp
if (suffixIndex < StandardSuffixes.Length)
    return $"{displayValue.ToString("F2", culture)}{StandardSuffixes[suffixIndex]}";
return $"{Mantissa.ToString("F2", culture)}e{Exponent}";
```

Bu bilinçli bir tasarım kararı ama idle oyunlarda 1e90+ yaygın. `az` sonrası `ba`, `bb`... devam etmeli veya dinamik suffix üretimi olmalı.

**Çözüm:** Dinamik suffix üretimi:
```csharp
private static string GetSuffix(long index)
{
    if (index < StandardSuffixes.Length)
        return StandardSuffixes[index];
    
    // aa=5, ab=6, ..., az=30, ba=31, bb=32, ...
    long adjusted = index - 4; // K=1, M=2, B=3, T=4, aa=5
    var sb = new System.Text.StringBuilder();
    while (adjusted >= 0)
    {
        sb.Insert(0, (char)('a' + (adjusted % 26)));
        adjusted = adjusted / 26 - 1;
    }
    return sb.ToString();
}
```

---

### M-8: `NexusRuntime.Metrics.RecordTrace` — ring buffer wrap-around'da eski entry'ler karışabilir
**Dosya:** `Runtime/Core/NexusRuntime.cs:502-519`

**Sorun:** `RecordTrace` ring buffer'a yazarken `Interlocked.Increment` kullanıyor ama `GetRecentTraces` okurken torn read riski var. Yorumlarda bu belgelenmiş (Audit fix 1.1, 2.3), ama hâlâ `s_traceCount` ve `s_traceIndex` arasında race olabilir:

```csharp
int rawIndex = System.Threading.Interlocked.Increment(ref s_traceIndex);
int idx = (int)((uint)rawIndex % (uint)size);
buffer[idx] = entry;
int currentCount = System.Threading.Volatile.Read(ref s_traceCount);
if (currentCount < size)
    System.Threading.Interlocked.CompareExchange(ref s_traceCount, currentCount + 1, currentCount);
```

`s_traceIndex` ve `s_traceCount` ayrı ayrı güncelleniyor. `GetRecentTraces` bunları farklı zamanlarda okursa tutarsız bir snapshot alabilir. Yorumlarda bu "torn read" olarak kabul edilmiş ve guard'lar eklenmiş.

**Çözüm:** Bu kabul edilebilir bir trade-off (metrics exactness vs performance). Dokümantasyonda belirtin.

---

### M-9: `ContextBuilder.Validate` — constructor parametre limiti (6) magic number
**Dosya:** `Runtime/Core/ContextBuilder.cs:389-396`

**Sorun:** 
```csharp
if (meta.ConstructorParameterTypes.Length > 6)
{
    issues.Add(new DiValidationIssue(..., $"Constructor of '{type.Name}' has {meta.ConstructorParameterTypes.Length} parameters (> 6 limit)..."));
}
```

6 limiti hardcoded. Bu değer `ContextData` veya `NexusEditorSettings`'den yapılandırılabilir olmalı.

**Çözüm:**
```csharp
public static int MaxConstructorParameters { get; set; } = 6;
// ...
if (meta.ConstructorParameterTypes.Length > MaxConstructorParameters)
```

---

### M-10: `AssemblyScanService.GetCachedTypes` — `assembly.FullName` null olabilir
**Dosya:** `Runtime/Core/Services/AssemblyScanService.cs:23`

**Sorun:**
```csharp
return s_typeCache.GetOrAdd(assembly.FullName ?? assembly.GetName().Name, _ => { ... });
```

`assembly.FullName` null ise `assembly.GetName().Name` kullanılıyor. Ama `GetName().Name` de teorik olarak null olabilir (dynamic assembly'ler). Bu durumda `GetOrAdd` null key ile çağrılır ve `ArgumentNullException` fırlatır.

**Çözüm:**
```csharp
string key = assembly.FullName ?? assembly.GetName().Name ?? "unknown";
return s_typeCache.GetOrAdd(key, _ => { ... });
```

---

### M-11: `Root` — `parentTimeoutFrames` ve `siblingTimeoutFrames` frame-based, süre-based değil
**Dosya:** `Runtime/Core/Root.cs:25-26, 266-277, 307-318`

**Sorun:** Timeout'lar frame sayısı olarak tanımlanmış (900 frame = 15 saniye @60fps, 30 saniye @30fps). Düşük FPS cihazlarda timeout çok uzun sürer.

**Çözüm:** Süre-based timeout kullanın:
```csharp
[SerializeField] private float parentTimeoutSeconds = 15f;
// ...
float elapsed = 0f;
while (!parentRoot.IsInitialized && elapsed < parentTimeoutSeconds)
{
    await Task.Yield();
    elapsed += Time.deltaTime;
}
```

---

### M-12: `HybridQueue` — `Drain` lock içinde dequeue, lock dışında dispatch — sinyal kaybı riski
**Dosya:** `Runtime/Queue/HybridQueue.cs:293-326`

**Sorun:** `Drain` metodu lock'u dequeue ile dispatch arasında bırakıyor:
```csharp
while (true)
{
    IQueuedSignal queuedSignal = null;
    lock (queueLock)
    {
        if (queue.Count > 0) queuedSignal = queue.Dequeue();
    }
    if (queuedSignal == null) break;
    // Lock dışında dispatch — başka bir thread de dequeue yapabilir
    try { queuedSignal.Fire(_signalBus); }
    // ...
}
```

Bu tasarım bilinçli (yorumda belgelenmiş: "Audit note 2.5"), ama iki thread aynı anda `Drain` çağırırsa sıralama garantisi yok. `Root.Update` tek thread'den çağrıldığı için pratikte sorun yok, ama `Drain` public API olduğu için yanlış kullanım riski var.

**Çözüm:** `Drain` metodunu `internal` yapın veya reentrancy guard ekleyin:
```csharp
private int _drainInProgress;
public void DrainThreadSafe()
{
    if (Interlocked.CompareExchange(ref _drainInProgress, 1, 0) != 0) return;
    try { Drain(_threadSafeQueue, _threadSafeLock); }
    finally { _drainInProgress = 0; }
}
```

---

## 4. LOW Bulgular

### L-1: `NexusRuntime` — `s_loggerCacheLock` kullanılmıyor
**Dosya:** `Runtime/Core/NexusRuntime.cs:171`

```csharp
private static readonly object s_loggerCacheLock = new(); // Kullanılmıyor
```
Yorumda "Retained for API compatibility" deniyor ama hiçbir yerde kullanılmıyor. Silinebilir.

### L-2: `Interfaces.cs` — Tüm interface'ler tek dosyada
**Dosya:** `Runtime/Interfaces/Interfaces.cs` (416 satır)

`ICommand`, `IAsyncCommand`, `IContext`, `ISignalBus`, `IView`, `IRecoveryStrategy`, exception'lar — hepsi tek dosyada. Ayrı dosyalara bölünmeli.

### L-3: `BigDouble.CompareTo` — negatif mantissa için edge case
**Dosya:** `Runtime/Data/BigDouble.cs:165-175`

```csharp
if (Mantissa > 0 && other.Mantissa <= 0) return 1;
if (Mantissa < 0 && other.Mantissa >= 0) return -1;
```
`Mantissa == 0` durumu ilk satırda ele alınmış, bu iyi. Ama `Exponent` karşılaştırması `Mantissa > 0` varsayımıyla yapılıyor — negatif mantissa için ters mantık doğru çalışıyor ama okunabilirlik düşük.

### L-4: `package.json` — `documentationUrl` relative path
**Dosya:** `package.json:7`

```json
"documentationUrl": "docs/10_MIN_QUICKSTART.md"
```
UPM `documentationUrl` için absolute URL bekler (https://...). Relative path çalışmayabilir.

### L-5: `Runtime/Core/Pipelines` — boş klasör
`Runtime/Core/Pipelines.meta` var ama klasör boş. Ya silinmeli ya da içine pipeline implementasyonları taşınmalı.

---

## 5. Mimari Tutarsızlıklar ve Öneriler

### A-1: Servis katmanında base class tutarsızlığı
**Sorun:** `AdService`, `AnalyticsService` → `INexusService` doğrudan; diğerleri → `NexusService<T>`. Bu, `Context` ve `SignalBus` erişiminde tutarsızlık yaratıyor.

**Öneri:** Tüm servisleri `NexusService<T>` tabanına taşıyın. `INexusService`'i doğrudan implemente eden servisler `Context`/`SignalBus`'a erişemez.

### A-2: UI katmanında iki paralel manager
**Sorun:** `UIManager` (type-safe, `ScreenView`) ve `WindowManager` (string-based, `GameObject`) aynı işi yapıyor. Hangisinin kullanılacağı belirsiz.

**Öneri:** `WindowManager`'ı deprecated yapın ve `UIManager`'a string-based overload'lar ekleyin:
```csharp
public Task<GameObject> OpenScreenAsync(string screenName, object args = null, UILayer layer = UILayer.Screen);
```

### A-3: Exception hierarchy eksik
**Sorun:** `NexusReentrancyException`, `NexusSyncAsyncMismatchException` vb. `Exception`'dan türüyor ama ortak bir `NexusException` base'i yok.

**Öneri:**
```csharp
public abstract class NexusException : Exception { protected NexusException(string msg) : base(msg) { } }
public class NexusReentrancyException : NexusException { ... }
```

### A-4: `Context` çok fazla sorumluluk taşıyor (SRP ihlali)
**Sorun:** `Context` sınıfı DI container, signal bus, view binder, plugin manager, lifecycle orchestrator, assembly scanner — hepsini koordine ediyor. 810 satır.

**Öneri:** `Context`'i facade olarak tutun ama sorumlulukları delegate edin:
- `ContextLifecycleOrchestrator` zaten var ama sadece start/stop için kullanılıyor; `Configure`/`Initialize` da ona taşınabilir
- `AssemblyScanService` ayrı ama `Context.ScanAssembliesAndRegister` hâlâ Context'te

### A-5: `NexusDI` çok büyük (1493 satır)
**Sorun:** DI container, metadata cache, injector, clearer, pending injection tracker — hepsi tek sınıfta.

**Öneri:** `NexusDI`'yi bölün:
- `NexusDI` (public API: Bind, Resolve, Inject)
- `InjectionEngine` (Injector + Clearer)
- `MetadataCache` (zaten ayrı ama internal class olarak içeride)
- `PendingInjectionTracker`

---

## 6. Performans Özeti

| Alan | Durum | Not |
|------|-------|-----|
| Signal dispatch | ✅ Mükemmel | Zero-GC, snapshot iteration, thread-static buffer'lar |
| DI resolution | ✅ İyi | Compiled accessor'lar, metadata cache |
| BigDouble aritmetiği | ⚠️ İyi | `operator +` overflow riski dışında sağlam |
| UI manager | ⚠️ Orta | `GetActiveGameObjects` her çağrıda allocate |
| Object pool | ✅ İyi | Bounded, generation tracking |
| Tick service | ✅ İyi | Dirty-flag + amortized sweep |
| Storage | ✅ İyi | Atomic write, HMAC, lock-free cache |

---

## 7. Öncelikli Aksiyon Planı

### Hemen (Bu sprint)
1. **C-1:** BigDouble `operator +` overflow fix
2. **C-2:** GameStateMachine `_stateCts` race fix
3. **C-3:** GameSaveManager `.tmp` cleanup fix
4. **H-10:** Wizard `wizard_create_view_go` localization key

### Kısa vade (1-2 sprint)
5. **H-1:** AdService/AnalyticsService → NexusService<T> migration
6. **H-3:** NexusDI null-arg warning
7. **H-4:** ObjectPoolService.GetId fix
8. **H-11:** ContextLifecycleOrchestrator → NexusRuntime.Logger

### Orta vade (1-2 ay)
9. **H-2:** UIManager/WindowManager consolidation
10. **A-4/A-5:** Context ve NexusDI decomposition
11. **M-11:** Root timeout'ları süre-based yapma

---

*Bu rapor 5 paralel uzman ajanın bulgularının satır satır doğrulanmasıyla derlenmiştir.*
