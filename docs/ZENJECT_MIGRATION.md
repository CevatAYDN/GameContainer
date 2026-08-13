# Zenject → Nexus Geçiş Rehberi

Bu rehber, Zenject/Extenject kullanan bir projenin `com.nexus.core`'a taşınması için kavram eşlemesi ve adım adım yol verir. Nexus, Zenject'in *yerine* değil, sinyal/komut/observable/editor paketiyle *tamamlayan* bir MVCS çekirdeğidir — geçişte en çok iş, binding sözdizimi ve signal sistemi farkındadır.

---

## 1. Kavram Eşleme Tablosu

| Zenject / Extenject | Nexus | Not |
|---|---|---|
| `ProjectContext` / `SceneContext` | `Root` MonoBehaviour + `NexusRuntime` | Root, sahneye eklenen context giriş noktasıdır (`GameObject/Nexus/Create Root`) |
| `DiContainer` | `NexusDI` (`Context.Container`) | Aynı görev; `Context` bir `NexusDI` sahibidir |
| `Container.Bind<T>().AsSingle()` | `container.Bind<T>(isSingleton: true)` | |
| `Container.Bind<T>().AsTransient()` | `container.Bind<T>(isSingleton: false)` | |
| `Container.BindInstance(x)` | `container.BindInstance(x)` | Birebir aynı ad |
| `BindInterfacesAndSelfTo<T>()` | `BindInterfacesAndSelfTo<T>()` | Aynı ad; singleton paritesi garantili |
| `Container.BindFactory<T, TFactory>()` | `container.Bind<T>(isSingleton: false)` + `ProviderFactory` | Fabric sinyalleri Nexus'ta `ProviderFactory` ile |
| `[Inject]` constructor/field | `[Inject]` field (aynı) | Constructor enjeksiyonu Nexus'ta yoktur → **field injection kullanın** |
| `[Optional]` | `[OptionalInject]` | `SaveThrottler` örneğinde görüldüğü gibi |
| `IInitializable` / `IStartable` | `IStartable` / `IAsyncStartable` / `NexusService.InitializeAsync(ct)` | `IAsyncStartable` iptal belirteci taşır — Zenject'ten ileri |
| `SignalBus.Fire<T>()` (Zenject Signals) | `SignalBus.Fire<T>(signal)` (Nexus) | Nexus'ta sinyal **struct** olmalı (`where T : struct`) |
| `SignalBus.Subscribe<T>(handler)` | `SignalBus.RegisterCommand` + `ICommand<T>` | Nexus'ta handler'lar komut sınıflarıdır (komut kalıbı) |
| `[Inject] SignalBus` | `[Inject] SignalBus` | Aynı |
| `ZenjectBinding` (scene binding) | `NexusBinding` MonoBehaviour | Aynı fikir: sahneye sıfır kod bağlama |
| `IViewModel` / `Model` | `ObservableProperty<T>` / `SecureObservableProperty<T>` | Nexus'ta reactive model gömülü; UniRx gerekmez |
| `ITickable` / `IFixedTickable` | `ITickable` / `IFixedTickable` / `ILateTickable` | Aynı isimler |
| `IInitializable.Initialize()` | `IStartable.Start()` / `IAsyncStartable.StartAsync(ct)` | |
| `Disposable` / `IDisposable` | `IDisposable` + `OnDispose()` | Context teardown'ı non-blocking |

---

## 2. Adım Adım Geçiş

### Adım 1 — Envanter çıkar
Her `ZenjectBinding` ve her `Container.Bind` çağrısını listeleyin; yukarıdaki tabloyla eşleyin. Binding **sayısı** korunmalıdır — tek `BindInterfacesAndSelfTo` Nexus'ta aynı singleton'ı üretir, çoğaltmayın.

### Adım 2 — Root kurulumu
`ProjectContext` yerine: sahneye `Root` ekleyin (`GameObject/Nexus/Create Root`). Root'un `OnEnable`'ı root context'i otomatik kurar.

### Adım 3 — Binding sözdizimini çevir
```csharp
// Zenject
Container.Bind<IPlayerData>().To<PlayerData>().AsSingle();
Container.Bind<IMusicPlayer>().To<MusicPlayer>().AsTransient();
Container.BindInstance(_settings);

// Nexus (ContextBuilder içinde)
builder.Bind<IPlayerData, PlayerData>(isSingleton: true);
builder.Bind<IMusicPlayer, MusicPlayer>(isSingleton: false);
builder.BindInstance(_settings);
```
> **Constructor enjeksiyonu:** Nexus constructor injection **kullanmaz**. `[Inject]` field'lara taşıyın:
> ```csharp
> // Zenject: public PlayerService(IPlayerData data) { ... }
> // Nexus:
> [Inject] private IPlayerData _data;  // protected/public set edilebilir
> ```

### Adım 4 — Signal'leri çevir
```csharp
// Zenject: SignalBus.Subscribe<LevelUpSignal>(OnLevelUp); SignalBus.Fire(new LevelUpSignal(5));
// Nexus:
public struct LevelUpSignal { public int NewLevel; public LevelUpSignal(int level) { NewLevel = level; } }

// handler bir komut sınıfı olur:
public class ApplyLevelUp : ICommand<LevelUpSignal>
{
    [Inject] private IProgressionService _progression;
    public void Execute(LevelUpSignal signal) => _progression.SetLevel(signal.NewLevel);
}

// kayıt:
builder.BindCommand<LevelUpSignal, ApplyLevelUp>();
// veya [RegisterCommand(typeof(LevelUpSignal))] ile otomatik keşif
```
> **Kritik fark:** Zenject signal handler'ları metot aboneliğiydi; Nexus'ta handler = `ICommand<T>` sınıfı. Mevcut metot tabanlı mantığınız için bir `ActionCommand<T>` adapter'ı yazabilirsiniz (metodu saran `ICommand<T>`), ama komut sınıflarına geçiş zamanla temizlenir.

### Adım 5 — Reactive model'leri çevir
```csharp
// UniRx + Zenject: private readonly ReactiveProperty<int> _hp = new(100);
// Nexus:
public ObservableProperty<int> Health { get; } = new(100);
// View aboneliği: Health.OnChanged(OnHealthChanged); // OnUnbind'da RemoveOnChanged
```
> **Hijyen kuralı (NEXUS004 + WizardTemplateSyncTests kilitli):** abonelikler isimli handler + `RemoveOnChanged` ile kaldırılır; `ClearOnChanged` başka aboneleri siler, anonim lambda kaldırılamaz.

### Adım 6 — Servisleri taşı
`INexusService` implementasyonu + `builder.BindService<TInterface, TImpl>()`. `InitializeAsync(ct)` iptal belirteci alır — teardown'da hang yok. Zenject'in `IInitializable` senkron başlatması yerine tercih edilen yoldur.

### Adım 7 — View/Mediator'ları taşı
`ViewBinder` + `Mediator` tabanı: view MonoBehaviour'unuza `ViewBinder` ekleyin, `Mediator<TView>`'dan türeyin. `OnBind`/`OnUnbind` çifti, Zenject'te elle yapılan subscribe/unsubscribe akışının Nexus karşılığıdır — `OnUnbind`'da **kendi** aboneliğinizi kaldırın.

### Adım 8 — Teardown'u doğrula
Zenject'te `Dispose` senkron çağrılırdı; Nexus `OnDispose` üzerinden non-blocking teardown yapar. `async void` kullanmayın; teardown içinde `.Wait()/.Result` çağırmayın (sync-over-async deadlock).

---

## 3. Geçiş Doğrulama Listesi

- [ ] Her Zenject binding'inin Nexus karşılığı var (sayı eşitliği)
- [ ] Constructor injection → field injection tamamlandı
- [ ] Tüm signal handler'lar `ICommand<T>` (veya adapter) — abonelik sızıntısı yok
- [ ] Tüm `OnChanged` abonelikleri `RemoveOnChanged` ile kaldırılıyor
- [ ] `async void` yok; teardown'da sync-over-async yok
- [ ] `Editor` menüsü → `NexusArchitectureAnalyzer.RunHeadless` temiz (0 sorun)
- [ ] `BuildValidation.RunSilent()` geçiyor
- [ ] EditMode suite'leri 100% yeşil (NEXUS_READY Kapı 10)

---

## 4. Geçişte En Çok Yapılan 3 Hata

1. **`BindInterfacesAndSelfTo`'yu çoğaltmak** → aynı tipi birden çok singleton sanmak; Nexus'ta tek çağrı yeter, parite garantili.
2. **`ClearOnChanged` kullanmak** → başka view'ların aboneliğini siler; her zaman `RemoveOnChanged(handler)`.
3. **Sinyalleri class yapmak** → Nexus `where T : struct` zorunlu kılar; struct'a çevirin (değer tipi = sıfır GC + kutu yok).

---

## 5. Otomatik Geçiş Aracı (ileri adım)

Mevcut durumda resmî bir otomatik dönüştürücü yok; ancak `NexusArchitectureAnalyzer`'ın headless modu geçiş sonrası mimari kapı olarak CI'a eklenebilir. Zenject → Nexus binding sözdizimi farkı yüksek oranda mekanik olduğundan (tablo 1), büyük projelerde regex-tabanlı bir dönüştürücü betiği (her `Container.Bind<X>().To<Y>().AsSingle()` → `Bind<X, Y>(isSingleton: true)`) zaman kazandırır; adım adım taşıyıp her aşamada test koşmak daha güvenlidir.
