# Nexus Altyapısı — Derin Denetim Raporu (v0.3.2)

**Kapsam:** `Nexus/Packages/com.nexus.core` (v0.3.2) — runtime, servisler, Netcode/DOTS, Editor Validation, paket meta.
**Yöntem:** Tüm çekirdek runtime dosyaları satır satır okundu; tüm bulgular kod üzerinde satır numarasıyla doğrulandı.

## Yönetici Özeti

Mevcut `Nexus_Eksiklik_Raporu.md` "tüm kritik ve orta seviye maddeler kapatıldı" demektedir; ancak bu derin inceleme, o raporda **hiç yer almayan 8 kritik (P0), 17 yüksek (P1), 17 orta/düşük (P2) sorun ve 12 ek bulgu** ortaya koymuştur. En kritik üç madde, **belgelenmiş davranış ile gerçek kod arasında doğrudan çelişkidir**:

1. **P0-1:** README'nin belgelediği `[SignalHandler]` + `ICommand<T>` (Style B) komutları **sessizce hiç kayıt edilmiyor**.
2. **P0-2:** `NEXUS_DEBUG` asmdef hilesiyle **production dahil her zaman açık** — tüm "compiled away" iddiaları geçersiz.
3. **P0-5:** `[CommandTimeout]` attribute'u **runtime'da hiç uygulanmıyor** (ölü özellik, sample dokümantasyonu yanıltıcı).

---

## 🔴 P0 — Kritik Hatalar

### P0-1. `[SignalHandler]` + yalnızca `ICommand<T>` uygulayan komutlar SESSİZCE ATLANIYOR
**Dosya:** `Runtime/Core/Context.cs` → `ScanAssembliesAndRegister` (≈ satır 255–266) + `Runtime/Interfaces/Interfaces.cs` (satır 9–27)

```csharp
if (typeof(ICommand).IsAssignableFrom(type)) { ... RegisterCommand(..., isAsync: false); }
else if (typeof(IAsyncCommand).IsAssignableFrom(type)) { ... isAsync: true); }
// else dalı YOK — generic-only tipler sessizce yok sayılır
```

`Interfaces.cs`'de `ICommand<TSignal>` **non-generic `ICommand`'dan türemez** (bağımsız arayüzler). Sonuçlar:
- Sınıf yalnızca `ICommand<T>` uygularsa (kök README'nin "Style B" örneği tam olarak böyle!) → hiçbir dala girmez, attribute **sessizce yok sayılır**, hata/uyarı yok.
- Sınıf yalnızca non-generic `ICommand` uygularsa → `SignalBus.RegisterCommand` "must implement ICommand<X>" diye **throw eder**.
- Attribute keşfi fiilen yalnızca **her iki arayüzü birden** uygulayan sınıflarda çalışır — bu da `RegisterCommand`'daki "cannot implement both" kuralına takılabilir.
- Composite kayıtta da benzer hata: `bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(type)` — yalnızca `IAsyncCommand<T>` uygulayan composite komut **sync olarak** kaydedilir.

**Etki:** README (kök, Adım 4 / Yöntem B) belgelenen canonical akış çalışmaz. Counter örneği `[SignalHandler]` kullanmadığı için bug maskelenmiştir.
**Düzeltme:** Tarama koşuluna generic arayüz kontrolü ekle (SignalBus'taki `ImplementsGenericInterface(type, typeof(ICommand<>))` yardımcısı zaten mevcut; internal yap ya da kopyala) ve eşleşmeyen tipler için `LogError` üret.

### P0-2. `NEXUS_DEBUG` production dahil HER ZAMAN AÇIK (asmdef versionDefines hilesi)
**Dosya:** `Runtime/com.nexus.core.asmdef`

```json
"versionDefines": [{"name": "com.unity.modules.unitywebrequest", "expression": "1.0.0", "define": "NEXUS_DEBUG"}]
```

`unitywebrequest` built-in modülü pratikte her projede etkindir → `NEXUS_DEBUG` kalıcı tanımlı. Etkilenenler:
- `Runtime/Core/SignalBus.cs` içinde **30+ `#if NEXUS_DEBUG` bloğu** (ProfilerMarker'lar, NexusTrace event'leri) — release build'de de derlenir.
- `Runtime/Tracing/CausalTracing.cs` (satır 87, 165, 217, 248): *"Compiled away when NEXUS_DEBUG is not defined"* iddiası **yanlış**.
- Ayrıca `#if UNITY_EDITOR || DEVELOPMENT_BUILD` davranış farklarıyla (throw vs log) üçlü karışık bir derleme matrisi oluşur.

**Etki:** Performans + "0 GC" iddiası release build'de ihlal edilir; kullanıcı trace maliyetini kapatamaz.
**Düzeltme:** versionDefines hilesini kaldır; `NEXUS_DEBUG`'ı gerçek opt-in scripting define'a dönüştür.

### P0-3. "0 GC steady-state" iddiası hot path'te birden çok yerde ihlal ediliyor
**Dosya:** `Runtime/Core/SignalBus.cs`

- `FireInternal` (≈ satır 442): `NexusRuntime.Metrics.RecordTrace($"▶ {typeof(T).Name}")` — **her Fire'da koşulsuz string interpolation**. `NexusRuntime.cs`'de (≈ satır 212) "Production tracing ring buffer — always active" olarak işaretli.
- `ExecuteCommand<TSignal>` (≈ satır 816): `RecordTrace($"  └ {handler.CommandType.Name}")` — her komutta string.
- `ExecuteCommand<TSignal>` (≈ satır 832): `ExecuteWithDecorators(genericSyncCmd, () => genericSyncCmd.Execute(signal))` — **her dispatch'te closure allocation** (signal struct capture).
- `NexusDI.Inject` her dispatch'te reflection `FieldInfo.SetValue` (codegen injector yoksa) + `ClearInjectedReferences` her pool iadesinde reflection.
- `SignalBus.CommandHandlers` ve `RegisteredHandlers` property'leri her erişimde `new Dictionary<...>`.
- `SweepDeadNodes` her tetiklendiğinde `new List<Type>(_subscriptions.Keys)`.

**Düzeltme:** RecordTrace çağrılarını `#if NEXUS_DEBUG`'a al ya da string üretmeyen (Type cache'li) forma çevir; decorator yolunu closure'sız hale getir (decorator yoksa doğrudan `Execute(signal)` çağır — mevcut kodda decorator olmasa da closure yaratılıyor).

### P0-4. Sync `Fire()` + async handler = runtime exception (kırılgan tasarım) ve iç tutarsızlıklar
**Dosya:** `Runtime/Core/SignalBus.cs` (≈ satır 449–458), `Runtime/Queue/HybridQueue.cs`, `Runtime/Netcode/NetworkSignalBus.cs`

`FireInternal`, sinyalin async handler'ı/aboneliği varsa `NexusSyncAsyncMismatchException` fırlatır. Sonuçlar:
- Herhangi bir Mediator `SubscribeAsync<T>` eklediği anda o sinyalin **tüm mevcut sync `Fire()` çağrıları** çalışma zamanında patlar; derleme zamanı güvencesi yok.
- Hata yolu: `HandleCommandErrorWithDecision` → `Fire(failedSignal)` **sync** çağrılır (≈ satır 1398, 1443 vb.); `CommandFailedSignal`'a async abone bağlıysa **hata işleme sırasında ikinci exception**.
- `HybridQueue.DrainThreadSafe/DrainNextFrame` wrapper'ı `bus.Fire(Signal)` (sync) çağırır; `NetworkSignalHistory.ReplaySignals` de `localSignalBus.Fire(...)` kullanır → async handler'lı sinyaller kuyruklandığında/rollback'te drain exception'la kesilir; kuyruklar için async yol yok.
- `FireInternalAsyncFromSync` (satır 612) **hiçbir yerden çağrılmıyor — ölü kod** (findstr ile doğrulandı: yalnızca tanım var).

**Düzeltme:** Kuyruk drain'ine ve `Fire(failedSignal)` yollarına `FireAsyncAndForget` köprüsü ekle; ölü kodu kaldır ya da yeniden bağla.

### P0-5. `[CommandTimeout]` attribute'u runtime'da HİÇ uygulanmıyor (ölü özellik)
**Dosya:** `Runtime/Attributes/Attributes.cs` (satır 125–131), `Samples~/Counter/CounterAsyncCommand.cs`

Paket genelinde `CommandTimeout` araması yalnızca **tanımı** buluyor (Attributes.cs) — `SignalBus.cs` ve `CommandHandlerInfo.cs`'de tek referans yok. Counter örneği ise *"if execution exceeds the timeout, the bus cancels via the CancellationToken"* diye belgeliyor ve `[CommandTimeout(2000)]` kullanıyor. `ExecuteCommandAsync` token'ı olduğu gibi geçirir; timeout üretmez. `STABILITY.md` bile `[CommandTimeout]` maddesini API yüzeyinde sayıyor.

**Düzeltme:** `RegisterCommand`'da `GetCustomAttribute<CommandTimeoutAttribute>()` okunup `CommandHandlerInfo`'ya `TimeoutMs` eklenmeli; `ExecuteCommandAsync` linked CTS + `CancelAfter` uygulamalı — ya da attribute ve tüm dokümantasyonu kaldırılmalı.

### P0-6. "AES-256" iddiası ama gerçekte AES-128
**Dosya:** `Runtime/Services/Storage/EncryptedStorageService.cs` (≈ satır 88–91)

```csharp
_encryptionKey = new byte[16]; // AES-128 key
_hmacKey = new byte[16];
```

Sınıf doc'u (satır 13) ve kök README ("AES-256 Anti-Cheat", servis kataloğu) **AES-256** der. HMAC anahtarı da 16 bayt ve `ComputeHmac` çıktısı 16 bayta kırpılıyor (HMAC-SHA256-128).

**Düzeltme:** `_encryptionKey`'i 32 bayta çıkar (seed hash'i zaten 32 bayt; HMAC anahtarı için ikinci bir türetim — örn. `SHA256(seed + "hmac")` — kullan) veya tüm "AES-256" metinlerini "AES-128" yap. Mevcut kayıtlar için migrasyon gerekir.

### P0-7. Reentrancy sayacı bozuluyor
**Dosya:** `Runtime/Core/SignalBus.cs` — `FireInternal` (≈ satır 468–479) ve `FireInternalAsync` (≈ satır 655–666)

Overflow dalında `s_stackDepth.Value = 0` atanıp throw edilir; ardından `finally { s_stackDepth.Value--; }` çalışır → sayaç **-1** olur ve kalıcı kayar (sonraki dispatch'ler -1'den başlar; limit fiilen 11, 12... olur; her overflow'da bir daha kayar).

**Düzeltme:** Overflow dalındaki `s_stackDepth.Value = 0` satırını kaldır; `finally` decrement'i zaten dengeler.

### P0-8. `Root` registry'de tutarsız kilit nesnesi
**Dosya:** `Runtime/Core/Root.cs` (≈ satır 52–86)

`OnEnable`/`OnDisable`/`EnsureRegistry` → `lock (s_rootLock)`; fakat `ClearRegistry` → `lock (s_allRoots)`. İki farklı kilit → temizlik ile yeniden doldurma yarışabilir. Ayrıca `ClearRegistry`'de `s_registryDirty = true` ataması **kilit dışında** yapılıyor.

**Düzeltme:** `ClearRegistry`'de `lock (s_rootLock)` kullan ve bayrak atamasını kilit içine al.

---

## 🟠 P1 — Yüksek Öncelikli Sorunlar

### P1-1. Priority dokümantasyonu kodla ZIT
**Dosyalar:** `Runtime/Attributes/Attributes.cs` (satır 27 `SignalHandlerAttribute.Priority`, satır 49 `CompositeSignalHandlerAttribute.Priority`), `Runtime/Core/CommandHandlerInfo.cs` (satır 18, 27), `Runtime/Core/ContextBuilder.cs` (`BindCommand`/`BindAsyncCommand` XML doc'ları)

Hepsi *"lower values run first"* der. Gerçek: `SignalBus.RegisterCommand` (≈ satır 279) → `list.Sort((a, b) => b.Priority.CompareTo(a.Priority))` — **yüksek değer önce**. `RegisterCompositeCommand` da aynı sıralamayı kullanır. Kullanıcılar komutları ters sırada yazar.

### P1-2. SignalBus thread-safety delikleri
**Dosya:** `Runtime/Core/SignalBus.cs`
- `RegisterCommand`/`RegisterCompositeCommand` `_commandHandlers` ve `_compositeTriggersBySignal`'a **`_handlerReadLock` almadan** yazar; yalnızca okuyucu property'ler kilitler → kilit etkisiz.
- `FireInternal`, `_subscriptions.TryGetValue` ve `HasAsyncSubscriptions`'ı `_subLock` **olmadan** okur; `Subscribe/Unsubscribe` kilitle yazar → Dictionary eşzamanlı okuma/yazma tanımsız davranış.
- `_hasAsyncHandler` de kilitsiz okunur/yazılır.
- `ProcessCompositeTriggers` `_compositeTriggersBySignal.TryGetValue`'yu kilitsiz okur.

**Düzeltme:** Yazımları kilit altına al; okuma için `Context._pluginsReadOnlyCopy` benzeri volatile snapshot deseni uygula.

### P1-3. `throw ex;` ile stack trace kaybı
**Dosya:** `Runtime/Core/SignalBus.cs` satır **1357** ve **1420** (`HandleCommandErrorWithDecision` / `...Async` başları). Orijinal stack trace sıfırlanır. Doğru desen paketin kendisinde var: `NexusDI.CreateInstance` → `ExceptionDispatchInfo.Capture(ex.InnerException).Throw()`.

### P1-4. NexusDI sessiz null-injection
**Dosya:** `Runtime/Core/NexusDI.cs` — `Inject` (alan/özellik/metot; ≈ satır 396–430) ve `CreateInstance` ctor parametreleri `TryResolve` ile çözülür; kayıtlı olmayan bağımlılık **sessizce null** kalır → hata çok sonra ilgisiz bir NRE olarak patlar. En azından dev build'de eksik `[Inject]` bağımlılığı loglanmalı/throw edilmeli.

### P1-5. NexusDI global kilit ve statik durum
**Dosya:** `Runtime/Core/NexusDI.cs`
- `s_singletonLock` + `s_constructingSingletons` **tüm container'lar arasında paylaşılan statik** — bir context'in singleton kurulumu diğer tüm context'leri serileştirir; farklı container'larda aynı Type'ın eşzamanlı kurulumu yanlış "circular dependency" tetikleyebilir.
- `GetActiveSingletons()` canlı `_resolvedSingletons` HashSet'ini kopyasız döndürür (enumeration sırasında mutasyon riski).
- `BindInstance` bile `lock (s_singletonLock)` alır → global çekişme.

### P1-6. `IAsyncDisposable` main-thread block → deadlock riski
**Dosya:** `Runtime/Core/NexusDI.cs` satır **569**: `asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult()`. Kullanıcının `DisposeAsync`'i Unity SynchronizationContext'e dönmeye çalışırsa (`ConfigureAwait(false)` yoksa) **klasik deadlock**. Koddaki "blocking is safe here" yorumu yalnızca çağıranın main thread olduğunu söyler; continuation'ın main thread'e ihtiyacı sorununu çözmez.

### P1-7. "Non-generic reflection fallback tamamen kaldırıldı" iddiası yanlış
**Dosya:** `Runtime/Core/SignalBus.cs` — kök README hem İngilizce hem Türkçe bölümde bunu iddia eder; ancak non-generic `ExecuteCommand(CommandHandlerInfo, object signal)` + reflection tabanlı `InjectSignal` (isim konvansiyonu `_signal`/`signal` ile alan arama!, ≈ satır 1076–1135) hâlâ mevcut ve **recovery Fallback** ile **composite** yollarında aktif kullanılıyor. Fallback komutları non-generic `ICommand` olmak zorunda; sinyal payload'u reflection'la enjekte edilir.

### P1-8. `CreatePureContextAsync` eksik yaşam döngüsü
**Dosya:** `Runtime/Core/NexusRuntime.cs` (≈ satır 127–150): pure context yalnızca tek `IContextLifecycle`'ın `OnInitializeAsync/OnStartAsync`'ini çağırır; `InitializeReactiveModelsAsync` ve `InitializeServicesAsync` **hiç çağrılmaz** → `BindReactiveModel`/`BindService` pure context'te init edilmez. Root yoluyla davranış farkı (test/dedicated server senaryoları bozuk).

### P1-9. Çoklu lifecycle'da `OnDispose` kaybı
**Dosyalar:** `Runtime/Core/Root.cs` (`InitializeContext`, yorumda kabul edilmiş) + `Runtime/Core/Context.cs` (`Dispose`). Root birden çok `IContextLifecycle` bileşenini `BindInstance` ile üst üste yazar ve `_lifecycles` cache'iyle Init/Start'ı hepsi için çağırır; ama `Context.Dispose` yalnızca `Container.Resolve<IContextLifecycle>()` (son bind edilen) için `OnDispose` çağırır → diğerlerinin `OnDispose`'u asla çalışmaz.

### P1-10. `Context.Dispose` hiç yaratılmamış servisleri yaratıp dispose ediyor
**Dosya:** `Runtime/Core/Context.cs` (`Dispose`, ≈ satır 448–460):
```csharp
var service = Container.Resolve(serviceTypes[i]) as INexusService;  // lazy ise İLK KEZ burada yaratılır!
service?.OnDispose();
```
Hiç resolve edilmemiş servis dispose sırasında inşa edilir (ctor yan etkileri: GameObject yaratma, PlayerPrefs, event aboneliği) ve hemen `OnDispose` edilir. Not: Pratikte `InitializeServicesAsync` tüm servisleri resolve eder; ama init tamamlanmadan dispose edilen ya da pure-context (P1-8) senaryolarında bu yol tetiklenir.

### P1-11. BuildValidation canonical yolu göremiyor
**Dosya:** `Editor/Validation/BuildValidation.cs` (`ValidateHandlers`) yalnızca `[SignalHandler]`/`[CompositeSignalHandler]` attribute'larını tarar. `CANONICAL-PATTERNS.md` ise `BindCommand<>`'i **tek canonical yol** ilan eder → canonical (fluent) kayıtlar editor-time doğrulamadan tamamen kaçar; mixed-mode/priority çakışmaları ancak runtime'da `RegisterCommand` exception'ı olarak görülür.
**Ek hata:** Aynı metodda `bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(handler.CommandType)` — yalnızca `IAsyncCommand<T>` uygulayan komut sync sanılır, `ICommand<T>` kontrolüne sokulur ve **yanlış "Generic Command Violation" hatası** üretilir.

### P1-12. View binding'i kaçırma (deferred rebind yok)
**Dosya:** `Runtime/Lifecycle/ViewBinder.cs` → `View.OnEnable`: Root var ama `Context` henüz null ise sadece **error loglar ve vazgeçer**; view kalıcı olarak bağlanmamış kalır (log mesajı bile "may be missed" diye itiraf ediyor). Retry/pending-queue mekanizması yok. Ayrıca fallback `FindObjectsByType<Root>` her OnEnable'da çalışır (pahalı).

### P1-13. WindowManager UGUI'ye reflection'la erişiyor → IL2CPP stripping riski
**Dosya:** `Runtime/Services/UI/WindowManager.cs` satır 78, 86, 96, 122: `Type.GetType("UnityEngine.UI.CanvasScaler, UnityEngine.UI")` vb. — asmdef zaten `"references": ["UnityEngine.UI"]` içerdiği halde reflection kullanılmış. `Runtime/link.xml` **yalnızca** `com.nexus.core`'u korur (`preserve="all"` — binary boyutunu da şişiren kaba çözüm); UnityEngine.UI tipleri IL2CPP'de strip edilirse CanvasScaler/GraphicRaycaster **sessizce** eklenmez.

### P1-14. Composite trigger eksikleri
**Dosya:** `Runtime/Core/SignalBus.cs` (`ProcessCompositeTriggers` ve devamı):
- Tetikleyen sinyallerin **payload'ları composite komuta taşınmaz** (yalnızca non-generic `Execute()`; `InjectSignal(command, null)` bile çağrılmaz — `HandleCommandErrorWithDecision`'a signal `null` geçilir).
- Komut, **`_compositeLock` tutulurken** çalıştırılır → kullanıcı kodu kilit altında; komut içinden yeni sinyal fire edilirse uzun kilit/kilit altında reentrancy riski.
- Composite yürütmede **decorator zinciri atlanır** (`syncCmd.Execute()` doğrudan; normal yol `ExecuteWithDecorators`).
- Async composite `SafeAsyncRunner` ile **fire-and-forget** → tamamlanma garantisi/awaitability yok.
- `ExecuteCompositeCommandAsyncCore` retry döngüsünde aynı komut instance'ı **re-inject edilmeden** yeniden kullanılır.

### P1-15. HybridQueue livelock + statik pool sızıntısı
**Dosya:** `Runtime/Queue/HybridQueue.cs`
- `DrainThreadSafe`/`DrainNextFrame` sınırsız `while TryDequeue` — bir handler drain sırasında aynı kuyruğa tekrar enqueue ederse **aynı frame'de sonsuz döngü** (frame başına işlem limiti yok). Ayrıca `DrainNextFrame` drain sırasında eklenen "next frame" sinyallerini **aynı frame'de** işler (semantik ihlali).
- Drain sırasında `Fire` exception fırlatırsa (örn. P0-4 mismatch) drain **yarıda kesilir**; kalan sinyaller o frame işlenmez.
- `QueuedSignalPool<T>` statik ve sınırsız; `SignalBus.ClearStaticCaches`/`NexusRuntime.Reset` bunu **temizlemez** (yalnızca domain reload'da sıfırlanır).

### P1-16. `Resolve<ILoggerService>()` hata loglarken exception fırlatabilir (yaygın anti-pattern)
Logger kayıtlı değilse `Resolve` **throw** eder; `?.` yalnızca `CurrentContext` null'unu korur. findstr taramasıyla **~30 çağrı noktası** doğrulandı: `Root.cs` (4: OnValidate satır 94, Start timeout'ları 151/192, catch 228), `CommandPool.cs` (60), `EncryptedStorageService.cs` (205, 222, 278, 314), `GameSaveManager.cs` (86, 117), `SceneManagerExtensions.cs` (71, 99, 121), `GameStateMachine.cs` (56, 79, 91), `Recovery.cs` (116), `AdService.cs` (94, 108, 121), `AnalyticsService.cs` (23, 34, 39), `HapticService.cs` (60), `IapService.cs` (67, 98, 138), `ObjectPoolService.cs` (138), `SaveThrottler.cs` (111), `TickService.cs` (174, 197, 220 — **her tick exception yolunda!**).
Doğru desen zaten mevcut: `NexusRuntime.Logger => CurrentContext?.TryResolve<ILoggerService>()`. **Hata işleme sırasında ikinci exception gerçek hatayı maskeler.**

### P1-17. Root.Update çoklu-root'ta metrikleri çarpıtıyor
**Dosya:** `Runtime/Core/Root.cs` (`Update`/`LateUpdate`): her `Root.Update()` → `PerformanceMonitor.UpdateFrameMetrics()`; N root varsa frame metrikleri N kez güncellenir. `LateUpdate`'teki bellek/GC metrikleri de her root'ta tekrarlanır.

---

## 🟡 P2 — Orta/Düşük Öncelik

- **P2-1. Abonelik sırası LIFO:** `SignalBus.Subscribe`/`SubscribeAsync` yeni node'u **liste başına** ekler (`node.Next = head`) → handler'lar kayıt sırasının TERSİNE çalışır; belgelenmemiş determinizm sürprizi.
- **P2-2. `SecureObservableInt`** (`Runtime/Models/SecureObservableProperty.cs`): `UnityEngine.Random.Range` **main-thread-only** (başka thread'den set → exception); `_cryptoKey`/`_obscuredValue` çifti atomik okunmaz/yazılmaz (torn read → yanlış değer); `OnChanged` `List.Contains` O(n).
- **P2-3. `ObservableProperty<T>.Value` setter'ında reentrancy koruması yok** — handler içinden değer değiştirilirse sonsuz ping-pong olabilir; `_value` yazımı da kilitsiz (torn read riski referans olmayan büyük struct'larda).
- **P2-4. `CommandPool.WarnIfStateLeakRisk`** (`Runtime/Core/CommandPool.cs` satır 46–66): `s_stateLeakWarningIssued`'a **kilitsiz** `Contains/Add` (Clear kilitli — tutarsız); alan analizi non-primitive struct alanları `continue` ile atlar (onlar da state sızdırır); ayrıca P1-16 anti-pattern'ini kullanır.
- **P2-5. Paket hijyeni:** `Editor/bin/`, `Editor/obj/`, `.Temp/` (içinde `.NETStandard...AssemblyAttributes.cs` derleme artığı!) klasörleri `.meta` dosyalarıyla pakete commit'lenmiş — UPM paketinde derleme artıkları dağıtılıyor. `com.nexus.core.csproj` da pakette.
- **P2-6. README/package.json eski repo URL'leri:** Kök README kurulum bloğu (her iki dilde) `https://github.com/CevatAYDN/Pixel-Flow-Clone.git?path=GameContainer/...` verir; paket bu repoda (`gitlab.com/beehivegame/GameContainer`). `package.json` → `author.url` da aynı eski repoyu gösterir. `ADOPTION.md` da upstream olarak Pixel-Flow-Clone'a atıf yapar.
- **P2-7. `NexusDI.Bind` sessiz overwrite:** `_bindings[typeof(T)] = new Binding` mevcut binding'i sessizce ezer; eski singleton instance container dispose'a kadar `_resolvedSingletons`'ta yaşar (geç dispose / çift yaşam).
- **P2-8. Singleton dispose sırası belirsiz:** `_resolvedSingletons` bir `HashSet<object>` — ters-kayıt-sırası garantisi yok; `Context` yalnızca `BindService` servislerini ters sırayla dispose eder, diğer singleton'lar rastgele sırada.
- **P2-9. Metrics API'si:** `NexusRuntime.Metrics.TotalSignalsDispatched`/`TotalCommandsExecuted` **public mutable field** (dışarıdan sıfırlanabilir/bozulabilir); `RecordTrace`'te `s_traceIndex` int overflow'unda `if (idx < 0) idx = 0` → tüm negatif indeksler tek slota (0) yığılır.
- **P2-10. DOTS** (`Runtime/DOTS/NexusDOTSBridge.cs`): `NativeSignalQueue.Drain` main-thread kontrolü `ManagedThreadId == 1` varsayımına dayalı (güvenilmez); `DOTSSignalBridge<T>` generic MonoBehaviour — Unity `AddComponent` edemez/serialize edemez, kullanıcı her T için subclass yazmalı (belgelenmemiş). Ayrıca bkz. Ek Bulgu E-2 (dosya fiilen hiç derlenmiyor).
- **P2-11. Netcode** (`Runtime/Netcode/NetworkSignalBus.cs`): `INetworkSignal` "replicated and serialized over the network" der ama pakette **hiçbir transport/serialization implementasyonu yok**; `NetworkSignalBus.CustomDispatcher` (satır 141) public static mutable global — üstelik hiç kullanılmıyor (bkz. E-3). `ReplaySignals` sync `Fire` kullanır (P0-4 zinciri).
- **P2-12. `FireAsyncWithTimeout`:** timeout token'ı komutlara (`commandCt`) geçer ama **async subscription'lara geçmez** — `FireInternalAsync` Phase 2'de abonelere `_context.LifetimeToken` verir; abonelikler timeout'tan muaf kalır.
- **P2-13. `ExternalAdapter` çift injection** (`Runtime/Core/NexusDI.cs` satır 383–385): adapter varsa `ExternalAdapter.Inject(instance)` çağrılır ve ardından Nexus'un kendi injection'ı da **aynı instance'a** uygulanır (return yok) → çift/çakışan injection.
- **P2-14. EncryptedStorage:** focus kaybında (`OnFocusChanged` → `Save()`) main thread'de kilit altında **senkron toplu dosya yazımı** (hitch); `GetFilePath` her çağrıda `MD5.Create()` (allocation + IDisposable churn); `HasKey` tamper edilmiş dosya için `File.Exists` → **true** döner (GetString ise default döndürür — tutarsız API).
- **P2-15. TickService:** `TimeScale` global `Time.timeScale`'i sarar → çok context'li kurulumda çakışma; her TickService instance'ı kendi `[Nexus_TickDriver]` `DontDestroyOnLoad` GameObject'ini yaratır (aynı isimle çoklanır). Ek: `[assembly: InternalsVisibleTo("com.nexus.core.tests")]` attribute'u bu servis dosyasının içine gömülü (hijyen).
- **P2-16. Root sibling bekleme (düzeltilmiş bulgu):** Kodda aynı `InitializationPriority` için **isim tabanlı tie-break mevcut** (`string.Compare(..., Ordinal) < 0`); ancak **aynı GameObject adı** için tie-break yok (ikisi de beklemez → sıra nondeterministik). Frame timeout'u `Task.Yield() ≈ 1 frame` varsayımına dayanır (garanti değildir).
- **P2-17. `ContextBuilder.BindCommand` doc'u:** `mode` için "Sequential, Concurrent, Exclusive, **CompositeTrigger**" der — enum üyesi `Composite`'tır (aynı hata `CommandHandlerInfo.Mode` doc'unda da var). Ayrıca `ExecutionMode.Composite`'ı `BindCommand`'a geçirmek doğrulanmaz: composite state oluşturulmaz, sinyal normal sequential gibi işlenir (tanımsız/yanıltıcı yol).

---

## ➕ Ek Bulgular (bu incelemede tespit edilen yeni sorunlar)

- **E-1. `NexusRuntime.UnregisterContext` cache'i kirletmiyor:** `RegisterContext` `s_activeContextsCacheDirty = true` atar; `UnregisterContext` **atamaz** → `ActiveContexts` snapshot'ı bir sonraki Register'a kadar **dispose edilmiş context'i içermeye devam eder**. Cross-context broadcast (`BroadcastCrossContext`) bu listeyi kullanır → ölü/dispose edilmiş context'e sinyal gönderilebilir. (`Runtime/Core/NexusRuntime.cs`)
- **E-2. DOTS köprüsü fiilen ölü kod:** `NexusDOTSBridge.cs` tamamı `#if UNITY_COLLECTIONS` altında; ancak asmdef'te `com.unity.collections` → `UNITY_COLLECTIONS` versionDefine **yok** (tek versionDefine NEXUS_DEBUG hilesi). Kullanıcı define'ı elle eklemedikçe DOTS desteği hiç derlenmez — CHANGELOG'daki "DOTS Bridge" özelliği fiilen kapalı.
- **E-3. `NetworkSignalBus.CustomDispatcher` hiç kullanılmıyor:** paket genelinde tek referans tanımın kendisi (satır 141). "Code-generated dispatcher to bypass reflection during rollback" iddiası ölü API.
- **E-4. BuildValidation false-positive:** `ValidateHandlers`'daki generic kontrol `typeof(IAsyncCommand).IsAssignableFrom` ile sync/async ayrımı yapar → yalnızca `IAsyncCommand<T>` uygulayan komutlara **yanlış** "Generic Command Violation" hatası verir (P1-11 altında da belirtildi).
- **E-5. WindowManager açılış yarışı:** `OpenWindowAsync` "already open" kontrolünden sonra kilidi bırakıp instantiate eder; iki eşzamanlı çağrı **aynı pencereyi iki kez instantiate** edebilir — yalnızca biri `_activeWindows`'a yazılır, diğeri sahnede sahipsiz kalır. Ayrıca `IsWindowOpen`/`GetWindow` kilitsiz erişir; `[Nexus_UICanvas]` `GameObject.Find` ile bulunur (çok context'te paylaşılan tekil global).
- **E-6. Composite attribute keşfinde async sınıflandırma hatası:** `Context.ScanAssembliesAndRegister` composite için `typeof(IAsyncCommand).IsAssignableFrom(type)` kullanır → yalnızca `IAsyncCommand<T>` uygulayan composite sync kaydedilir (P0-1'in composite ayağı).
- **E-7. `HandleCommandErrorWithDecision`'da Fallback için `CommandHandlerInfo` her hatada `new` ile yaratılır** (hata yolu allocation'ı — küçük ama "0 GC" iddiasıyla tutarsız).
- **E-8. `Root.Start` `async void`:** Unity yaşam döngüsünde kaçınılmaz olsa da, exception'lar yalnızca iç try/catch ile yakalanır; `Task.Yield` tabanlı frame sayımı editor pause/timescale'den etkilenir.
- **E-9. Kök dizinde `tatus` dosyası:** içeriği bir `git log` çıktısı (yanlış yazılmış komut yönlendirmesi). Repo hijyeni; silinmeli, `.gitignore`'a gerek yok.
- **E-10. `ObservableList<T>` hiçbir thread-safety/reentrancy koruması içermez** ve `_onAdded` vb. event'ler mutasyon sırasında senkron çağrılır (handler içinden listeye müdahale → tanımsız davranış).
- **E-11. `NetworkSignalHistory.ReplaySignals` O(n) tarama:** her tick için tüm liste taranır; uzun geçmişte rollback maliyeti O(tick_aralığı × toplam_sinyal).
- **E-12. `Context.Configure` içinde `Activator.CreateInstance` ile lifecycle yaratılırken DI injection yapılmaz** (`[Inject]` alanları null kalır; kullanıcı ancak `OnConfigure`'da builder üzerinden erişebilir — belgelenmemiş kısıt).

---

## 🧹 Hijyen Bulguları (özet)

| Konu | Durum |
|---|---|
| Kök `tatus` dosyası | `git log` çıktısı; silinmeli (E-9) |
| `Editor/bin/`, `Editor/obj/`, `.Temp/` | .meta'larıyla pakete commit'li; UPM'de derleme artığı dağıtımı (P2-5) |
| `com.nexus.core.csproj` pakette | IDE artığı; `Samples~` gibi dışlanmalı |
| README kurulum URL'i + `package.json` author.url | Eski GitHub reposu (Pixel-Flow-Clone) — GitLab reposuyla güncellenmeli (P2-6) |
| `link.xml` `preserve="all"` | Kaba; binary şişirir; UnityEngine.UI korunmuyor (P1-13) |
| `InternalsVisibleTo` TickService.cs içinde | AssemblyInfo.cs'e taşınmalı (P2-15 notu) |

---

## 🗺️ Öncelik Sıralı Düzeltme Yol Haritası

1. **P0-1 (+E-6):** `Context.ScanAssembliesAndRegister` (≈ satır 255–266) dallarını generic arayüz kontrolüyle genişlet; eşleşmeyen tip için `LogError`. Composite kayıtta `isAsync` tespitini `ImplementsGenericInterface(type, typeof(IAsyncCommand<>))` ile yap. Aynı düzeltmeyi `BuildValidation.ValidateHandlers`'a da uygula (E-4).
2. **P0-2 + P0-3:** asmdef'ten `versionDefines` NEXUS_DEBUG hilesini kaldır; `RecordTrace($"▶ ...")` (SignalBus ≈ 442) ve `RecordTrace($"  └ ...")` (≈ 816) çağrılarını `#if NEXUS_DEBUG`'a al ya da string üretmeyen forma çevir; `ExecuteWithDecorators` closure'ını decorator yokken bypass et; `CommandHandlers` property'lerini cache'le.
3. **P0-5:** `RegisterCommand`'da `CommandTimeoutAttribute` oku → `CommandHandlerInfo.TimeoutMs`; `ExecuteCommandAsync`'te `CreateLinkedTokenSource(ct)` + `CancelAfter(TimeoutMs)` — ya da attribute'u ve sample/STABILITY dokümantasyonunu kaldır.
4. **P0-6:** `EncryptedStorageService` (satır 88–91) anahtarı 32 bayta çıkar (HMAC için ayrı türetim) veya tüm "AES-256" metinlerini düzelt; kayıt formatı için sürüm/migrasyon planla.
5. **P0-7:** SignalBus satır ~470 ve ~583'teki `s_stackDepth.Value = 0` satırlarını kaldır.
6. **P0-8:** `Root.ClearRegistry` → `lock (s_rootLock)`; bayrak atamasını kilit içine al.
7. **P0-4:** `HybridQueue` drain'ine ve `Fire(failedSignal)` yollarına async-uyumlu köprü (`FireAsyncAndForget`) ekle; `FireInternalAsyncFromSync` (satır 612) ölü kodunu kaldır ya da köprü olarak bağla; `NetworkSignalHistory.ReplaySignals` için de aynı karar.
8. **P1-1 + P2-17:** Üç dosyadaki "lower values run first" doc'larını "higher priority runs first" olarak; "CompositeTrigger" atıflarını "Composite" olarak düzelt; `BindCommand`'a `ExecutionMode.Composite` geçilirse throw et.
9. **P1-16:** ~30 `CurrentContext?.Resolve<ILoggerService>()` çağrısını `NexusRuntime.Logger` ile değiştir (mekanik, düşük riskli, yüksek getirili).
10. **P1-8/9/10:** `CreatePureContextAsync`'e `InitializeReactiveModelsAsync` + `InitializeServicesAsync` ekle; `Context` configure edilen tüm lifecycle listesini saklasın ve `Dispose`'da hepsinin `OnDispose`'unu çağırsın; servis dispose'unda yalnızca **halihazırda instantiate edilmiş** singleton'ları dispose et (binding.Instance null kontrolü).
11. **P1-2 + E-1:** `RegisterCommand`/`RegisterCompositeCommand` yazımlarını kilit altına al; `_subscriptions` okumaları için volatile snapshot deseni; `UnregisterContext`'e `s_activeContextsCacheDirty = true` ekle.
12. **Kalan P1/P2:** P1-3 (`throw ex;` → `ExceptionDispatchInfo`), P1-14 (composite: kilit dışında yürütme + decorator + payload), P1-15 (drain'e frame limiti + `QueuedSignalPool<T>` reset kaydı), P1-12 (ViewBinder pending-rebind kuyruğu), P1-13 (WindowManager'da doğrudan `UnityEngine.UI` tipleri + link.xml genişletme), E-2 (`com.unity.collections` versionDefine), E-3/E-5 (ölü API temizliği, OpenWindowAsync kilit kapsamı), P2-5/P2-6/E-9 (paket ve repo hijyeni).

---

*Not: Bu rapor `Nexus_Eksiklik_Raporu.md`'nin yerini almaz; onu tamamlar. Oradaki P0-A/B, P1-A/B/C, P2-A→D maddeleri ve v0.3.2 execution-order düzeltmesi (Commands → Subscriptions sırası) kodda doğrulanmıştır; yukarıdaki bulguların tamamı o raporda yer almayan yeni tespitlerdir.*
