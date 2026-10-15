# Nexus — Objektif Durum Analizi ve Rekabetçi Yol Haritası (2026-08-13)

> **Amaç:** "Zenject, VContainer, StrangeIoC'un yerini alacak; kolay entegre, kolay kullanılır,
> gelişmiş, performanslı, okunabilir bir proje" hedefi için **kanıt bazlı** durum tespiti ve
> öncelikli plan.
> **Yöntem:** Tüm iddialar kaynak kod, benchmark harness, NEXUS_READY kapıları ve mevcut
> dokümanlarla çapraz doğrulandı. Dosya:satır referansları gerçektir.

> **Uygulama durumu (2026-08-13):** Faz 0 kapsamındaki doküman tutarsızlıkları düzeltildi
> (E1: `ZENJECT_MIGRATION.md` ctor injection beyanı koda eşlendi; `STRANGE_COMPARISON.md`
> cross-boundary çelişkisi giderildi; canonical dokümanların "breaks/cannot set" ifadeleri
> doğru mekanikle yeniden yazıldı; quickstart en+TR ve README örnekleri canonical stile çevrildi).
> **Faz 1 çekirdeği uygulandı:** `Lifetime` enum'u (Singleton/Scoped/Transient) + tüm bind
> overload'ları, `NexusDI.CreateChildScope(configure)`, ters-oluşturma-sıralı disposal ve
> **fluent zincir API** (`BindFluent<T>().To/AsSingleton/AsScoped/AsTransient/AsSingle/
> AsCached/AsImplementedInterfaces/AndSelf/WithParameter`) —
> `tools/nexus-benchmark/LifetimeScopeSuite.cs` (LT1–LT9) + `NewFeatureSuite.cs` (FL1–FL7,
> SF1–SF5, CF1–CF4) ile kanıtlandı. Kalan Faz 1 işleri: `IFactory<T>` tamamlama, prefab
> component injection.
> **Faz 2 codegen ayağı uygulandı** (`NexusCodeGenerator` → `NexusDI.RegisterConstructorFactory<T>`,
> CF1–CF4, CF5); harness kabul kapısı **CG1 + CG2 ile kapatıldı** (2026-08-13): gerçek
> `NexusCodeGenerator` çalıştırılıp ürettiği binder Roslyn ile derleniyor ve uçtan uca boot
> ediliyor (CG1); aynı kapılar **Roslyn Source Generator** (`NexusBinderGenerator`,
> `com.nexus.core/SourceGenerator~`) için de açıldı — ürettiği binder `CSharpGeneratorDriver`
> ile derleniyor, boot ediliyor ve uçtan uca resolve ediliyor (CG2, geçerli-derleme + başvurulan
> derleme yollarında). Bu turda ayrıca kodgen'in gerçek hataları düzeltildi: compiler-generated
> closure tipleri (`<>c__DisplayClass*`) ve non-visible tipler (private/internal nested) artık
> binder'a emite edilmiyor (CS1001/CS0122); `WithParameter` override'ları kayıtlı ctor factory
> varken artık sessizce yutulmuyor (CF4); `[PostConstruct]` kayıtlı injector varken artık
> atlanmıyor (runtime garantisi, CG1/CG2 fixture'ları); SG, metadata (başvurulan) tipler için
> injector üretmiyor — Roslyn metadata `GetMembers()` private/internal üyeleri gizlediği için
> kısmi injector sessiz bozulma yaratırdı; her derleme kendi binder'ını üretir.
> Kalan kapı: **Unity IL2CPP build doğrulaması** (`tools/unity-verify`, Unity makinesi gerektirir;
> Mode B'de SG binder'ı da doğrulanır). **Faz 4 uygulandı**
> (`ISignalFilter<T>` ref-based pipeline, SF1–SF5).
> **✅ Harness doğrulaması tamamlandı (2026-08-13):** `dotnet` engeli çözüldü (kökteki
> bozukluk: başarısız bir servicing geçişi 10.0.11 bileşen setini sıfırlamış — hostfxr, runtime,
> SDK 10.0.303 ve ref pack'ler; `C:\Program Files\dotnet` onarımı admin gerektirir. Geçici
> çözüm: sağlam sürümlerin (10.0.9/9.0.16/8.0.27) junction ağacı olan `~/.dotnet-fixed` +
> kopyalanan muxer). Koşu: `DOTNET_ROOT` yok — `~/.dotnet-fixed/dotnet.exe` ile
> `build tools/nexus-benchmark -c Release` → **tüm suite'ler yeşil**: LT1–LT9, FL1–FL7,
> SF1–SF5, CF1–CF4, CF5, CodeGen (CG1 + CG2), 50/50 Architecture Stress, GCAudit, ServiceGraph,
> Fuzz, CrossThread, TeardownLeak, FixVerify, Evidence. Bu turda ayrıca tespit edilip düzeltildi: harness
> `NEXUS_DEBUG` tanımı gerçek tracing ile çelişiyordu (AsyncLocal set + TraceEvent
> event başına ~896 B tahsis ediyor — tüm 0-GC audit ailesi bu yüzden kırmızıydı; E5 ve
> Stress-27 zaten compiled-out kontratını test ediyor) — define kaldırıldı, stub'lar
> (`ProfilerMarker` namespace'i, `NexusProfiler` stand-in, `Time.unscaledTime`) düzeltildi.

---

## 1. Yönetici Özeti

- Nexus teknik olarak **olgun bir çekirdek**: 86/86 runtime dosyası harness'ta derleniyor,
  0-GC disiplini ölçülüyor, AOT binder üretimi var, 16 editor plugin'i, üç denetim turu
  geçmişi. "Kalite sorunu" yok.
- Sorun **konumlanma ve kapsam**: "VContainer Killer" çerçevesi doğrulanmıyor; mevcut
  dokümanların bazıları **kodu yanlış anlatıyor** (constructor injection); API ergonomisi
  (fluent binding, `Lifetime`, fabrika, prefab injection) ve ekosistem köprüleri
  (UniTask, ağ transport, IAP/Analytics adaptörleri) eksik.
- "Üç framework'ün yerini almak" = **özellik birleşimi + göç köprüleri**. Nexus'un
  gerçek kazanma ekseni: entegre 0-GC sinyal/komut/recovery, editor araçları ve
  Zenject'in bakımsızlığından doğan göç talebi.
- Bu plan 7 fazdır; **Faz 0 (doğruluk/hijyen) + Faz 1 (API + scope) + Faz 4 (sinyal
  filtresi)** kısa vadede en yüksek değer/maliyet oranına sahiptir. Modüler paketleme
  (Faz 5) v1.0 öncesi yapılmalıdır.

---

## 2. Objektif Durum Tespiti

### 2.1 Kanıtlı güçlü yanlar

| Güç | Kanıt |
|---|---|
| 0-GC hot-path disiplini | `SignalBus` sync/async ayrımı, buffer pooling, `CompiledAccessorEmitter`, `tools/nexus-benchmark` (86/86 runtime dosyası, soak + fuzz + differential suite'ler) |
| AOT/IL2CPP güvenli DI | `Editor/CodeGen/NexusCodeGenerator.cs` → `NexusGeneratedBinder.g.cs` + link.xml üretimi, `[Preserve]` disiplini, `tools/unity-verify` (Mono + IL2CPP build doğrulaması) |
| StrangeIoC yüzeyi | named bindings, `BindMultiple`, cross-boundary, `[Construct]`/`[PostConstruct]`/`[Deconstruct]`, `ICommand<T>`, `Mediator<TView>` + havuzlu `ViewBinder`, `NexusBinder` |
| Zenject/VContainer yüzeyinin parçaları | `BindInterfacesAndSelfTo` (`ContextBuilder.cs:130`), `ITickable`/`IStartable`, `Root`+`Context` (scene context), child context + scoped teardown, `[OptionalInject]`, `LazyInjection` |
| Editor paketi (16 plugin) | ContextInspector, GameManager, Performance/Network/Error dashboards, Tracer+Graph, FSM, Wizard, LiveReload, Scene overlay, Profiler modülü |
| Güvenlik katmanı | AES-256+HMAC storage, SecureObservable (XOR + canary), hardware-tick anti-cheat, captive-dependency validation |
| Dokümantasyon kültürü | AUDIT/REVIEW raporları, NEXUS_READY kapıları, SLO, ZENJECT_MIGRATION, ADR'ler, AI-agent dostu README |

### 2.2 Kanıtlı eksikler ve hatalar

| # | Eksik | Kanıt / Gerçek |
|---|---|---|
| E1 | **Doküman-kod çelişkisi (kritik):** `ZENJECT_MIGRATION.md` "Constructor enjeksiyonu Nexus'ta yoktur → field injection kullanın" diyor; **kodda ctor injection var** (`NexusDI.cs` `ConstructAttribute` kontrolü, `Injector.CreateInstance` → `meta.Constructor.Invoke(args)`; `TROUBLESHOOTING.md` de çalıştığını belgeliyor). Göç eden geliştiriciyi yanlış yola sokar; "VContainer Killer" raporunun "ctor injection YOK" iddiasının kaynağı budur. | ✅ **Düzeltildi (2026-08-13)** — `ZENJECT_MIGRATION.md` koda eşlendi; `STRANGE_COMPARISON.md` cross-boundary bölümü ve canonical dokümanların mekanik iddiaları da düzeltildi |
| E2 | `Lifetime` enum'u ve `CreateChildScope` API'si yok; `isSingleton: bool` + `BindFactory`. Scoped davranış child-container disposal ile fiilen var ama açık API yok. | ✅ **Uygulandı (2026-08-13)** — `Lifetime.Singleton/Scoped/Transient` + tüm bind overload'ları + `CreateChildScope(configure)` + ters-oluşturma-sıralı disposal; `LifetimeScopeSuite` LT1–LT9 |
| E3 | Fluent binding zinciri yok (`Bind<T>().AsSingle().AsImplementedInterfaces()...`); API `Bind<I,T>(bool)` düzeyinde. | `NexusDI.cs` |
| E4 | `IFactory<T>` / `PlaceholderFactory` yok; prefab instantiation + component injection (`Container.Instantiate(prefab)`) yok. | arama: sadece servislerin içinde `Object.Instantiate` |
| E5 | AOT binder **constructor fabrikası üretmiyor** (field/property/method injector + generic dispatcher üretiyor). | `NexusCodeGenerator.cs` |
| E6 | Sinyal interceptor pipeline var ama **object tabanlı** (`ISignalInterceptor.Intercept(ref object)` → interceptor varken struct box'lanır, 0-GC iddiası koşullu). | `PluginSystem.cs:48`, `SignalBus.cs` (kod bunu kendisi belgeliyor) |
| E7 | Ekosistem köprüleri yok: UniTask, DOTween, Addressables, ağ transport, IAP/Analytics adaptörleri — **sıfır referans**. | STUDIO-EVALUATION grep |
| E8 | Karşılaştırmalı benchmark yok: harness'ta VContainer/Zenject/MessagePipe hiç geçmiyor. | `tools/nexus-benchmark` |
| E9 | Monolitik paket: `com.nexus.core` içinde DI+FSM+Netcode+DOTS+Services+Debug hepsi. | `package.json` + `Runtime/` (19 alt dizin) |
| E10 | CI pasif (Kapı 3/5), Unity test sonuçları arşivlenmiyor (Kapı 4), cihaz soak yok (Kapı 7). | `NEXUS_READY.md` |
| E11 | Harness'ın Unity dışı çalışması için `dotnet` gerekiyor; STUDIO-EVALUATION bu makinede bozuk olduğunu not etmiş — doğrulama zinciri tek noktaya bağımlı. | `docs/STUDIO-EVALUATION-2026-08-13.md` |

---

## 3. Üç Framework'e Karşı Kapsam Matrisi

| Yetenek | Zenject | VContainer | StrangeIoC | Nexus | Boşluk |
|---|---|---|---|---|---|
| Constructor injection | ✅ | ✅ | ✅ (Construct) | ✅ (reflection) | compiled ctor yok (E5) |
| Field/Property/Method injection | ✅ | ✅ | ✅ | ✅ | — |
| Fluent binding zinciri | ✅ | ✅ | ✅ | ⚠️ `Bind<I,T>(bool)` | E3 |
| Lifetime (Singleton/Scoped/Transient) | ✅ | ✅ | ⚠️ | ⚠️ isSingleton+factory | E2 |
| Child scope + otomatik dispose | ✅ | ✅ | ⚠️ | ⚠️ child context var, API yok | E2 |
| Fabrika (`IFactory<T>`) | ✅ | ✅ (RegisterFactory) | ✅ (IPool) | ⚠️ `BindFactory` | E4 |
| Prefab/component binding + injection | ✅ | ✅ | ✅ | ❌ | E4 |
| Entry point'ler (IStartable/ITickable) | ✅ | ✅ | ⚠️ | ✅ | — |
| Sinyal bus + command + recovery | ek paket | ek paket (MessagePipe) | ✅ | ✅ (entegre, 0-GC) | güçlü nokta |
| Sinyal filtresi/middleware | ❌ | ek paket (MessagePipe filter) | ❌ | ⚠️ object-tabanlı interceptor | E6 |
| Mediator/MVCS | ❌ | ❌ | ✅ | ✅ | güçlü nokta |
| AOT/IL2CPP codegen | ❌ | ✅ (Source Generator) | ❌ | ✅ (editor-time) | E5 (ctor) |
| Editor/teşhis araçları | ❌ | ❌ | ❌ | ✅ (16 plugin) | güçlü nokta |
| UniTask interop | ❌ | ✅ | ❌ | ❌ | E7 |
| Göç köprüsü (Zenject) | — | ❌ | ❌ | ⚠️ doküman var, araç yok | E1 (yanlış doküman!) |

**Okuma:** Bu matris Faz 1/2/4 **öncesi** başlangıç durumudur; kapatılan hücreler için güncel,
kanıt-atıflı durum **§9 Düzeltilmiş Rekabet Matrisi**'nde. Nexus StrangeIoC'u kapsıyor,
Zenject'in çoğunu kapsıyor (göç köprüsü + araçlar eksik), VContainer'a karşı API ergonomisi
(E2-E4) ve codegen (E5) Faz 1/2/4'te kapatıldı; kalan: `IFactory<T>`, prefab injection,
Unity IL2CPP build kapısı. "Yerini alma" iddiası ancak Faz 1-2-5-7 birlikte tamamlanınca
savunulabilir.

---

## 4. Stratejik Konumlanma

1. **Kazanma ekseni "saf DI" değil.** VContainer saf DI'da API olgunluğu, doküman ve
   toplulukla kazanır. Nexus'un savunulabilir farkı: **entegre 0-GC sinyal/komut/recovery +
   editor araçları + gömülü servisler** — rakiplerin hiçbirinde tek pakette yok.
2. **En büyük pazar fırsatı Zenject'in ölümü.** Zenject 2020'den beri bakımsız; stüdyolar
   taşınmak zorunda. Doğru "Zenject → Nexus" göç aracı (dönüştürücü + test kiti) en
   hızlı adoption köprüsüdür. Ama önce E1 (yanlış doküman) düzeltilmeli.
3. **VContainer'a karşı tek dürüst hamle:** API yüzeyini kapat (Faz 1-2) ve kendi
   güçlü yanlarını (0-GC sinyal, editor) kanıtla. "VContainer Killer" dili pazarlama
   değil, itibar riskidir — kullanmayın.
4. **StrangeIoC zaten kapsanıyor** — doküman + örnekle resmiyet kazandırın.

---

## 5. Yol Haritası (Öncelik Sıralı)

> Her fazın kabul kriteri: **`tools/nexus-benchmark` tam pipeline yeşil + ilgili yeni suite
> eklenmiş + 0-GC kontratları bozulmamış.**

### Faz 0 — Doğruluk & Hijyen (1 hafta) — hemen başla
- **E1 düzelt:** `ZENJECT_MIGRATION.md`'deki "ctor injection yoktur" beyanını kodla
  eşle; ctor injection'ı `[Inject]`/`[Construct]` işaretli veya tek-ctor kuralıyla
  belgele. Aynı drift'i `10_MIN_QUICKSTART`, `BREAKING_CHANGES.md` ve README'de tara.
- Doküman-kod tutarlılığını otomatikleştir: `NexusArchitectureAnalyzer`'a
  (veya ayrı bir edit-mode testine) "dokümanda iddia edilen API koddan kopuk mu" kontrolü.
- `tools/nexus-benchmark`'ı bu makinede çalıştırılabilir yap (`dotnet` sorununu çöz);
  sonucu `NEXUS_READY.md`'ye arşivle.
- **CI kararı:** GitHub Actions'ı yeniden aktifleştir (Kapı 3) veya self-hosted runner
  (Kapı 5). Unity test sonuçlarını arşivlemeye başla (Kapı 4).

### Faz 1 — API Ergonomisi & Scope (2-3 hafta)
- `Lifetime` enum (`Singleton`/`Scoped`/`Transient`) + `CreateChildScope` +
  **ters sıralı** scoped disposal (VContainer garantisi; `NexusDI.Dispose` şu an
  HashSet sırasıyla geziyor).
- ✅ Fluent binding zinciri: `BindFluent<T>().To/AsSingle/AsTransient/AsImplementedInterfaces
  /AndSelf/WithParameter` — mevcut metotların üzerine **geriye uyumlu** katman (FL1–FL7).
  `AsSingle`/`AsCached` Zenject/VContainer adları Scoped'a eşlenir; `BindFluent<T>()` tek
  başına hiçbir şey kaydetmez.
- ⏳ `IFactory<T>` tamamlama; `Container.Instantiate(prefab)` + prefab component injection.
- ✅ Yeni harness suite'leri: `LifetimeScopeSuite` (LT1–LT9), `NewFeatureSuite` (FL1–FL7,
  SF1–SF5, CF1–CF4, CF5), `CodeGenSuite` (CG1 + CG2). Kalan: `FactorySuite`,
  `PrefabInjectionSuite`.

### Faz 2 — AOT Constructor Fabrikaları (3-4 hafta)
- ✅ **Önce:** `NexusCodeGenerator` ctor'lar için `NexusDI.RegisterConstructorFactory<T>`
  üretiyor (tek public ctor veya tek `[Inject]`/`[Construct]` işaretli ctor; değer-tipli
  parametre, generic tanımlar, compiler-generated ve non-visible tipler reflection'a düşer).
  Runtime tarafı `ResolveConstructorParameter<T>` ile strict/warn semantiğini birebir
  koruyor (CF1–CF4; CF4 = `WithParameter` override'ının factory'ye üstünlüğü).
  **CG1 (2026-08-13):** gerçek kodgen çalıştırılıp ürettiği binder Roslyn ile derleniyor ve
  boot ediliyor — kodgen çıktısının geçerli C# olduğu harness içinde kanıtlanıyor.
- ✅ **Roslyn Source Generator'a geçiş değerlendirildi ve uygulandı** (2026-08-13):
  `NexusBinderGenerator` (`SourceGenerator~/NexusBinderGenerator.cs`, Roslyn **4.10**'a
  pinli — Unity 6000.5'in derleyicisi; netstandard2.0, paket içinde `SourceGenerators~/`
  olarak dağıtılıyor). CG1 kapılarını birebir korur: görünürlük kapısı, compiler-generated
  atlama, değer-tipli parametre atlama, all-or-nothing injector, `WithParameter` üstünlüğü.
  Fark (bilinçli): injector/clearer **yalnızca geçerli derleme** tipleri için üretilir;
  başvurulan (metadata) tipler yalnızca ctor fabrikası + dispatcher alır — Roslyn metadata
  `GetMembers()` private/internal üyeleri gizler, kısmi injector reflection yolunu sessizce
  devre dışı bırakırdı. Her asmdef kendi derlemesinde kendi binder'ını üretir.
  **CG2 (2026-08-13):** SG'nin ürettiği binder `CSharpGeneratorDriver` ile derleniyor, boot
  ediliyor ve uçtan uca resolve ediliyor (geçerli-derleme source yolu + başvurulan derleme
  yolu; chain-walk base injector, `[PostConstruct]`, `WithParameter` üstünlüğü dahil).
  **Yan düzeltmeler:** kayıtlı (AOT) injector varken `[PostConstruct]` artık çalışıyor
  (runtime garantisi); `NexusDI` audit'inde `ExternalAdapter` yerel binding'leri sessizce
  gölgeliyordu (CF5 — yerel binding artık kazanır, adapter yalnızca delegasyonu sahiplenir).
- Kabul: Unity IL2CPP build doğrulaması (`tools/unity-verify`, Unity makinesi). Harness
  ayağı kapandı: CG1 (editor codegen) + CG2 (Source Generator) üretilen binder'ları
  derleyip boot ediyor (Roslyn, SDK içinden — NuGet bağımlılığı yok). Unity tarafında
  editör binder'ı varsayılan dört adımlık pipeline ile, SG binder'ı `README`'deki Mode B
  ile doğrulanır.

### Faz 3 — Ekosistem Köprüleri (4-6 hafta)
- UniTask köprüsü: `#if NEXUS_UNITASK` (çekirdeğe opsiyonel) veya `com.nexus.unitask`
  köprü paketi; `IAsyncStartable`/`IAsyncDisposable` UniTask döngüsüne bağlanır.
- Ağ transport örnek adaptörleri (Mirror / FishNet / Photon) — `INetworkAdapter` üzerine.
- IAP/Analytics referans adaptörleri (Unity IAP, GameAnalytics/Adjust).
- Addressables: mevcut dokümanın (`ADDRESSABLES_ADAPTER.md`) üstüne çalışan örnek.
- Kural: **çekirdek paket hiçbirine bağımlı olmaz**; her köprü ayrı paket/örnek.

### Faz 4 — Sıfır-Boxing Sinyal Filtresi (2-3 hafta, Faz 1 ile paralel)
- ✅ `ISignalFilter<T> { bool OnFilter(ref T signal); }` — generic, ref-tabanlı,
  boxing'siz; sync (`Fire`) ve async (`FireAsync`) yollarda interceptor/komut/abonelikten
  **önce** çalışır; `false` döndürmek sinyali iptal eder, `ref` ile mutasyon yapılabilir
  (SF1–SF5). Mevcut object-tabanlı `ISignalInterceptor` korunur; kayıtlıyken tek box
  davranışı belgelidir.
- ✅ İptal zinciri + sıra testleri (`NewFeatureSuite` SF1, SF4).
- ✅ Kabul: `GCAuditSuite` dahil tüm harness yeşil (dotnet engeli `~/.dotnet-fixed` ile aşıldı;
  filter varken 0 B davranışı SF1–SF5 + GCAudit'te doğrulanıyor).

### Faz 5 — Modüler Paketleme (v1.0 öncesi, 4-6 hafta)
- Ayrım: `com.nexus.di` → `com.nexus.signals` → `com.nexus.mvcs` →
  `com.nexus.services` → `com.nexus.tools` (editor-only).
- Bağımlılık grafiği tek yönlü; `com.nexus.core` eski sürümler için **meta-package**
  olarak kalır (0.4.0 kullanıcıları kırılmaz).
- Kabul: yalnızca `com.nexus.di` ile derlenen örnek proje; meta-package geçiş testi.
- ⚠️ Bu işi v1.0'dan sonraya bırakmayın — breaking change'i 1.0 öncesi yapmak ucuzdur.

### Faz 6 — Kanıt: Karşılaştırmalı Benchmark (sürekli)
- Harness'a VContainer + Zenject kaynaklarını da derleyip **aynı makinede** ölçen bir
  karşılaştırma modu ekle (harness zaten "gerçek kaynakları derle" tekniğini kullanıyor).
- Metrikler: 100k resolve süresi + GC, 100k sinyal fırlatma GC, scoped teardown sırası.
- **Dürüstlük kuralı:** sonucu görmeden yayınlama; kaybetme ihtimali olan bir grafiği
  önceden README'ye koymak kalıcı hasar bırakır.

### Faz 7 — Adoption Köprüleri (v1.0 ile birlikte)
- Otomatik Zenject→Nexus dönüştürücü (Roslyn/regex tabanlı; `ZENJECT_MIGRATION.md`
  Bölüm 5'te ileri adım olarak zaten öngörülmüş).
- VContainer→Nexus ve StrangeIoC→Nexus eşleme dokümanları (ZENJECT_MIGRATION formatında).
- Mini oyun örneği (servis katmanını uçtan uca çalıştıran; STUDIO-EVALUATION P1-8).
- **Differential migration kiti:** Zenject davranışını kopyalayan testlerin Nexus
  üzerinde yeşil olduğunu kanıtlayan suite — göç eden stüdyoya "davranış eşitliği" garantisi.

---

## 6. Yapılmaması Gerekenler (Anti-Liste)

1. "VContainer Killer" pazarlama dilini README/dokümanlara koymayın — teknik topluluk
   bunu ölçer, itibar riskidir.
2. Kaybetme ihtimali olan benchmark'ı önceden yayınlamayın; önce ölçün, sonra karar verin.
3. Roslyn SG'ye acele geçmeyin; editor-time codegen çalışıyor, SG ayrı bir projedir.
4. UniTask/DOTween/Addressables'ı **çekirdeğe** gömmeyin — opsiyonel köprü olmalı
   (STUDIO-EVALUATION'ın temel kuralı).
5. Paket bölmeyi v1.0 sonrasına ertelemeyin (E9 → Faz 5).
6. 0-GC kontratlarını etkileyen hiçbir refactor'u harness koşmadan merge etmeyin
   (mevcut `NexusBenchmark.csproj` bunu zaten zorluyor).
7. Tek maintainer riskini görmezden gelmeyin: doküman + CI + örnekler topluluk katkısını
   mümkün kılan altyapıdır; bunlar "feature" değil, ön koşuldur.

---

## 7. Karar Bekleyen Sorular (Kullanıcı Onayı Gerekli)

1. **Göç yaklaşımı:** (A) tam uyumluluk katmanı (`using Zenject;` Nexus üzerinde çalışsın),
   (B) birleşik native API + dönüştürücü araç, (C) ikisi birden.
   → Öneri: **B** çekirdek olarak (Faz 1-2), dönüştürücü ile (Faz 7); tam A katmanı yalnızca
   Zenject için ve v1.1+ (en büyük pazar, en büyük maliyet). VContainer/StrangeIoC için
   doküman + örnek yeterli.
2. ~~Roslyn SG vs editor-time codegen~~ (Faz 2) — **karar verildi (2026-08-13): ikisi de**.
   Editor-time codegen (reflection ile tüm derlemeleri gören) varsayılan üretim yolu; Roslyn
   SG (`SourceGenerator~/NexusBinderGenerator.cs`) ayrı proje olarak uygulandı, her derleme
   için kendi binder'ını üretir, pakete `SourceGenerators~/` olarak dağıtılır (Unity IL2CPP
   kapısı Mode B'de SG'yi doğrular). Tek dosya kuralı: iki binder aynı derlemede bulunamaz.
3. **Paket bölme zamanı** (Faz 5) — v1.0 öncesi önerilir.
4. **CI bütçesi** (Faz 0) — GitHub ücretli plan mı, self-hosted runner mı?

---

## 8. Özet Tablo

| Faz | Süre | Değer | Bağımlılık / Durum |
|---|---|---|---|
| 0 — Doğruluk & hijyen | 1 hafta | Yüksek (güven) | ✅ doküman tutarsızlıkları düzeltildi (E1, STRANGE_COMPARISON, canonical, quickstart); kalan: CI kararı, Unity test arşivi |
| 1 — API + Scope | 2-3 hafta | Yüksek (VContainer paritesi) | ✅ çekirdek + fluent zincir uygulandı (LT1–LT9, FL1–FL7); ⏳ kalan: IFactory, prefab injection |
| 2 — AOT ctor fabrikası | 3-4 hafta | Yüksek (immutability iddiası) | ✅ codegen + runtime uygulandı (CF1–CF5) + **Roslyn Source Generator** (CG1 + CG2 binder derleme/boot); ⏳ kalan: Unity IL2CPP build koşusu (`tools/unity-verify`, Unity makinesi) |
| 3 — Ekosistem köprüleri | 4-6 hafta | Orta-yüksek (adoption) | — |
| 4 — Sinyal filtresi | 2-3 hafta | Yüksek (koşulsuz 0-GC) | ✅ `ISignalFilter<T>` uygulandı (SF1–SF5); ✅ GCAudit dahil tüm harness yeşil |
| 5 — Modüler paketleme | 4-6 hafta | Yüksek (CTO kabulü) | v1.0 öncesi |
| 6 — Karşılaştırmalı benchmark | sürekli | Orta (kanıt) | Faz 1-2 sonrası anlamlı |
| 7 — Adoption köprüleri | v1.0 ile | Yüksek (Zenject göçü) | Faz 0 (doküman düzeltmesi) |

**90 günlük gerçekçi hedef:** Faz 0-1-4 tamam (≈7-8 hafta), Faz 2'ye başlanmış,
Faz 5 kararı alınmış. "Üç framework'ün yerini alma" iddiası ancak Faz 1-2-5-7 birlikte
tamamlanınca savunulabilir.

---

## 9. Düzeltilmiş Rekabet Matrisi (2026-08-13, ff0f1c9 sonrası durum)

Faz 1/2/4 uygulandıktan sonraki **güncel** karşılaştırma. §3'teki matris Faz öncesi
boşluk analizidir; bu bölüm kapatılan hücreleri ve **önceki raporlardaki hatalı
iddiaların düzeltmelerini** kanıt atıflarıyla verir. Harness kanıtı:
`tools/nexus-benchmark` — 26 yeni test (LT1–LT9, FL1–FL7, SF1–SF5, CF1–CF5) + CodeGenSuite
CG1 + CG2; tam pipeline çıkış kodu 0.

| Yetenek | Nexus Core | VContainer | Zenject | StrangeIoC |
|---|---|---|---|---|
| Constructor injection | ✅ reflection + AOT factory (`new T(...)`, CF1–CF5, CG1 + CG2 — editor codegen ve Source Generator binder'ları derlenip boot ediliyor) | ✅ IL emit / expression tree (dış kaynak) | ✅ reflection + cache (dış kaynak) | ✅ `[Construct]` ile işaretlenmiş ctor — **resmî doküman**: "Perform constructor or setter injection" + "Tag your preferred constructor". Önceki raporlardaki "❌ Yok" iddiası **hatalı** |
| `readonly`/immutable ctor state | ✅ AOT factory ctor üzerinden; field injection için destek yok (dokümante sınır) | ✅ | ✅ | ⚠️ yalnızca ctor yolu; setter/field injection readonly'e dokunamaz |
| Lifetime scopes | ✅ `Lifetime.Singleton/Scoped/Transient` + `CreateChildScope` (LT1–LT9) | ✅ | ✅ | ⚠️ context düzeyi; gerçek child-scope yaşam döngüsü yok |
| Ters sıralı dispose | ✅ `_resolvedSingletonOrder`, LT7 kanıtı | ✅ | ✅ | ❌ |
| Sinyal bus + middleware | ✅ entegre 0-GC `ISignalFilter<T>` (SF1–SF5) | ❌ dahili yok — MessagePipe öneriliyor (resmî doküman) | ✅ sınıf/struct sinyaller | ✅ tip-güvenli `Signal<T>` sınıfları — **string/Enum DEĞİL**; string olan yalnızca eski `EventDispatcher`. Önceki raporlardaki "String/Enum" iddiası **yanıltıcı** |
| Fluent binding | ✅ `BindFluent<T>().To/AsSingleton/AsScoped/AsTransient/AsSingle/AsCached/AsImplementedInterfaces/AndSelf/WithParameter` (FL1–FL7) | ✅ | ✅ | ✅ `Bind<X>().To<Y>().ToSingleton()/ToValue()/ToName()/CrossContext()` zinciri — önceki raporlardaki "❌ Yok" iddiası **hatalı** (Strange'inki opsiyon zinciri; VContainer/Zenject kadar zengin değil) |
| Editor/teşhis araçları | ✅ 16 plugin + Scene overlay + headless analyzer | ⚠️ Diagnostics Window var (bağımlılık grafiği) — "❌ Yok" iddiası **hatalı**; 16 plugin'lik suite değil | ⚠️ object-graph görselleştirmesi kaldırıldı (Zenject ReleaseNotes); debug penceresi sınırlı | ❌ |
| CI mimari kapısı | ✅ `NexusArchitectureAnalyzer.RunHeadless` — `EditorApplication.Exit(0/1)` | ❌ | ❌ | ❌ |
| Kanıt durumu | ✅ 26 yeni test (LT9+FL7+SF5+CF5) + CG1+CG2 derleme/boot + tam pipeline exit 0 | — | — | — |

**Notlar:**
- VContainer/Zenject/StrangeIoC sütunlarındaki "dış kaynak" işaretleri, bu repo dışından
teyit edilen olgulardır (resmî dokümanlar/ReleaseNotes). §6'daki dürüstlük kuralı gereği
**karşılaştırmalı süre/GC sayıları bu matriste yayınlanmamaktadır** — Faz 6'da aynı makinede
ölçülmeden hiçbir iddia sayısallaştırılmaz.
- "Nexus VContainer seviyesini aşmıştır" sonucu, matrisin yukarıdaki düzeltilmiş hücreleriyle
değerlendirilmelidir: özellikler gerçek ve testli, ama rakip framework'lerin hatalı "yok"
olarak işaretlenen özellikleri (Strange ctor injection, VContainer diagnostics, Strange fluent
zinciri) sayılmazsa sonuç daha ölçülü yazılmalıdır.
