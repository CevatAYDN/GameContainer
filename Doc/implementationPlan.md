# Nexus: Observable Architecture — Ürün Spesifikasyonu (RFC v2.5 — HARD FREEZE 🔒)

Bu doküman, Unity 6.5 için tasarlanan açık kaynak **Nexus** mimari çerçevesinin **dondurulmuş (frozen)** nihai ürün spesifikasyonunu içerir. Target implementation sürümü **v0.1**'dir.

> [!IMPORTANT]
> **🔒 HARD FREEZE — Bu doküman dondurulmuştur. Mimari değişiklik yapılamaz, sadece implementasyon netleştirmeleri ve v0.1 gereksinimleri eklenebilir.**
>
> **v2.5 Değişiklik Özeti (v0.1 Geri Bildirim Revizyonu):**
> 1. Asenkron Lifecycle: `IContextLifecycle` asenkron başlatma desteği (`OnInitializeAsync`, `OnStartAsync`) eklendi.
> 2. Composite Overflow: Fazla sinyal yutulma (idempotency) davranışı netleştirildi.
> 3. Command State Leak: Komut havuzlama sonrası state sızıntısını engellemek için `IResettable` ve otomatik bağımlılık temizleme kuralları eklendi.
> 4. Performance: Cross-Context sinyaller için `ScopeTag` filtreleme seçeneği eklendi.
> 5. View-Mediator Timing: `Awake` aşamasında Mediator/Model/SignalBus erişim yasağı ve `OnEnable`/`OnBind` kuralları eklendi.
> 6. DI Resolution: `IContextBuilder` DI binding arayüzü ve API'si tanımlandı.
> 7. Sürüm Hedefleri: Tüm `v1.0`/`v1.1` referansları ilk gerçek implementasyon hedefi olan **v0.1** olarak güncellendi.
> Bootstrap Manifest'ten `NexusMode` kaldırıldı.

## 🚀 Ana Vaat
**"Unity'de sisteminizin neden çalışmadığını 10 saniyede görün."**

---

## 🎯 Başarı Ölçütleri (Success Criteria)

| Metrik | Hedef | Not |
|---|---|---|
| GC Allocation (100K dispatch) | **Steady-state 0 bytes** | İlk çalıştırmada warmup; sonrası 0 alloc |
| IL2CPP / AOT testleri | **Tüm platformlar pass** | CI pipeline'da iOS/Android/WebGL hedefleri |
| Inspector FPS (1000 sinyal/sn) | **≥ 60 FPS** | Rate Limit sadece UI katmanında |
| Yeni kullanıcı ilk sistem kurulumu | **≤ 15 dakika** | |
| Sinyal zinciri takibi (Inspector) | **≤ 3 tıklama** | Causal chain ile tam nedensellik |
| Root Wizard ile ilk sahne | **≤ 30 saniye** | |
| Domain Reload kapalıyken sızıntı | **0 leak** | CancellationToken + NexusRuntime.Reset() |
| Lifecycle kontrat ihlali tespiti | **Compile-time veya Initialize-time** | |
| Equal-priority handler | **Build Error (hard fail)** | Determinizm garantisi |

## 📐 Tasarım Felsefesi: Progressive Disclosure (Kademeli Keşif)

Nexus'ta yapay kullanım modları (Lite, Standard, Full) **yoktur**.
Framework her zaman tam kabiliyetlidir. Geliştirici ihtiyacı kadarını kullanır.

### Temel Kurallar
*   **Basit şeyler basit olmalı:** `_signalBus.Fire(new DamageSignal(10));` — tek satır, çalışır.
*   **İleri özellikler opt-in:** Priority, ExecutionMode, Recovery — belirtmezsen akıllı varsayılanlar devreye girer.
*   **Hiçbir şey zorunlu değil:** Command olmadan da sinyal fırlatılır. Model olmadan da Command yazılır. Framework kızmaz.
*   **Hata mesajları = öğretmen:** Her exception, sorunu ve çözümü açıklar.
*   **Varsayılanlar akıllı:** Priority = 0, Mode = Sequential, Recovery = Skip. Belirtmezsen çalışır.

### İlk 5 Dakika Deneyimi
Bir geliştirici Nexus'u ilk kez kullandığında şunu yazar ve **çalışır**:
```csharp
// 1. Sinyal tanımla
public readonly struct DamageSignal
{
    public readonly int Amount;
    public DamageSignal(int amount) => Amount = amount;
}

// 2. Komutu yaz (sinyali dinle, bir şey yap)
[SignalHandler(typeof(DamageSignal))]
public class DamageCommand : ICommand
{
    public void Execute() => Debug.Log("Hasar alındı!");
}

// 3. Herhangi bir yerden fırlat
_signalBus.Fire(new DamageSignal(10));
```
Model yok, Mediator yok, özel konfigürasyon yok. İhtiyacı olduğunda ekler.
Framework bunu **modlarla değil, API tasarımıyla** sağlar.

---

# BÖLÜM 1: MİMARİ ÇEKİRDEK

## 1.1 Root → Context Hiyerarşisi
*   **Root (MonoBehaviour):** Sahnedeki fiziksel giriş noktası. Görevi sadece kendi `Context`'ini ayağa kaldırmak ve ona bir `ContextData` (ScriptableObject) vermektir.
*   **Context:** Kapsamı belirleyen beyin. Her Context kendi `SignalBus`'ını, `Model`'lerini, `Command` eşlemelerini ve `Mediator` kayıtlarını barındırır.

## 1.2 MVCS Boru Hattı ve Katman Kuralları

```text
Signal ──→ Command ──→ Model (yazma) ──→ Command sinyal fırlatır ──→ Mediator ──→ View
 (Olay)     (İş Mantığı) (Pasif Veri)     (Bildirim)                  (Köprü)     (Görsel)
```

### Doğru Akış Örneği
```csharp
// DamageCommand.Execute()
public void Execute()
{
    _playerModel.Health -= _signal.Amount;
    _signalBus.Fire(new HealthChangedSignal(_playerModel.Health));
}
```

### Sıkı Erişim Kuralları
| Kaynak | Erişebilir | Erişemez |
|---|---|---|
| **View** | Mediator (dolaylı) | Model, Command, Signal (doğrudan) |
| **Mediator** | Model (oku), SignalBus (dinle/fırlat) | Command (doğrudan), diğer Mediator |
| **Command** | Model (oku/yaz), SignalBus (fırlat) | View, Mediator |
| **Model** | Hiçbir şey (saf pasif veri deposu) | SignalBus, Command, View, Mediator |

## 1.3 API Tasarımı

### Sinyal Tanımı (Immutable Struct)
```csharp
public readonly struct DamageSignal
{
    public readonly int Amount;
    public DamageSignal(int amount) => Amount = amount;
}
```

### Dispatch API
```csharp
_signalBus.Fire(new DamageSignal(10));                  // Senkron
await _signalBus.FireAsync(new LoadProfileSignal());    // Asenkron (await)
_signalBus.FireThreadSafe(new DataLoadedSignal(data));  // Thread-safe kuyruk
_signalBus.FireNextFrame(new UIRefreshSignal());        // Sonraki kare
```

### Komut Türleri
```csharp
public interface ICommand
{
    void Execute();
}

public interface IAsyncCommand
{
    ValueTask ExecuteAsync(CancellationToken ct);
}
```

Context yaşam token'ı otomatik enjekte edilir:
```csharp
// Nexus Core iç mekanizması
await command.ExecuteAsync(context.LifetimeToken);
```

### CancellationToken Davranış Tablosu
| Durum | Davranış |
|---|---|
| Context dispose oldu | Token iptal → tüm async command'lar temiz durur |
| Command çalışıyor | `ct.ThrowIfCancellationRequested()` ile güvenli çıkış |
| Scene unload | Root → Context.Dispose → Token cancel → tüm zincir temiz |

## 1.4 Command Execution Modes (Komut Çalıştırma Modları)

Bir sinyal fırlatıldığında ona bağlı komutlar **nasıl** çalıştırılır? Bu, Nexus'un en kritik çalışma zamanı kararıdır. Sistem 4 farklı mod destekler:

### 1.4.1 Sequential (Sıralı) — Varsayılan Mod
Aynı sinyali dinleyen komutlar **Priority sırasıyla, birer birer** çalışır. Bir komut bitmeden sonraki başlamaz.
```text
DamageSignal fırlatıldı
    ↓
[P:100] DamageCommand.Execute()        ← biter
    ↓
[P:50]  ShieldCheckCommand.Execute()   ← biter
    ↓
[P:10]  AnalyticsCommand.Execute()     ← biter
    ↓
Dispatch tamamlandı
```
*   **Deterministic:** Evet. Her zaman aynı sıra, aynı sonuç.
*   **Kullanım:** Oyun mantığı, state değiştiren işlemler, replay/rollback gereken sistemler.
*   **Varsayılan olan bu moddur.** Açık bir attribute belirtilmezse Sequential çalışır.

### 1.4.2 Concurrent (Eşzamanlı) — Opt-in
Bağımsız async komutlar **aynı anda** başlatılır ve hepsinin bitmesi beklenir (`WhenAll`).

> [!NOTE]
> **M1 Düzeltmesi:** Concurrent mod'da Priority **anlamsızdır** (paralel çalışırlar, sıra yoktur).
> Bu yüzden eşit-Priority tekilleme kuralı (1.6) Concurrent handler'lara **uygulanmaz**.
> Concurrent handler'larda Priority alanı yok sayılır.

```csharp
[SignalHandler(typeof(LevelLoadSignal), Mode = ExecutionMode.Concurrent)]
public class PreloadAssetsCommand : IAsyncCommand { ... }

[SignalHandler(typeof(LevelLoadSignal), Mode = ExecutionMode.Concurrent)]
public class PreloadAudioCommand : IAsyncCommand { ... }
```
```text
LevelLoadSignal fırlatıldı
    ↓
┌─ PreloadAssetsCommand.ExecuteAsync() ─┐
│                                       │ → WhenAll → Dispatch tamamlandı
└─ PreloadAudioCommand.ExecuteAsync()  ─┘
```
*   **Deterministic:** Hayır (zamanlama farklı olabilir). Bu yüzden **state yazan komutlarda kullanılmamalıdır.**
*   **Kullanım:** Bağımsız I/O işlemleri (asset yükleme, network fetch, dosya okuma).

> [!IMPORTANT]
> **M2 Düzeltmesi:** "Concurrent komutlar Model'e yazamaz" kuralı, statik kod analizi ile **denetlenemez**
> (virtual dispatch, dolaylı çağrılar analizi pratikte imkansız). Bunun yerine **tip sistemi** ile zorlanır:
> Concurrent command'lere yazılabilir Model referansı **enjekte edilmez**; sadece read-only arayüzler verilir.

*   **Kural:** Concurrent komutlara sadece `IReadOnlyXxxModel` tarzı okuma arayüzleri inject edilir.
*   **Build Validation:** "Concurrent command'e yazılabilir Model inject edilmiş mi?" → inject tablosuna bakar (kolay, güvenilir).
*   **Mekanizma:** Model'ler hem yazılabilir (`IPlayerModel`) hem okunabilir (`IReadOnlyPlayerModel`) arayüz sunar.
    Sequential/Exclusive command'ler yazılabilir arayüzü alır, Concurrent command'ler sadece read-only alır.

```csharp
// Model arayüzleri
public interface IReadOnlyPlayerModel
{
    int Health { get; }
    int Score { get; }
}

public interface IPlayerModel : IReadOnlyPlayerModel
{
    new int Health { get; set; }
    new int Score { get; set; }
}

// Concurrent command — sadece read-only alır (derleme zamanı güvenlik)
[SignalHandler(typeof(LevelLoadSignal), Mode = ExecutionMode.Concurrent)]
public class PreloadStatsCommand : IAsyncCommand
{
    private readonly IReadOnlyPlayerModel _player; // ✅ Sadece okuma
    
    public async ValueTask ExecuteAsync(CancellationToken ct)
    {
        var hp = _player.Health; // ✅ Okuma — güvenli
        // _player.Health = 50;  // ❌ Derleme hatası — yazılamaz
    }
}
```

### 1.4.3.2 DI Bağlama ve IContextBuilder API'si

Nexus'ta model, komut ve bağımlılıkların DI (Dependency Injection) kaydı `IContextLifecycle.OnConfigure(IContextBuilder builder)` aşamasında yapılır. `IContextBuilder` arayüzü şu API'yi sunar:

```csharp
public interface IContextBuilder
{
    // Model Bağlama (Singleton / Context-scoped)
    void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface;
    void BindModel<TImplementation>() where TImplementation : class;
    void BindModelInstance<TInterface>(TInterface instance) where TInterface : class;
    
    // Genel Bağımlılık Bağlama (DI)
    void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface;
    void Bind<T>() where T : class;
    void BindInstance<T>(T instance) where T : class;
    
    // Command Bağlama (Gerektiğinde manuel bağlama - Attribute yerine alternatif veya tamamlayıcı)
    void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
        where TCommand : class, ICommand;
    void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
        where TCommand : class, IAsyncCommand;
}
```

### 1.4.3 Exclusive (Tekil) — Tek Handler Garantisi
Bir sinyale **sadece bir** komut bağlanabilir. İkinci bir handler eklenmeye çalışılırsa Build Error.
```csharp
[SignalHandler(typeof(PurchaseSignal), Mode = ExecutionMode.Exclusive)]
public class ProcessPurchaseCommand : ICommand { ... }
```
*   **Kullanım:** Ödeme, save, kritik tek-noktadan-geçmesi-gereken işlemler.
*   **Garanti:** Sinyal fırlatıldığında kesinlikle tek bir komut çalışır. Hiçbir belirsizlik yok.

### 1.4.4 Composite Trigger (Bileşik Tetik) — Çoklu Sinyal → Tek Komut
Birden fazla sinyalin **hepsinin** gelmesini bekleyen komut. Fan-in yapısı.
```csharp
[CompositeSignalHandler(
    typeof(PlayerReadySignal), 
    typeof(LevelLoadedSignal), 
    typeof(UIReadySignal),
    OneShot = false              // varsayılan: re-triggerable
)]
public class StartGameCommand : ICommand
{
    public void Execute()
    {
        // Üç sinyal de geldi — oyunu başlat
    }
}
```
```text
PlayerReadySignal  ──┐
LevelLoadedSignal  ──┼──→ [Hepsi geldi mi?] ──→ StartGameCommand.Execute()
UIReadySignal      ──┘
```
*   **State Yönetimi:** Nexus, hangi sinyallerin geldiğini Context-scoped bir `BitMask` ile takip eder. Context dispose olunca mask sıfırlanır.
*   **Kullanım:** Oyun başlatma, sahne geçişi koordinasyonu, çoklu kaynaktan onay bekleme.
*   **Lifecycle:** Context `OnDispose` olduğunda bekleyen composite handler'lar temizlenir, orphan bırakılmaz.

> [!NOTE]
> **M3 Düzeltmesi — Composite Trigger Semantiği:**
> *   **Varsayılan: Re-triggerable.** Tüm sinyaller tamamlandığında komut çalışır ve mask otomatik sıfırlanır.
>     Sonraki döngüde sinyaller tekrar gelirse komut tekrar tetiklenir. Level geçiş koordinasyonu için idealdir.
> *   **Opsiyonel: OneShot.** `OneShot = true` ayarlandığında komut Context ömrü boyunca **yalnızca bir kez** çalışır.
>     Oyun başlatma gibi tek seferlik olaylar için uygundur.

> [!IMPORTANT]
> **Composite Signal Overflow & Idempotency:**
> *   Composite set içindeki bir sinyal (ör. `PlayerReadySignal`) birden fazla kez fırlatılırsa, ilgili bit maskesindeki bit 1 olarak kalır ve **ekstra gelen sinyaller yutulur (idempotent)**.
> *   Döngü tamamlanıp komut tetiklenene (veya re-triggerable ise maske sıfırlanana) kadar gelen mükerrer sinyaller yeni bir tetikleme sırası oluşturmaz.

> [!NOTE]
> **M7 — Uygulama Notu:** Composite setin üye sinyalleri (ör. `LevelLoadedSignal`) aynı zamanda normal
> `[SignalHandler]` ile bağlanmış handler'lara da dispatch edilir. İki sistem **ortogonaldir** — composite
> mask takibi ve normal dispatch birbirini engellemez.

### 1.4.5 Mixed-Mode Yasağı (Karışık Mod Kuralı)

> [!IMPORTANT]
> **M4 Düzeltmesi:** Tek bir sinyale bağlanan tüm handler'lar **aynı Execution Mode'u** paylaşmak
> zorundadır. Karışık mod (ör. aynı sinyale hem Sequential hem Concurrent handler) → **Build Error**.

```text
❌ YASAK:
[SignalHandler(typeof(LoadSignal), Mode = Sequential)]  DamageCommand
[SignalHandler(typeof(LoadSignal), Mode = Concurrent)]  PreloadCommand
→ Build Error: "LoadSignal has mixed execution modes (Sequential + Concurrent)"

✅ DOĞRU:
Tüm handler'lar aynı mod → Sequential VEYA Concurrent VEYA Exclusive
```

### Execution Mode Özet Tablosu
| Mod | Sıralama | Deterministic | Model Yazma | Priority Geçerli | Birden Fazla Handler | Kullanım Alanı |
|---|---|---|---|---|---|---|
| **Sequential** (varsayılan) | Priority | ✅ Evet | ✅ (yazılabilir) | ✅ Evet | ✅ Evet | Oyun mantığı, state |
| **Concurrent** | Paralel | ❌ Hayır | ❌ (read-only) | ❌ Yok sayılır | ✅ Evet | I/O, asset yükleme |
| **Exclusive** | Tek handler | ✅ Evet | ✅ (yazılabilir) | ✅ Evet | ❌ Sadece 1 | Ödeme, save |
| **Composite** | Bileşik tetik | ✅ Evet | ✅ (yazılabilir) | ✅ Evet | ❌ Sadece 1 | Koordinasyon, başlatma |

## 1.5 Sinyal-Komut Eşleme: Tek Doğruluk Kaynağı

### Attribute = Gerçek Bağlama (Single Source of Truth)
```csharp
[SignalHandler(typeof(DamageSignal), Priority = 100)]
public class DamageCommand : ICommand { ... }
```

### ContextData = Orkestrasyon (Daraltılmış Rol)
```csharp
[CreateAssetMenu(menuName = "Nexus/Context Data")]
public class ContextData : ScriptableObject
{
    [Header("Orchestration")]
    public string[] AssemblyScopes;      // Hangi assembly'ler taranacak
    public string[] DependsOn;           // Bağımlılık sırası
    
    [Header("Feature Flags")]
    public bool EnableAnalytics;
    public bool EnableDebugSignals;
    
    [Header("Performance")]
    public int CommandPoolInitialSize;
}
```

## 1.6 Sinyal Sıralaması ve Determinizm Garantisi

> [!IMPORTANT]
> **D2 Düzeltmesi:** Eşit Priority = **Build Error (Hard Fail)**.
> Determinizm vaadi, belirsiz sıralamaya asla izin vermez.
> **M1 Muafiyeti:** Bu kural yalnızca **Sequential ve Exclusive** modlar içindir.
> Concurrent mod'da Priority yok sayılır ve eşit değer engellenmez.

### Priority Kuralları
1.  **Sequential/Exclusive:** Her handler **benzersiz** bir Priority değerine sahip olmalıdır (aynı sinyal kapsamında).
2.  Eşit Priority tespit edildiğinde → **Build Error** (uyarı değil, build kırılır).
3.  Priority değeri belirtilmezse → varsayılan `Priority = 0`. İkinci bir handler de varsayılan kullanırsa → Build Error.
4.  **Concurrent:** Priority alanı yok sayılır. Eşit değer serbesttir (paralel çalışırlar, sıra yoktur).
5.  **Composite:** Tek handler garantili olduğundan Priority çakışma riski yoktur.

### Deterministic Tie-Break (Güvenlik Ağı)
Build Validation'ı atlatıp runtime'a ulaşan eşit-priority edge case'i için son çare:
*   **Tip adı (fully qualified name) alfabetik sırası** ile çözülür.
*   Bu davranış **loglanır ve Inspector'da sarı uyarı** olarak gösterilir.
*   Garanti: Runtime'da **asla tanımsız sıra yoktur**. Her koşulda deterministik.

> [!NOTE]
> **M6 — Uygulama Notu:** Tie-break yalnızca **farklı ContextData scan-scope'larından** gelen
> eşit-priority handler'lar için tetiklenir. Tek scope içinde Build Validation zaten eşit priority'yi
> engeller; bu yüzden tie-break, cross-scope edge case güvenlik ağıdır.

## 1.7 Reentrancy Koruması (Senkron + Asenkron)

> [!IMPORTANT]
> **D3 Düzeltmesi:** Senkron ve asenkron reentrancy ayrı mekanizmalarla korunur.

### Senkron Reentrancy
Stack-derinlik sayacı. `Fire()` → `Command` → `Fire()` → `Command` → ... zincirinde derinlik limiti (varsayılan: 50) aşılırsa `NexusReentrancyException` fırlatılır.

### Asenkron Reentrancy
Stack unwind nedeniyle stack-derinlik sayacı async döngüleri yakalayamaz. Bu nedenle ayrı bir mekanizma:
*   **In-flight Async Command Counter:** Aynı anda çalışan async command sayısı takip edilir. Konfigüre edilebilir bir limit (varsayılan: 100) aşıldığında `NexusAsyncOverflowException` fırlatılır.
*   **Async Signal Trace:** Async zincirinde aynı sinyal tipinin belirli bir pencere içinde (varsayılan: 500ms) tekrar fırlatılması durumunda Inspector'da **sarı uyarı** gösterilir (potansiyel async döngü).
*   **v0.1 kapsamı:** In-flight counter + Inspector uyarısı (best-effort). Tam async-graph-cycle detection v0.2'de.

### Reentrancy Özet Tablosu
| Zincir Türü | Tespit Mekanizması | Aksiyon | Kapsam |
|---|---|---|---|
| Senkron döngü | Stack-derinlik sayacı | `NexusReentrancyException` | v0.1 |
| Async overflow | In-flight command counter | `NexusAsyncOverflowException` | v0.1 |
| Async döngü (potansiyel) | Aynı sinyal tekrar uyarısı | Inspector sarı uyarı | v0.1 |
| Async graph-cycle detection | Tam statik analiz | Build Validation | v0.2 |

## 1.8 Frame Boundary Dispatch
`FireNextFrame(signal)` → `Queue<T>` → `LateUpdate`'de toplu tüketim.

### İki Erteli Kuyruk Sıralaması (L3 — Uygulama Notu)
Nexus iki farklı erteli kuyruk kullanır:
1.  **Thread-Safe Queue** (`FireThreadSafe`) → `Update` başında drain edilir.
2.  **NextFrame Queue** (`FireNextFrame`) → `LateUpdate`'de drain edilir.

Sıralama garantisi: Aynı frame içinde **önce** thread-safe kuyruk (Update), **sonra** next-frame kuyruk (LateUpdate) işlenir. Bu sıra sabittir ve deterministiktir.

## 1.9 Context Arası İletişim (Cross-Context Communication)
İki Context birbirini **asla** doğrudan referans almaz.

### Cross-Context Sinyal Semantiği (L4 — Uygulama Notu)
*   `[CrossContext]` sinyaller **her aktif Context'in SignalBus'ına bir kez** dispatch edilir.
*   Priority değerleri **Context içinde** geçerlidir; farklı Context'lerdeki handler'lar arasında Priority karşılaştırması yapılmaz.
*   Dispatch sırası: Context Dependency Graph'taki sıraya (parent → child) göre.
*   **ScopeTag Filtreleme:** Çoklu Context içeren büyük sahnelerde (ör. 500+ Context) performansı korumak için `[CrossContext(ScopeTag = "Gameplay")]` şeklinde etiketleme yapılabilir. Sinyal sadece o tag ile eşleşen Context'lerin SignalBus'ına yönlendirilir. Boş bırakılırsa tüm Context'lere broadcast edilir.
*   **Paylaşımlı Modeller:** `GlobalContext` içinde kayıtlı, alt Context'lere enjekte edilir.

## 1.10 Hata Yönetimi (Error Handling)
Bir komut içinde fırlatılan `Exception`:
1.  `CommandFailedSignal { Exception, SourceCommand, SourceSignal }` fırlatılır.
2.  `IRecoveryStrategy` varsa karar alınır (Bölüm 7).
3.  Inspector'da kırmızıyla loglanır.

---

# BÖLÜM 2: YAŞAM DÖNGÜSÜ MOTORU (LIFECYCLE ENGINE)

## 2.1 Lifecycle Kontratı
```csharp
public interface IContextLifecycle
{
    void OnConfigure(IContextBuilder builder);  // Senkron kayıtlar (DI)
    ValueTask OnInitializeAsync(CancellationToken ct);  // Asenkron başlatma (bağlantılar hazır)
    ValueTask OnStartAsync(CancellationToken ct);       // Asenkron oyun mantığı başlangıcı
    void OnDispose();                            // Senkron temizlik
}
```

### Çalışma Sırası Garantisi
```text
1. Root.Awake()
2.   → Context.OnConfigure()
3. Root.Start() (asenkron başlatılır)
4.   → Context.OnInitializeAsync()  (await edilir)
5.   → Context.OnStartAsync()       (await edilir)
   ...
6. Root.OnDestroy() / Scene Unload
7.   → Context.OnDispose()
```
Dependency Graph'a göre: Bağımlı Context, parent'ın `OnStartAsync()` tamamlanmadan başlamaz. Eğer asenkron bir aşamada cancellation tetiklenirse başlatma yarıda kesilir ve hemen `OnDispose()` çağrılarak yarım kalan state temizlenir.

## 2.2 Subscription Lifetime
```csharp
public interface ISignalSubscription : IDisposable
{
    bool IsActive { get; }
    CancellationToken Lifetime { get; }
}
```
*   **Context-scoped:** Context `Dispose` → tüm subscription'lar toplu temizlenir.
*   **View-scoped:** `destroyCancellationToken` üzerinden otomatik.
*   **Manuel:** `subscription.Dispose()` ile erken koparma.

## 2.3 View Registration Lifecycle (Dinamik View Bağlama)

### View Self-Registration Kontratı
```csharp
public interface IView
{
    void Bind(IContext context);
    void Unbind();
}
```

### Spawn Akışı
```text
Instantiate(prefab) → View.OnEnable() → Context.RegisterView(this)
    → MediatorFactory.Create<T>() → Mediator.OnBind(view, signalBus)
```

### Destroy Akışı
```text
Destroy(go) → View.OnDisable() → Context.UnregisterView(this)
    → Mediator.OnUnbind() → MediatorFactory.Return(mediator) [havuza geri]
```

Mediator ömrü **View'a bağlıdır**, sahneye değil. Addressables async prefab'lar dahil.

### Awake vs OnEnable/OnBind Kuralları
*   **Awake Yasaktır:** View'ların `Awake()` aşamalarında Context veya SignalBus tam kurulmamış olabilir. Bu yüzden `Awake` içinde Mediator'a, Model'e veya `SignalBus`'a erişmek **kesinlikle yasaktır** (Runtime Error tetiklenir).
*   **OnEnable / OnBind Kullanımı:** View veri bağlaması veya ilk event tetiklemesi en erken `OnEnable` (View self-register olduktan sonra) veya en güvenlisi Mediator'ın `OnBind` callback'i içinde gerçekleştirilmelidir.

## 2.4 Domain Reload Stratejisi
*   Nexus Core sıfır statik mutable state tutar.
*   `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` → `NexusRuntime.Reset()`.
*   Garanti: Domain Reload açık/kapalı → sonuç aynı, leak sıfır.

## 2.5 Scene Unload Davranışı
1.  Root `OnDestroy` → Context `OnDispose()`.
2.  Subscription'lar, Mediator'lar, command pool'lar, composite trigger mask'leri temizlenir.
3.  `LifetimeToken` iptal → tüm async command'lar güvenle durur.
4.  `GlobalContext` (DontDestroyOnLoad) etkilenmez.

---

# BÖLÜM 3: THREADING VE CONCURRENCY MODELİ

## 3.1 Hybrid Queue Model
```text
┌──────────────────────────────────────┐
│           ANY THREAD                 │
│  _signalBus.FireThreadSafe(signal)   │
│         ↓                            │
│  ┌────────────────────┐              │
│  │  ConcurrentQueue   │ (lock-free)  │
│  └────────┬───────────┘              │
└───────────┼──────────────────────────┘
            ↓  (Main Thread, Update başında)
┌──────────────────────────────────────┐
│       MAIN THREAD ONLY               │
│  SignalBus.DrainQueue()              │
│  → Normal dispatch pipeline          │
└──────────────────────────────────────┘
```
Tüm `Command.Execute()` çağrıları **main thread garantilidir**.

---

# BÖLÜM 4: BELLEK SAHİPLİĞİ MODELİ

| Model Türü | Sahibi | Yaşam Süresi | Dispose Sorumlusu |
|---|---|---|---|
| **Local** | Oluşturan Context | Context ömrü | Context.OnDispose() |
| **Shared** | GlobalContext | Uygulama ömrü | GlobalContext.OnDispose() |
| **Transient** | GC | Referans kalmadığında | Garbage Collector |

```csharp
public interface IResettableModel { void Reset(); }
public interface IDisposableModel : IDisposable { }
```

Build Validation: Dispose zincirinde çağrılmayan `IDisposableModel` → **Hata**.

---

# BÖLÜM 5: VERİ ODAKLI SİSTEM (DATA-DRIVEN)

| SO Türü | Amacı |
|---|---|
| `ContextData` | Orkestrasyon: scan scope, bağımlılıklar, feature flags |
| `ModelData` | Başlangıç değerleri (can, hız vb.) |
| `NexusBootstrapManifest` | Root Wizard şablonu |

### Bootstrap Manifest
```csharp
[CreateAssetMenu(menuName = "Nexus/Bootstrap Manifest")]
public class NexusBootstrapManifest : ScriptableObject
{
    [Header("Proje İskeleti")]
    public string[] DefaultContextNames;  // ["Global", "Gameplay", "UI"]
    public bool GenerateSampleSignals;    // Örnek sinyal dosyası oluştursun mu?
    public bool GenerateSampleCommands;   // Örnek komut dosyası oluştursun mu?
    
    [Header("Editor Araçları")]
    public bool EnableInspector;          // Nexus Inspector otomatik aktif mi?
}
```

SO Versioning: Zorunlu `_version` alanı + Migration araçları.

---

# BÖLÜM 6: PERFORMANS MİMARİSİ

## 6.1 Zero Allocation Dispatch (Steady-State)
*   **Struct Signals:** `readonly struct` → 0 heap allocation.
*   **Command Pooling:** Object Pool'dan çekil, geri bırak.
*   **Boxing Koruması:** Generic dispatch'te `object` kutulama yok → cached strongly-typed delegate.
*   **Warmup:** İlk çalıştırmada delegate cache + reflection scan. Sonraki tüm dispatch'ler allocation-free.

### 6.1.1 Command State & Referans Sızıntısı Koruması (GC Leak / State Reset)
*   **Havuzlama Reset Kontratı:** GC allocation'ı 0'da tutmak için Command nesneleri havuzlanır. Durum (state) sızıntısını ve referans birikmesini önlemek için:
    *   Command çalıştırıldıktan sonra, havuza geri gönderilmeden önce, eğer komut `IResettable` arayüzünü uyguluyorsa `Reset()` çağrılır.
    *   Nexus Core, komut üzerindeki enjekte edilmiş tüm bağımlılık alanlarını (dependencies) otomatik olarak `null` set ederek GC'nin bu nesneleri serbest bırakabilmesini (leak önlemeyi) garanti eder.
    *   Komut içinde enjekte edilmemiş, durum saklayan ve `IResettable` ile sıfırlanmayan alanlar bulunursa, Build Validation aşamasında **Build Warning** üretilir.

## 6.2 AOT / IL2CPP Güvenliği
*   `[Preserve]` attribute'ları ve `link.xml` otomatik üretimi.
*   İlk sürüm Reflection + Aggressive Caching. Source Generator opsiyonel eklenti (v0.2+).

## 6.3 `NEXUS_DEBUG` Derleme Bayrağı
Trace çağrıları `#if NEXUS_DEBUG` ile sarmalanır. Production'da derlenmez.

## 6.4 Profiling Entegrasyonu (Unity Profiler)
```csharp
static readonly ProfilerMarker s_DispatchMarker = new ProfilerMarker("Nexus.Signal.Dispatch");
static readonly ProfilerMarker s_CommandMarker  = new ProfilerMarker("Nexus.Command.Execute");
static readonly ProfilerMarker s_DrainMarker    = new ProfilerMarker("Nexus.Queue.Drain");
```
Unity Profiler Timeline'ında her dispatch ayrı blok. Production'da IL2CPP otomatik strip.

---

# BÖLÜM 7: HATA KURTARMA STRATEJİSİ (ERROR RECOVERY)

> [!IMPORTANT]
> **D1 Düzeltmesi:** `RecoveryAction` enum yerine `RecoveryDecision` struct.
> Fallback artık hedef komut tipini ve Retry artık max deneme sayısını taşır.

```csharp
public readonly struct RecoveryDecision
{
    public readonly RecoveryAction Action;
    public readonly Type FallbackCommandType;  // Action == Fallback ise çalışacak komut
    public readonly int MaxRetries;             // Action == Retry ise max deneme (varsayılan: 3)
    
    public static RecoveryDecision Skip()     
        => new(RecoveryAction.Skip, null, 0);
    public static RecoveryDecision Retry(int max = 3) 
        => new(RecoveryAction.Retry, null, max);
    public static RecoveryDecision Abort()    
        => new(RecoveryAction.Abort, null, 0);
    public static RecoveryDecision Fallback<T>() where T : ICommand 
        => new(RecoveryAction.Fallback, typeof(T), 0);
}

public enum RecoveryAction { Skip, Retry, Abort, Fallback }

public interface IRecoveryStrategy
{
    RecoveryDecision OnCommandFailed(CommandFailureContext failure);
}

public readonly struct CommandFailureContext
{
    public readonly Exception Exception;
    public readonly Type CommandType;
    public readonly object Signal;
    public readonly int RetryCount;      // Şu anki deneme sayısı
}
```

### Kullanım Örneği
```csharp
public class GameRecoveryStrategy : IRecoveryStrategy
{
    public RecoveryDecision OnCommandFailed(CommandFailureContext ctx)
    {
        if (ctx.CommandType == typeof(SaveGameCommand))
            return RecoveryDecision.Retry(max: 3);
            
        if (ctx.CommandType == typeof(PurchaseCommand))
            return RecoveryDecision.Fallback<ShowErrorDialogCommand>();
            
        if (ctx.CommandType == typeof(AnalyticsCommand))
            return RecoveryDecision.Skip();
            
        return RecoveryDecision.Abort();
    }
}
```

### Varsayılan Davranış (Strateji Kayıtlı Değilse)
1.  `CommandFailedSignal` fırlatılır.
2.  Komut atlanır (`Skip`), zincire devam.
3.  Inspector'da kırmızı log.

> [!NOTE]
> **M5 — Uygulama Notu (Retry Tükenmesi):** `Retry(max: N)` denemeleri tükendiğinde
> (`RetryCount >= MaxRetries`), strateji **tekrar çağrılır** (son `RetryCount` ile). Strateji bu noktada
> `Abort`, `Skip` veya `Fallback` döndürebilir. Eğer strateji yine `Retry` döndürürse → **zorla Abort**
> uygulanır ve Inspector'da uyarı loglanır. Varsayılan davranış (strateji kayıtlı değilse): retry
> tükenince otomatik `Abort`.

---

# BÖLÜM 8: PLUGIN İZOLASYON SİSTEMİ

```csharp
public interface INexusPlugin
{
    NexusPluginManifest Manifest { get; }
    void OnPluginRegistered(IPluginContext context);
    void OnPluginRemoved();
}

[Flags]
public enum PluginCapabilities
{
    None              = 0,
    SignalInterceptor = 1 << 0,
    CommandDecorator  = 1 << 1,
    ContextExtender   = 1 << 2,
    ModelSerializer   = 1 << 3,
    TraceProvider     = 1 << 4,
}
```

| Kural | Açıklama |
|---|---|
| Yetenek beyanı zorunlu | Manifest'te beyan etmeden müdahale → `UnauthorizedPluginAccessException` |
| Core readonly | Plugin iç dispatch mekanizmasını değiştiremez, sadece hook'lara takılır |
| Çıkarılabilirlik | `OnPluginRemoved()` tüm hook'ları temizler |

### Resmi Eklenti Paketleri
| Paket | Yetenek |
|---|---|
| `Nexus.Core` | SignalBus, Context, Root, MVCS, Build Validation |
| `Nexus.Inspector` | Signal Explorer, Context Graph, Time Travel, Type Analyzer |
| `Nexus.Addressables` | SO/Context için Addressables entegrasyonu |
| `Nexus.SaveSystem` | `ISerializableModel` ile State kaydetme/yükleme |
| `Nexus.Netcode` | Sinyal senkronizasyonu, authority yönetimi |
| `Nexus.UniTask` | `IAsyncCommand` için UniTask entegrasyonu |

---

# BÖLÜM 9: GELİŞTİRİCİ DENEYİMİ (DX) ARAÇLARI

Tüm görsel araçlar **UI Toolkit** ile inşa edilir.

## 9.1 Root Wizard
`GameObject → Create Nexus Root`. Manifest'ten deterministik iskelet.

## 9.2 Nexus Inspector (Canlı Sinyal İzleyici)
`Window → Nexus → Inspector`.
*   Rate Limit sadece UI çizimi; Ring Buffer her zaman beslenir.
*   Causal Chain tam nedensellik zinciri.
*   Execution Mode gösterimi (Sequential/Concurrent/Exclusive/Composite).
*   Composite Trigger state gösterimi (hangi sinyaller geldi, hangileri bekleniyor).

## 9.3 Signal Explorer (Statik Harita)
`Window → Nexus → Signal Explorer`. Oyun çalışmasa bile sinyal-komut eşlemeleri + Priority + Execution Mode.

## 9.4 Context Graph Editor
`Window → Nexus → Context Graph`. Node tabanlı, Virtualization ile 500+ Context destekli.

## 9.5 Type Dependency Analyzer
Seçilen sınıfın "Referenced By" + "Depends On" ağı.

## 9.6 Build Validation System
| Kontrol | Seviye | Not |
|---|---|---|
| Circular Context | **Error** | |
| Missing Binding (dinleyeni olmayan sinyal) | **Warning** | |
| Equal Priority — Sequential/Exclusive (aynı sinyal, aynı Priority) | **Error** (D2) | Concurrent muaf (M1) |
| Exclusive mod ihlali (ikinci handler) | **Error** | |
| Mixed-mode dispatch (aynı sinyale farklı mod) | **Error** (M4) | |
| Concurrent command + yazılabilir Model inject | **Error** (M2) | Inject tablosu kontrolü |
| Unused Context | **Warning** | |
| Missing SO (boş ContextData) | **Error** | |
| Reentrancy riski (statik senkron analiz) | **Warning** | |
| Ownership ihlali (IDisposableModel Dispose zinciri dışı) | **Error** | |
| Thread Safety (Fire() yerine FireThreadSafe() gerekli) | **Warning** | |
| Composite Trigger unreachable signal | **Warning** | |

## 9.7 Time Travel Debugging
Ring Buffer (10K olay, ~1 MB). 0 GC, sabit bellek. Geçmişe bakma.

---

# BÖLÜM 10: TRACE SİSTEMİ (CAUSAL TRACING)

## 10.1 TraceEvent Yapısı
```csharp
public readonly struct TraceEvent
{
    public readonly int Id;
    public readonly int ParentId;           // Kök ise -1
    public readonly TraceEventType Type;    // Signal, Command, ModelChange
    public readonly double Timestamp;
    public readonly string TypeName;
    public readonly TraceStatus Status;     // OK, Failed, Cancelled
    public readonly ExecutionMode Mode;     // Sequential, Concurrent, Exclusive, Composite
}
```

## 10.2 Causal Chain Örneği
```text
[Id:1  Parent:-1] DamageSignal(50)           ← Kök
[Id:2  Parent:1 ] DamageCommand              ← Sequential P:100
[Id:3  Parent:2 ] HealthChangedSignal(50)    ← Command fırlattı
[Id:4  Parent:3 ] UpdateUICommand            ← Sequential P:100
[Id:5  Parent:3 ] PlaySoundCommand           ← Sequential P:50
```

## 10.3 Mimari Ayrım
```text
Runtime: SignalBus → INexusTraceSink
                          │
         ┌────────────────┼────────────────┐
   EditorTraceSink   FileTraceSink   NullTraceSink
   (Inspector+Ring)     (Log)        (Production)
```

*   Ring Buffer = Source of Truth (her zaman yazılır).
*   Rate Limit = Sadece UI çizim throttle'ı.
*   Production: `NullTraceSink` + `#if NEXUS_DEBUG` → Runtime cost = 0.

---

# BÖLÜM 11: HOT RELOAD VE İTERASYON HIZI

*   Sıfır statik mutable state → `NexusRuntime.Reset()` ile temiz başlangıç.
*   Domain Reload açık/kapalı → sonuç aynı.
*   SO Hot Edit: `[LiveReload]` attribute ile Play Mode'da ModelData değişikliği yansıtılabilir.
*   Model state koruması (recompile sonrası): Sadece SO-backed `ModelData` üzerinden veya `Nexus.SaveSystem` eklentisi ile. Model'ler saf C# → `[SerializeField]` uygulanamaz.

---

# BÖLÜM 12: UNITY 6.5 ÖZEL ENTEGRASYONLARI

> [!NOTE]
> **L5 — Minimum Unity Feature Set:** Nexus, aşağıdaki Unity 6.5 özelliklerini aktif olarak kullanır.

| Unity 6.5 Özelliği | Nexus Kullanımı |
|---|---|
| `destroyCancellationToken` | View-scoped subscription lifetime (otomatik unsubscribe) |
| `Awaitable` / `async-await` | `IAsyncCommand.ExecuteAsync`, `FireAsync`, `Concurrent` mod |
| `UI Toolkit` / `GraphView` | Tüm Editor araçları (Inspector, Signal Explorer, Context Graph) |
| `ProfilerMarker` | Signal dispatch ve Command execute profillemesi |
| `ConcurrentQueue<T>` | Thread-safe dispatch kuyruğu (Hybrid Queue) |
| Source Generators (C# 11+) | Opsiyonel: attribute → binding code gen (v0.2+) |
| Addressables | `Nexus.Addressables` eklentisi ile SO/prefab async yükleme |

---

# BÖLÜM 13: TEST VE CI MİMARİSİ

## 13.1 Test Katmanları

| Katman | Araç | Kapsam |
|---|---|---|
| **Unit Test** | Unity Test Framework (NUnit) | SignalBus dispatch, Command pool, Ring Buffer, Priority sorting, Reentrancy counter |
| **Integration Test** | Unity Test Framework (Play Mode) | Context lifecycle (Configure→Start→Dispose), View registration, Cross-context signal, Composite trigger |
| **Validation Test** | Build Validation API (Editor) | Circular context, equal priority, ownership ihlali, exclusive mod ihlali |
| **AOT/IL2CPP Test** | Unity Cloud Build + CI | iOS Simulator, Android IL2CPP, WebGL — link.xml doğrulaması, generic stripping |
| **Performance Test** | Unity Performance Testing | 100K dispatch allocation ölçümü, Ring Buffer throughput, Inspector FPS |

## 13.2 CI Pipeline (Önerilen)

```text
Push / PR
    ↓
[Stage 1] Unit Tests (EditMode + PlayMode) — ~2 dk
    ↓
[Stage 2] Build Validation Tests — ~1 dk
    ↓
[Stage 3] IL2CPP Build (Android + iOS Sim) — ~15 dk
    ↓
[Stage 4] Performance Regression Tests — ~5 dk
    ↓
[Stage 5] WebGL Build + Smoke Test — ~10 dk
    ↓
✅ Merge Ready
```

## 13.3 Test Harness API

> [!NOTE]
> **M8 — Uygulama Notu:** `NexusTestHarness` imperative `Register<T>()` API'si,
> Attribute-SSoT kuralını (1.5) **yalnızca test kapsamında** bypass eder. Bu, test
> izolasyonu için standart bir pratiktir. Production runtime'da her zaman attribute-scan
> kullanılır; `Register<T>()` production kodunda çağrılamaz.

```csharp
// Geliştirici kendi komut zincirlerini kolayca test edebilir
var testContext = NexusTestHarness.CreateContext();
testContext.Register<DamageCommand>();
testContext.Register<HealthChangedSignal>();

testContext.Dispatch(new DamageSignal(50));

Assert.That(testContext.SignalWasDispatched<HealthChangedSignal>());
Assert.AreEqual(50, testContext.GetModel<PlayerModel>().Health);
```

---

# BÖLÜM 14: BENİMSENME STRATEJİSİ (ADOPTION)

| Zaman Dilimi | Kazanç |
|---|---|
| **İlk 30 saniye** | Root Wizard ile ilk sahne ayağa kalkar |
| **İlk 10 dakika** | Nexus Inspector açılır, sinyal akışı canlı görülür |
| **İlk 1 saat** | İlk sinyal + komut sistemi kurulur, çalışır |
| **İlk 1 gün** | Model, Mediator eklenip gerçek mekanik yazılır |
| **İlk 1 ay** | Build Validation, Time Travel Debugging, 0 GC |

## Karşılaştırma Tablosu

| Kriter | VContainer | Zenject | **Nexus** |
|---|---|---|---|
| Öğrenme Eğrisi | Orta | Yüksek | **Progressive Disclosure — sıfır yapay kısıtlama** |
| Runtime Debugging | ❌ | ❌ | **✅ Inspector + Causal Chain** |
| Yaşam Döngüsü Kontratı | Kısmi | Kısmi | **✅ Tam Lifecycle Engine** |
| Command Execution Modes | ❌ | ❌ | **✅ 4 mod (Seq/Conc/Excl/Comp)** |
| View Dynamic Binding | Manuel | Manuel | **✅ Self-Registration** |
| Threading Modeli | Tanımsız | Tanımsız | **✅ Hybrid Queue** |
| Async Cancellation | ❌ | ❌ | **✅ CancellationToken** |
| Hata Kurtarma | ❌ | ❌ | **✅ RecoveryDecision** |
| Plugin İzolasyonu | ❌ | ❌ | **✅ Capability Model** |
| Signal Akış Görselleştirme | ❌ | ❌ | **✅ Signal Explorer** |
| Determinizm Garantisi | ❌ | ❌ | **✅ Priority + tie-break** |
| Build Doğrulama | ❌ | ❌ | **✅ Build Validation** |
| GC Allocation | Düşük | Yüksek | **Steady-state 0** |
| IL2CPP | ✅ | ⚠️ | **✅ CI doğrulamalı** |
| Profiler | ❌ | ❌ | **✅ ProfilerMarker** |
| Hot Reload | Kısmi | Sorunlu | **✅ Domain Reload Safe** |
| Composite Trigger | ❌ | ❌ | **✅ Multi-signal→Command** |
| Test Harness | ❌ | Kısmi | **✅ NexusTestHarness** |
