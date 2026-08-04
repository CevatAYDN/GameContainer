# Nexus — Boot & Frame Spike Refaktör Planı

> Repo okunarak, sınıf/satır seviyesinde çıkarıldı. **Burst değil, refaktör** — tüm değişiklikler C# tarafında.
> Her madde: **Sınıf**, **Sorun**, **Neden**, **Çözüm**, **Beklenen kazanç**.

---

## Önce Büyük Resim

Boot yolun:
```
Root.Awake
  → EnsureSupportComponents (QueueDrainer, MetricsSampler)
  → InitializeContext
    → new Context()
      → new NexusDI()
      → new CommandPoolManager
      → new SignalBus (içinde CommandRegistry + SubscriptionRegistry + CommandExecutor + RecoveryEngine)
      → new HybridQueue
      → new ViewBinder
    → Context.Configure
      → FindLifecycleTypeByConvention  ← TÜM assembly'leri tarıyor
      → ScanAssembliesAndRegister       ← TÜM type'larda reflection
      → _builder.Validate()             ← HER type için HER field/property/method reflection
  → Start (async)
    → await parent + sibling initialize
    → InitializeLifecycleAsync
      → InitializeReactiveModelsAsync
      → InitializeServicesAsync         ← TÜM eager service'ler instantiate + InitializeAsync
      → lifecycles.OnInitializeAsync
      → lifecycles.OnStartAsync
      → ExecuteStartableLifecyclesAsync
```

**Toplamda her servis için 5–10 kez Inject + reflection cache lookup**, komut sayısı kadar `RegisterCommand` çağrılıyor (her biri snapshot yeniden inşa ediyor), ve validation tüm type'ları yeniden geziyor.

---

## BÖLÜM 1 — AÇILIŞ (Boot) Refaktörleri

### 1.1 [KRİTİK] `CommandRegistry.RegisterCommand` her çağrıda snapshot'u baştan inşa ediyor

**Dosya:** `Runtime/Core/CommandRegistry.cs`, satır 99–149 (özellikle 145–148)

**Sorun:** Her `RegisterCommand` çağrısında `_commandHandlersReadCopy` ve `_hasAsyncHandlerReadCopy` **tüm sözlüğü** kopyalıyor:
```csharp
_commandHandlersReadCopy = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
foreach (var kvp in _commandHandlers)
    _commandHandlersReadCopy[kvp.Key] = new List<CommandHandlerInfo>(kvp.Value);
_hasAsyncHandlerReadCopy = new Dictionary<Type, bool>(_hasAsyncHandler);
```

Eğer 50 komut kayıtlıysa → 50 * 50 = **2500 allocation** boot sırasında. Üstelik her `list` için `new List<>(kvp.Value)` ayrıca kopya.

**Çözüm:** Sadece değişen `signalType` için incremental güncelleme yap, tüm dict'i yeniden kurma. Liste zaten aynı referansı paylaşabilir çünkü `RegisterCommand` zaten `list.Add` ile mutasyona uğruyor (zaten bu yüzden `volatile read copy` pattern'i var).

```csharp
// ESKİ (her çağrıda O(N)):
_commandHandlersReadCopy = new Dictionary<...>();
foreach (var kvp in _commandHandlers)
    _commandHandlersReadCopy[kvp.Key] = new List<...>(kvp.Value);

// YENİ — sadece değişen key için snapshot güncelle:
if (_commandHandlers.TryGetValue(signalType, out var updatedList))
{
    // Read-copy: list'in snapshot'unu immutable tutmak için kopyala
    // AMA sadece bu key için, tüm dict değil
    if (_commandHandlersReadCopy.TryGetValue(signalType, out var existing))
    {
        var newSnap = new List<CommandHandlerInfo>(updatedList);
        _commandHandlersReadCopy[signalType] = newSnap;
        // Eğer önce yoktu, eklemek için:
        // _commandHandlersReadCopy[signalType] = newSnap;  // ConcurrentDict değilse OK
    }
    else
    {
        _commandHandlersReadCopy[signalType] = new List<CommandHandlerInfo>(updatedList);
    }
}
_hasAsyncHandler[signalType] = isAsync; // tek entry güncelleme
_hasAsyncHandlerReadCopy = _hasAsyncHandler; // artık ref paylaşabilir çünkü mutable
```

**Beklenen kazanç:** 50 komutta ~50x daha az allocation, snapshot yeniden inşa süresi O(1) yerine O(N).

---

### 1.2 [KRİTİK] `FindLifecycleTypeByConvention` her Context için tüm assembly'leri tarıyor

**Dosya:** `Runtime/Core/Context.cs`, satır 403–429

**Sorun:**
```csharp
private static Type FindLifecycleTypeInAssemblies(List<Assembly> assemblies, string scopeTag)
{
    string targetName1 = $"{scopeTag}Lifecycle";
    string targetName2 = $"{scopeTag}ContextLifecycle";
    foreach (var assembly in assemblies)
    {
        foreach (var type in Services.AssemblyScanService.GetCachedTypes(assembly))
        {
            if (type.IsClass && !type.IsAbstract && typeof(IContextLifecycle).IsAssignableFrom(type))
            {
                if (string.Equals(type.Name, targetName1, ...) ||
                    string.Equals(type.Name, targetName2, ...))
                    return type;
            }
        }
    }
}
```

Her Context başlatıldığında tüm type'lar `IsAssignableFrom` testinden geçiyor. `IsAssignableFrom` ilk seferde pahalı (interface walk yapıyor).

**Çözüm:**
1. Lifecycle type'ı için **dedicated cache** ekle: `ConcurrentDictionary<string, Type> s_lifecycleTypeCache` (key = scopeTag)
2. İlk aramada cache'le, sonrakilerde O(1) lookup
3. `IContextLifecycle.IsAssignableFrom` yerine `type.GetInterfaces().Any(i => i == typeof(IContextLifecycle))` + cached lookup

```csharp
private static readonly ConcurrentDictionary<string, Type> s_lifecycleTypeCache = new();

private Type FindLifecycleTypeByConvention()
{
    if (string.IsNullOrEmpty(ScopeTag)) return null;
    var key = ScopeTag;
    if (s_lifecycleTypeCache.TryGetValue(key, out var cached)) return cached; // null cache'lenebilir

    var assemblies = ...;
    var found = FindLifecycleTypeInAssemblies(assemblies, key);
    s_lifecycleTypeCache[key] = found; // null de OK
    return found;
}
```

**Beklenen kazanç:** Birden fazla context varsa, ilk açılıştan sonra **her context için 100-1000 type reflection tasarrufu**.

---

### 1.3 [KRİTİK] `ContextBuilder.Validate()` tüm metadata'yı her seferinde yeniden resolve ediyor

**Dosya:** `Runtime/Core/ContextBuilder.cs`, satır 340–491

**Sorun:** `Validate()` her `type` için `GetOrCreateInjectMetadata` çağırıyor — bu **kendi başına iyi** (cache'li), ama `meta.ConstructorParameterTypes`, `meta.Fields`, `meta.Properties`, `meta.Methods` üzerinde **doğrusal arama** yapıyor. `allRegisteredTypes.Contains(...)` çağrıları her seferinde HashSet lookup ama field/property enumeration reflection yapıyor.

Daha önemlisi: `Validate()` her startup'ta çalışıyor (release build dahil) ve **A8 fix**'ten sonra zorunlu. Validate sonuçları cache'lenmiyor.

**Çözüm:** Validate sonuçlarını `ContextData` veya `Context` üzerinde **immutable cache**'le. ContextData aynıysa tekrar çalıştırma:

```csharp
// ContextBuilder'a ekle:
private int _validationHash;
private List<DiValidationIssue> _cachedValidation;

public List<DiValidationIssue> Validate()
{
    int currentHash = ComputeBindingHash(); // basit hash: binding sayısı + her binding'in type hash'i
    if (_cachedValidation != null && _validationHash == currentHash) 
        return _cachedValidation;
    
    var issues = ComputeValidation();
    _cachedValidation = issues;
    _validationHash = currentHash;
    return issues;
}
```

**Alternatif (daha az iş):** `ValidateOnStartup` default'unu `false` yap, sadece `ContextData.EnableStartupValidation = true` ise çalıştır. Validate, geliştirme zamanı kontrolü — release'de yavaşlatmamalı.

**Beklenen kazanç:** Release build'de 100-300ms (50+ type varsa). Dev build'de 50-100ms.

---

### 1.4 [ÖNEMLİ] `GetDefaultScanAssemblies` her Context'te null check yapıyor ama pattern'de her çağrı yeni liste kurabiliyor

**Dosya:** `Runtime/Core/Context.cs`, satır 516–551

**Sorun:** `s_defaultScanAssemblies` cache'li ama `assembly.GetReferencedAssemblies()` her assembly için **linq iterasyonu**. İlk seferde maliyetli ama sonra cache'leniyor. **Asıl sorun**: birden fazla Root varsa, **her Root kendi InitializeContext'inde** `ScanAssembliesAndRegister` çağırıyor — ama static cache var, O(1). İyi.

Daha büyük sorun: `BindInterfacesAndSelfTo` içindeki `GetUserDefinedInterfaces` her tür için `type.GetInterfaces()` çağırıyor + tüm interface'lerde namespace kontrolü yapıyor.

**Çözüm:** `GetUserDefinedInterfaces` sonucunu `MetadataCache`'e benzer şekilde cache'le:

```csharp
// ContextBuilder.cs:
private static readonly ConcurrentDictionary<Type, Type[]> s_userInterfacesCache = new();

private static Type[] GetUserDefinedInterfaces(Type type)
{
    return s_userInterfacesCache.GetOrAdd(type, t =>
    {
        var allInterfaces = t.GetInterfaces();
        var result = new List<Type>(allInterfaces.Length);
        for (int i = 0; i < allInterfaces.Length; i++)
        {
            // ... mevcut filter logic
        }
        return result.ToArray();
    });
}
```

**Beklenen kazanç:** `BindInterfacesAndSelfTo` çağrılarında %50-80 hızlanma (interface sayısına bağlı).

---

### 1.5 [ÖNEMLİ] Eager service binding — tüm servisler boot'ta instantiate

**Dosya:** `Runtime/Core/ContextBuilder.cs`, satır 197–218; **tüm Lifecycle sınıfları (örnek projedeki)**

**Sorun:** `BindService<T>()` her zaman `_serviceTypes`'a ekliyor → `InitializeServicesAsync` hepsini resolve + `InitializeAsync` çağırıyor. 13 servis varsa (README'ye göre), 13 injection + 13 async init boot sırasında.

**Çözüm:** README'de `BindLazyService` **zaten var**. Lifecycle'ı yazarken her servisi "sadece gerçekten ihtiyaç varsa eager" yap:

```csharp
// ESKİ:
builder.BindService<AnalyticsService>();
builder.BindService<AdService>();
builder.BindService<AudioService>();
// ...

// YENİ:
builder.BindLazyService<AnalyticsService>();   // sadece oyuncu bir event tracklediğinde
builder.BindService<AudioService>();            // erken lazım, prewarm gerekli
builder.BindService<TickService>();             // frame update için erken lazım
// ...
```

**Önce kontrol et:** Hangi servis gerçekten `OnStart` öncesi lazım? Çoğu muhtemelen değil. Sadece `TickService`, `AudioService` (prewarm için), `WindowManager` (ilk pencere lazım), `ObjectPoolService` (prewarm için) → bunlar eager. Diğerleri lazy.

**Beklenen kazanç:** 13 → 4 eager service = **%60-70 daha az injection** + **%60-70 daha az async init work** boot'ta.

---

### 1.6 [ORTA] `ScanAssembliesAndRegister` her type için 4 ayrı `GetCustomAttribute` çağrısı

**Dosya:** `Runtime/Core/Context.cs`, satır 431–500

**Sorun:** Her type için 4 reflection call:
```csharp
var handlerAttrs = type.GetCustomAttributes<SignalHandlerAttribute>(); // 1
var regCmdAttrs = type.GetCustomAttributes<RegisterCommandAttribute>(); // 2
var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>(); // 3
var regCompositeAttr = type.GetCustomAttribute<RegisterCompositeCommandAttribute>(); // 4
```

`GetCustomAttributes` her çağrıda attribute cache'ini lookup ediyor, ama yine de method call overhead'i var. 4 * N types = 4N reflection call.

**Çözüm:** Tek seferde `GetCustomAttributes(typeof(Attribute), true)` ile **tüm attribute'leri** al, sonra `is` ile type check yap. Veya daha iyi: `Attribute.IsDefined` ile boolean check + sadece defined olanlar için `GetCustomAttribute`:

```csharp
// YENİ:
var allAttrs = type.GetCustomAttributes(typeof(Attribute), inherit: true);
List<SignalHandlerAttribute> handlers = null;
CompositeSignalHandlerAttribute composite = null;
foreach (var attr in allAttrs)
{
    if (attr is SignalHandlerAttribute sha) (handlers ??= new List<...>()).Add(sha);
    else if (attr is RegisterCommandAttribute rca) (handlers ??= new List<...>()).Add(new SignalHandlerAttribute(rca.SignalType) { Mode = rca.Mode, Priority = rca.Priority });
    else if (attr is CompositeSignalHandlerAttribute csa) composite = csa;
    else if (attr is RegisterCompositeCommandAttribute rcca) composite = new CompositeSignalHandlerAttribute(rcca.SignalTypes) { OneShot = rcca.OneShot, Priority = rcca.Priority };
}
```

**Ama dikkat:** Reflection amacı bu — performans kritikse **source generator** daha iyi çözüm. IL post-processing ile tüm `[SignalHandler]`'ları bir registry'ye yazdırırsın, scan'a gerek kalmaz.

**Beklenen kazanç:** %30-50 daha hızlı scan (ama sonuçlar cache'lendiği için sadece ilk açılışta).

---

### 1.7 [ORTA] `EnsureSupportComponents` her Root'ta GetComponent → AddComponent

**Dosya:** `Runtime/Core/Root.cs`, satır 179–194

**Sorun:**
```csharp
if (GetComponent<QueueDrainer>() == null)
    gameObject.AddComponent<QueueDrainer>();
if (GetComponent<MetricsSampler>() == null)
    gameObject.AddComponent<MetricsSampler>();
```

`GetComponent` reflection tabanlı, maliyetli değil ama her Root'ta 2x call. Birden fazla Root varsa 2N call.

**Çözüm:** `gameObject.TryGetComponent(out _)` (Unity 6 API, daha hızlı) + `[DisallowMultipleComponent]` attribute ekle. Veya `GetComponent<T>()` yerine cached `MonoBehaviour[] _supportComponents` field:

```csharp
private QueueDrainer _queueDrainer;
private MetricsSampler _metricsSampler;

private void EnsureSupportComponents()
{
    if (_queueDrainer == null) 
        _queueDrainer = gameObject.AddComponent<QueueDrainer>();
    if (_metricsSampler == null) 
        _metricsSampler = gameObject.AddComponent<MetricsSampler>();
}
```

**Beklenen kazanç:** Marjinal ama 0 allocation guarantee.

---

### 1.8 [ORTA] `OnEnable` her seferinde `s_allRoots.Contains` linear search

**Dosya:** `Runtime/Core/Root.cs`, satır 86–95

**Sorun:** `s_allRoots.Contains(this)` — her Root aktif olduğunda O(N) arama. N = Root sayısı.

**Çözüm:** `HashSet<Root>`'a çevir. `List.Contains` O(N), `HashSet.Contains` O(1).

```csharp
private static readonly HashSet<Root> s_allRoots = new();
private static readonly object s_rootLock = new();
```

**Beklenen kazanç:** Marjinal (çok Root yok) ama allocation-free.

---

### 1.9 [ORTA] `RegisterPendingView` `Contains` linear search

**Dosya:** `Runtime/Core/Root.cs`, satır 62–75

**Sorun:** `_pendingViews.Contains(view)` — O(N) her view register'da.

**Çözüm:** `HashSet<IView> _pendingViewsSet` ekle, list paralel tutulsun.

---

## BÖLÜM 2 — FPS DROP (Frame Spike) Refaktörleri

### 2.1 [KRİTİK] `ProcessCompositeTriggers` her fire'da çalışıyor

**Dosya:** `Runtime/Core/SignalBus.cs`, satır 808–881

**Sorun:** Her `Fire()` çağrısında `ProcessCompositeTriggers` çağrılıyor, o da `TryGetCompositeTriggers` ile dictionary lookup yapıyor. Composite trigger yoksa **erken return** ama dictionary lookup maliyeti var.

**Çözüm:** Eğer hiç composite trigger kayıtlı değilse, **bus seviyesinde** skip et:

```csharp
// SignalBus'a ekle:
private volatile bool _hasAnyCompositeTriggers = false;

// CommandRegistry.RegisterCompositeCommand içinde set et:
// _hasAnyCompositeTriggers = true;

// FireInternal'da:
if (_hasAnyCompositeTriggers) 
    ProcessCompositeTriggers(signal);
```

**Beklenen kazanç:** Composite trigger kullanılmıyorsa, **her Fire() için 1 dictionary lookup + 1 method call** tasarrufu. Binlerce fire/frame'de 1-2ms kazanç.

---

### 2.2 [KRİTİK] İlk Fire() anlık spike — singleton'lara ilk erişim

**Dosya:** Çeşitli

**Sorun:** İlk `Fire(new XxxSignal())`:
- `RecordSignalDispatched()` — `NexusRuntime.Metrics` first access
- `RecordTrace` — `NexusRuntime.Metrics` first access
- `_commandRegistry.HasAsyncCommandHandlers(type)` — dictionary lookup
- `_subscriptionRegistry.HasAsyncSubscriptions(type)` — dictionary lookup
- Her method ilk çağrıda JIT compile

İlk frame'de onlarca sinyal fire ediliyorsa → onlarca method JIT cost birikmesi.

**Çözüm:** Root initialization'dan sonra, **warmup pass** ekle:

```csharp
// Root.Start sonunda veya Context.OnStart'tan sonra:
public void WarmupSignals()
{
    // Tüm bilinen signal type'ları için boş fire yap
    var signalTypes = GetKnownSignalTypes(); // veya SignalTraceLabel<> reflection
    foreach (var sigType in signalTypes)
    {
        // Generic fire — reflection ile:
        var fireMethod = typeof(SignalBus).GetMethod("Fire", ...).MakeGenericMethod(sigType);
        fireMethod.Invoke(this, new object[] { Activator.CreateInstance(sigType) });
    }
}
```

Veya statik list: `s_warmupSignals` → bilinen tüm signal type'ları pre-fire.

**Beklenen kazanç:** İlk frame 10-50ms → 1-3ms. Oyuncu bunu **kayda değer gecikme** olarak hissetmez.

---

### 2.3 [KRİTİK] `ViewBinder.RegisterView` reflection her view için

**Dosya:** `Runtime/Lifecycle/ViewBinder.cs`, satır 155

**Sorun:**
```csharp
var mediatorAttr = view.GetType().GetCustomAttribute<MediatorAttribute>();
```

`GetCustomAttribute` her view açılışında. İlk frame'de onlarca view varsa → spike.

**Çözüm:** View Type → MediatorType cache'i:

```csharp
private static readonly ConcurrentDictionary<Type, MediatorAttribute> s_mediatorAttrCache = new();

private static MediatorAttribute GetMediatorAttrCached(Type viewType)
{
    return s_mediatorAttrCache.GetOrAdd(viewType, t => t.GetCustomAttribute<MediatorAttribute>());
}
```

**Beklenen kazanç:** View açılışlarında %30-50 hızlanma. İlk frame'de 5-20ms tasarruf.

---

### 2.4 [KRİTİK] `Mediator.Reset()` çift çağrılıyor (bilinen bug)

**Dosya:** `Runtime/Lifecycle/Mediator.cs`, satır 137–151; **`ViewBinder.cs` satır 260–266 ve 317**

**Sorun:** `OnReset` override eden her mediator **her pool pop'ta 2 kez** çağrılıyor:
- `ViewBinder.GetMediator` (pop path): `resettable.Reset()` (satır 262)
- `NexusDI.ClearInjectedReferences` (return path): `resettable.Reset()` (Clearer, satır 652)

**Çözüm:** `IResettable`'a "already reset" flag ekle veya bir path'i kaldır. En basit: `ViewBinder.GetMediator`'daki çağrıyı kaldır (zaten `ClearInjectedReferences` return'de çağırıyor):

```csharp
// ViewBinder.cs GetMediator:
if (mediator is IResettable resettable)
{
    // Reset() is already invoked on return-to-pool by Clearer.ClearInjectedReferences.
    // Do NOT reset on pop — the cleared instance is clean. Resetting twice was a known
    // double-invocation that hurt hot-path performance.
    _poolResetCount++;
}
```

veya `IResettable.Reset` default implementation'da no-op yap, derived class'lar override etmesin (sadece `OnReset` kalsın).

**Beklenen kazanç:** Pool pop'larında %20-40 hızlanma. Oyun açılışı sırasında UI pencereleri açılıp kapanırken fark edilir.

---

### 2.5 [ÖNEMLİ] `RecordSignalDispatched` ve `RecordTrace` her Fire'da

**Dosya:** `Runtime/Core/SignalBus.cs`, satır 384–385

**Sorun:** Her fire'da 2 method call + metrics update. Binlerce fire/frame'de 0.5-1ms.

**Çözüm:** `NEXUS_DEBUG` veya `#if DEVELOPMENT_BUILD` ile wrap'le. Production'da bu çağrılar gereksiz:

```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    NexusRuntime.Metrics.RecordSignalDispatched();
    NexusRuntime.Metrics.RecordTrace(SignalTraceLabel<T>.Fire);
#endif
```

**Beklenen kazanç:** Binlerce fire/frame'de 0.3-1ms.

---

### 2.6 [ÖNEMLİ] `OnUnhandledException` event her fire'da null check

**Dosya:** `Runtime/Core/SignalBus.cs`, satır 89–94

**Sorun:** `OnUnhandledException?.Invoke(...)` — null-conditional + delegate allocation eğer event varsa.

**Çözüm:** Static field kontrolü ile erken return. Zaten bu pattern diğer yerlerde var.

---

### 2.7 [ORTA] `SubscriptionRegistry.EnterDispatch` / `ExitDispatch` her fire

**Dosya:** `Runtime/Core/SignalBus.cs`, satır 425, 531

**Sorun:** Reentrancy guard için EnterDispatch/ExitDispatch çağrıları. Eğer subscription dispatch sırasında değişmiyorsa, bu maliyet gereksiz.

**Çözüm:** Eğer subscription read-copy boş ise (re-entrancy mümkün değil), skip et.

---

### 2.8 [ORTA] `BroadcastCrossContext` her fire'da `GetActiveContexts` çağırıyor

**Dosya:** `Runtime/Core/SignalBus.cs`, satır 466–471 ve 883–927

**Sorun:** Her fire'da `_commandRegistry.GetCachedCrossContext(type)` → attribute cache lookup. Cross-context kullanılmıyorsa bu her fire'da boşa dönüyor.

**Çözüm:** Cross-context attribute yoksa erken return — ama bu zaten attribute null check'i ile yapılıyor. Sorun: `GetCachedCrossContext` her seferinde dictionary lookup yapıyor.

```csharp
// CommandRegistry'e ekle:
private readonly ConcurrentDictionary<Type, CrossContextAttribute> s_crossContextCache;
// Bu zaten var (satır 40) — ama static. Her CommandRegistry instance'ı kendi cache'ine sahip olabilir
// Veya sadece type başına bir kez lookup yap ve result null ise signal için "no cross-context" set et:
private readonly ConcurrentDictionary<Type, bool> _noCrossContextCache = new();

// FireInternal'da:
if (!isCrossContextSource && !_noCrossContextCache.ContainsKey(type))
{
    var attr = _commandRegistry.GetCachedCrossContext(type);
    if (attr != null)
    {
        BroadcastCrossContext(signal, attr.ScopeTag);
    }
    else
    {
        _noCrossContextCache[type] = true; // bir daha bakma
    }
}
```

**Beklenen kazanç:** Cross-context kullanılmıyorsa, her fire'da 1 dictionary lookup tasarrufu.

---

## BÖLÜM 3 — DERİN OPTİMİZASYONLAR (Uzun vadeli)

### 3.1 Source Generator ekle — AssemblyScan'ı bypass et

`Nexus.SourceGen` adında Roslyn source generator. `[SignalHandler]`, `[Mediator]`, `[Inject]` gibi attribute'ları **derleme zamanında** tara ve bir `Registry.g.cs` dosyası üret. Bu sayede:
- `ScanAssembliesAndRegister` çalışmaz
- `GetCustomAttribute` reflection yok
- `MetadataCache.GetOrCreateInjectMetadata` reflection yok
- `[Inject]` setter'ları compile-time Expression tree yerine direkt generated delegate

**Başlangıç noktası:** `s_assemblyScanCache` zaten var → bunu **derleme zamanında** üretilen bir `static readonly` array'a çevir.

### 3.2 Command registry immutable'a taşı

Eğer tüm command'lar boot sırasında register oluyorsa, snapshot pattern gereksiz. Startup'tan sonra komut ekleme yoksa, **immutable** yap:

```csharp
public sealed class CommandRegistry
{
    private readonly Dictionary<Type, IReadOnlyList<CommandHandlerInfo>> _handlers; // immutable
    private bool _sealed;
    
    public void Seal() { _sealed = true; } // Start sonrası çağrılır
    public void RegisterCommand(...) 
    { 
        if (_sealed) throw ...; 
        // ...
    }
}
```

Bu sayede:
- Snapshot rebuild yok
- Volatile read copy yok
- Lock yok (read path)

### 3.3 Binding constructor'ı için compiled lambda

`NexusDI.ResolveBinding` reflection-based constructor invocation kullanıyor (varsayıyorum, `Activator.CreateInstance` görmedim ama 1424 satırlık dosyada olabilir). Source generator ile constructor'ı compile et:

```csharp
// Generated:
private static readonly Func<NexusDI, MyService> _ctor_MyService = (di) => new MyService(di.Resolve<IDep>());
```

---

## BÖLÜM 4 — AKSİYON PLANI (Sıralı)

### Hafta 1 — Hızlı kazanımlar (yarın başla)

| # | Sınıf | İş | Tahmini etki |
|---|---|---|---|
| 1 | `CommandRegistry.cs:99-149` | Snapshot incremental güncelleme | Yüksek |
| 2 | `Context.cs:403-429` | `FindLifecycleTypeByConvention` cache | Orta |
| 3 | `ContextBuilder.cs:340-491` | Validate cache veya release'de skip | Yüksek |
| 4 | `ViewBinder.cs:155` | `GetMediatorAttrCached` | Yüksek (frame spike) |
| 5 | Tüm Lifecycle'lar | `BindService` → `BindLazyService` dönüşümü | Yüksek |
| 6 | `Mediator.cs:137-151` | Double-Reset düzeltmesi | Orta |

### Hafta 2 — Orta vadeli

| # | Sınıf | İş |
|---|---|---|
| 7 | `Context.cs:431-500` | Tek `GetCustomAttributes` çağrısı |
| 8 | `SignalBus.cs:808-881` | Composite trigger erken skip |
| 9 | `SignalBus.cs:466-471` | Cross-context cache |
| 10 | `SignalBus.cs:384-385` | Production metrics skip |
| 11 | `ContextBuilder.cs:152-170` | `GetUserDefinedInterfaces` cache |
| 12 | `Root.cs:179-194` | Cached component refs |
| 13 | `Root.cs:62-75` | HashSet pending views |

### Hafta 3+ — Büyük mimari

| # | Konu |
|---|---|
| 14 | Source Generator (assembly scan'ı bypass) |
| 15 | Command registry sealable/immutable |
| 16 | Compiled constructor lambdas |
| 17 | `[Inject]` setter generated code |

---

## BÖLÜM 5 — DOĞRULAMA PROTOKOLÜ

Her değişiklikten sonra ölç:

1. **Editor açılış süresi** — `Time.realtimeSinceStartup` logla, Root.Awake → IsInitialized = true
2. **İlk 60 frame** — `FrameTimingManager` ile her frame süresi
3. **GC alloc** — Profiler → Memory → "GC Alloc in frame" column
4. **Signal dispatch sayısı** — 1 saniyede kaç signal fire ediliyor

Bunları değişiklik öncesi kaydet, sonra karşılaştır. Tahmini kümülatif etki:

- **Açılış süresi:** %40-60 azalma
- **İlk frame spike:** %60-80 azalma
- **Steady-state GC alloc:** %10-30 azalma
- **Frame başına signal fire overhead:** %20-40 azalma

---

## NOTLAR

- Tüm değişiklikler geriye dönük uyumlu kalmalı (public API)
- Her refaktör kendi başına atomic olmalı, ayrı PR'lar
- Test coverage (eğer varsa) her değişiklikte çalışmalı
- `NEXUS_DEBUG` define'ı production perf'ı ölçmek için açılabilir/kapatılabilir
