# Nexus ↔ StrangeIoC — Yapısal Karşılaştırma Raporu

Bu rapor, [The Big Strange HowTo](https://strangeioc.github.io/strangeioc/TheBigStrangeHowTo.html)
okunarak hazırlanmıştır. Amaç: Strange'in mimari yeteneklerini Nexus'un mevcut
durumuyla satır satır karşılaştırıp, **kapatılmış boşlukları** ve **bilinçli
tasarım farklarını** tek yerde toplamak. Her satırda "Nexus karşılığı", "durum"
ve "not" bulunur.

Önceki oturumlarda kapatılan boşluklar (ilgili kanıt testleriyle birlikte):

| Strange yeteneği | Nexus karşılığı | Durum |
|---|---|---|
| `Bind(anyKey).To(anyValue).ToName()/.ToSingleton()/.ToFactory()` | `NexusBinder<TKey,TValue>` fluent API | ✅ Kapalı (`BinderSuite` B1–B8) |
| `[Inject(SomeName)]` adlandırılmış enjeksiyon | `[Inject(Name = "...")]` + `Bind<T>(name)` / `Resolve<T>(name)` | ✅ Kapalı (`BinderSuite` DI1–DI6) |
| `[PostConstruct]` | `[PostConstruct]` (Order destekli) | ✅ Kapalı (`BinderSuite` P1–P3) |
| `[Deconstruct]` | `[Deconstruct]` (Order destekli, Dispose/DisposeAsync) | ✅ Kapalı (`BinderSuite` DC1) |
| `[Construct]` ctor işaretleme | `[Construct]` (alias, `[Inject]` ile birlikte sayılır) | ✅ Kapalı (`BinderSuite` CT1) |
| `Bind(X).To<Cmd>().Once()` | `BindCommandOnce` / `BindAsyncCommandOnce` / `.Once()` | ✅ Kapalı (`BinderSuite` ON1–ON3, ONC1–ONC2) |
| Polimorfik binding (`Bind<A>().Bind<B>().To<Impl>()`) | `BindMultiple<T1,T2[,T3],TImpl>()` (paylaşılan singleton) | ✅ Kapalı (`BinderSuite` PM1–PM3) |
| `crossContextInjectionBinder` | `BindCrossBoundary<TInterface,TImpl>()` + `ResolveCrossBoundary<T>()` | ✅ Kapalı (CB1–CB8) |
| `ToAbstraction<IMediator>()` | `[Mediator(typeof(Concrete), typeof(IMediator))]` | ✅ Kapalı (MA1–MA8) |
| `PostContexts` (tüm bağlamlar hazır olunca) | `IPostContextLifecycle` + `NexusRuntime.FinalizeInitializationAsync()` | ✅ Kapalı (PC1–PC5) |

Aşağıdaki bölümler Strange'in **hâlâ farklı** olduğu alanları ve Nexus'un bu
alanlardaki mevcut karşılıklarını inceler. Bu alanların hiçbiri "eksik" değildir;
bazıları bilinçli olarak farklı tasarlanmıştır.

---

## 1. Cross-context iletişim

### StrangeIoC
- `crossContextInjectionBinder` — bağlamlar arası DI paylaşımı.
- `crossContextDispatcher` + `Broadcast()` — bağlamlar arası olay yayını.
- Bağlamlar hiyerarşik/ebeveyn zinciriyle ilişkilidir; alt bağlam üstünün
  bağlamalarına erişebilir.

### Nexus
- `[CrossContext(scopeTag)]` attribute'u — sinyalin başka bağlama yayınlanacağını
  işaretler (CommandRegistry'de cache'li tespit).
- `SignalBus.BroadcastCrossContext<T>(signal, scopeTag)` — aktif bağlamları
  `IContextResolver` üzerinden alır, kendini atlar, eşleşen `ScopeTag`'lere
  `FireCrossContext` ile iletir (case-insensitive eşleşme, BUG-5 düzeltmesi).
- `NexusRuntime.GetContexts(scopeTag)` / `GetContext(scopeTag)` — isimle bağlam
  bulma; `ActiveContexts` thread-safe anlık görüntü sağlar.

**Durum:** ✅ İşlevsel karşılık mevcut — ama **kavramsal fark var**: Strange
"her şey paylaşılabilir" derken (cross-context binder her şeyi taşır), Nexus
yalnızca `[CrossContext]` işaretli sinyalleri yayınlar (bilinçli, az-ayrıcalık
tasarımı). DI nesnelerinin bağlamlar arası paylaşımı için Nexus'ta doğrudan bir
`crossContextInjectionBinder` **yoktur**; bunun yerine paylaşılacak servisler
ortak bir üst bağlamda ya da singleton olarak kaydedilir.

> **Öneri:** Gerçekten ihtiyaç duyulursa (ör. çok oyunculu oturum paylaşımı)
> `IContextResolver` üzerinden hedefli "cross-context resolve" API'si eklenebilir.
> Bu, Strange'in genel binder'ına göre Nexus'un "az ayrıcalık" felsefesiyle
> uyumlu bir uzantı olur.

---

## 2. Mediator yaşam döngüsü ve auto-wiring

### StrangeIoC
- `OnRegister()` / `OnRemove()` — mediator bağlanınca/çözülünce çağrılan hook'lar.
- `mediationBinder.Bind<View>().To<Mediator>()` — view→mediator haritası.
- `ToAbstraction<IMediator>()` — interface üzerinden bağlama.
- `SignalMediator` — sinyallere doğrudan abone olan mediator tabanı.

### Nexus
- `Mediator<TView>` — `OnBind()` / `OnUnbind()` hook'ları; `View` (cast'li),
  `[Inject] ISignalBus SignalBus`, `Subscribe<T>()` (oto-dispose'lu), `IsViewValid`,
  `ExecuteIfViewValid`.
- `[Mediator(typeof(TMediator))]` attribute'u + `ViewBinder.RegisterView` —
  view OnEnable olunca otomatik kayıt, mediator havuzdan alınır/çözülür,
  `mediator.Bind(view, bus)` çağrılır. **Inject view'a Bind'den ÖNCE yapılır**
  (OnBind içinde `[Inject]` servislere erişim garantisi).
- Mediator **havuzlama**: `IResettable.Reset()` her pool pop/return'de çağrılır;
  `ClearInjectedReferences` pool return'de temizler; `OnReset()` türemiş durumu
  temizler (idempotent sözleşme). `PoolPopCount`/`PoolLeakWarnings` telemetrisi var.
- `ScreenMediator<TView>` — ekran yaşam döngüsü hook'ları (`OnScreenOpened`/
  `OnScreenClosed`) üzerine ekler; `ScreenView` → `View` türevi, UIManager
  katmanlama/pooling yönetir, mediator'ü elle dokunmaz.

**Durum:** ✅ İşlevsel karşılık mevcut. İsimler farklı (`OnBind`/`OnUnbind`
vs `OnRegister`/`OnRemove`), ama sözleşme aynı: view bağlanınca hook, çözülünce
temizlik. Nexus'un **fazlası**: havuzlama + sızıntı telemetrisi + view
geçerlilik koruması (`IsViewValid`).

> ~~**Not:** Strange'in `ToAbstraction` (interface üzerinden mediator bağlama)
> karşılığı Nexus'ta yok — mediator'ler concrete tiplerle bağlanır. Gerçek
> ihtiyaç hâlinde `[Mediator(typeof(...))]`'a interface desteği eklenebilir,
> ama mevcut kullanımda concrete yeterlidir.~~

---

## 3. `[Deconstruct]` ve temizlik sözleşmesi

### StrangeIoC
- `[Deconstruct]` — nesne yok edilirken çalışan temizlik hook'u.
- Sıralama garantisi vermez; dependency'lerin durumu garanti edilmez.

### Nexus
- `[Deconstruct(Order)]` — `Dispose()` ve `DisposeAsync()` içinde, `IDisposable.
  Dispose`'tan **önce**, artan `Order` sırasıyla çalışır.
- **Tam-tam garantisi:** singleton'lar instance-keyed `HashSet` + `alreadyDisposed`
  guard ile izlenir; paylaşılan (polimorfik) singleton bile **bir kez**
  deconstruct edilir. Bir `[Deconstruct]` throw ederse diğerleri yine çalışır
  (per-method try/catch + log).
- **Dependency state:** Nexus'te deconstruct, `Dispose` döngüsünün içinde, DI
  bindings temizlenmeden önce çalışır — yani dependency'ler **hâlâ non-null**'dır
  (`[Deconstruct]` içinde inject edilmiş servislere güvenle erişilebilir).

**Durum:** ✅ Kapalı ve Strange'ten **daha güçlü** (sıralı, tam-tam garantili,
dependency'ler canlı).

---

## 4. Context yaşam döngüsü fazları

### StrangeIoC
- `ContextStart` → `ContextDestroy` + `PostContexts` (çoğul bağlam sıralaması).
- `ContextView` — bağlamın MonoBehaviour görünümü.

### Nexus
- `IContextLifecycle`: `OnConfigure(builder)` → `OnStartAsync(ct)` → dispose
  edilirken ters kayıt sırası (services).
- `Context.Configure()` auto-discovery (convention ile lifecycle bulma) + elle
  kayıt; `NexusRuntime.CreatePureContextAsync` — sahne gerektirmeyen code-only
  bağlam (test/dedicated server).
- `Root` MonoBehaviour — bağlamın sahne görünümü; view kayıtları Root discovery
  üzerinden çalışır.

**Durum:** ✅ İşlevsel karşılık mevcut. Faz adları farklı ama sıralama sözleşmesi
aynı. `PostContexts` için **Phase 3 — `IPostContextLifecycle`**: `IContextLifecycle`'dan ayrı
bir `IPostContextLifecycle` interface'i tanımlanmıştır (`OnPostContext(builder)`). `Context.Configure()`
içinde tüm `IContextLifecycle` örnekleri `IPostContextLifecycle`'a cast edilip ayrı bir listede
saklanır. `NexusRuntime.FinalizeInitializationAsync()` tüm aktif bağlamları dolaşır ve her birinin
`RunPostContextAsync()`'ini çağırır. Standart lifecycle tamamlandıktan sonra (OnConfigure →
OnInitialize → OnStart), PostContext fazı ateşlenir. **5 harness testi ile kanıtlandı (PC1–PC5)**. (MA1–MA8).

---

## 5. Signal sistemi

### StrangeIoC
- `Signal<T>` sınıfları + `SignalCommandBinder` — sinyal→komut eşlemesi.
- `signal-to-command` payload'lı bağlama.

### Nexus
- `SignalBus.Fire<T>` / `FireAsync<T>` / `FireAsyncWithTimeout` — generic struct
  sinyaller, sync/async ayrımı (mismatch koruması), command + subscription faz
  ayrımı (önce komutlar, sonra subscriber'lar gözlemler).
- `BindCommand<TSignal,TCommand>` / `BindSignal<TSignal>().To<TCommand>()` /
  `.Once()` / composite (`[CompositeSignalHandler]`).
- **Fark:** Strange sinyalleri sınıf olarak tanımlar; Nexus **struct** olarak
  (GC'siz hot path). Nexus'ta sinyal tipleri taşınabilir value type'lardır.

**Durum:** ✅ Kapalı; GC farkı bilinçli (struct sinyaller + havuzlama).

---

## 6. Binder esnekliği — Strange'in kalan avantajı

Strange'in `Bind(anything).To(anything)` genelliği, Nexus'un tipe-özel
`BindService`/`BindReactiveModel`/`BindMultiple` API'lerinden daha serbesttir.
Nexus bu genelliği `NexusBinder<TKey,TValue>` (`BinderSuite` B1–B8) ile geri kazanmıştır:
MVCS dışı kataloglar, config tabloları, entity tanımları bu binder'da yaşar;
MVCS içi bağlamalar tipe-özel, denetimli API'lerle kalır.

**Durum:** ✅ Kapalı — genellik korunurken disiplin (MVCS dışı = NexusBinder,
MVCS içi = tipe-özel API) korunuyor.

---

## 7. Nexus'un Strange'te olmayan güçleri

- **Zero-GC hot path:** struct sinyaller, havuzlu komut/mediator/subscription
  düğümleri, snapshot-read-copy (okuma yolları kilitlenmeden thread-safe).
- **Async first-class:** `FireAsync`, `IAsyncCommand<T>`, timeout + cancellation,
  sequential/parallel dispatch garantileri.
- **Havuzlama + telemetri:** mediator/subscription havuzları, sızıntı uyarıları,
  `Metrics` (sinyal/komut oranları).
- **Fault tolerance:** recovery engine, interceptor'lar, error collection.
- **Harness ile kanıt:** `tools/nexus-benchmark` — 9 doğrulama süiti + benchmark grubu, tam mimarinin
  çalışır kanıtı (benchmarks, recovery, stress, fuzz, cross-thread, game-session,
  services, binder, registry, concurrent-diff).

---

## Özet tablo

| Alan | Strange | Nexus | Durum |
|---|---|---|---|
| Genel Binder | `Bind(any).To(any)` | `NexusBinder<TKey,TValue>` | ✅ Kapalı |
| Named injection | `[Inject(Name)]` | `[Inject(Name=)]` strict | ✅ Kapalı |
| `[PostConstruct]` | var | var (Order) | ✅ Kapalı |
| `[Deconstruct]` | var (sırasız) | var (sıralı, tam-tam) | ✅ Kapalı + güçlü |
| `[Construct]` | var | var (alias) | ✅ Kapalı |
| `.Once()` | var | var (race-safe) | ✅ Kapalı |
| Polimorfik binding | var | var (paylaşılan singleton) | ✅ Kapalı |
| Cross-context | binder + dispatcher | `[CrossContext]` + scope tag / `BindCrossBoundary<T>` + `ResolveCrossBoundary<T>` | ✅ Kapalı |
| Mediator lifecycle | OnRegister/OnRemove / ToAbstraction | OnBind/OnUnbind + pooling / `[Mediator(TMediator, TIAbstraction)]` | ✅ Kapalı + güçlü |
| Signal | sınıf bazlı | struct bazlı (GC'siz) | ✅ Farklı tasarım |
| PostContexts | tüm bağlamlar hazır olunca global faz | `IPostContextLifecycle` + `NexusRuntime.FinalizeInitializationAsync()` | ✅ Kapalı (PC1–PC5) |
| Async | sınırlı | first-class | ✅ Nexus üstün |
| Havuzlama/telemetri | yok | kapsamlı | ✅ Nexus üstün |

**Sonuç:** Kapatılabilir boşlukların tamamı kapatıldı ve harness ile kanıtlandı.
Kalan farklar ya bilinçli tasarım kararı (struct sinyaller, az-ayrıcalık
cross-context) ya da Nexus'un zaten üstün olduğu alanlardır (havuzlama, async,
telemetri).
