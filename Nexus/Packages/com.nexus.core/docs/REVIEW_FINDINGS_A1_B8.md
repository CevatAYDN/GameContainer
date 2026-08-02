# Nexus Core — Review Findings A1–A6 + B1–B8 (Çözüm Takip Dokümanı)

**Proje:** GameContainer / `com.nexus.core`
**Tarih:** 2026-08-01
**Amaç:** `cf66e9e` commit'inde atıfta bulunulan "review findings A1–A6 + B1–B8" listesinin
**kalıcı kaydı.** Bu liste daha önce yalnızca commit mesajında referans verilmişti; gelecekteki
incelemecilerin "bu bulgular neydi ve nasıl çözüldü?" sorusunu yanıtlayabilmesi için her bulgu,
**gerçek koda karşı** (dosya:satır kanıtı + test) burada dokümante edilir.

Bulgu etiketleri, `cf66e9e` fix serisinin **storage / DI / UI / netcode** olmak üzere dört
alanda sertleştirdiği 14 bulguyu (6×A + 8×B) temsil eder. A4 ve A1 alt başlıkları (A1b,
A4a/A4b/A4c) ilgili ana bulgunun alt kalemleridir.

---

## 📋 Özet Tablo

| ID | Alan | Bulgu Özeti | Durum | Kanıt (dosya:satır) |
|---|---|---|---|---|
| A1 | Storage | Save yazımları atomik değil — delete-then-move veri kaybı penceresi | ✅ Çözüldü | `EncryptedStorageService.cs:28–29, 402–410` |
| A1b | Storage | Yazma retry backoff'u main thread'i `Sleep(10)` ile blokluyor | ✅ Çözüldü | `EncryptedStorageService.cs:30–31, 493–510` |
| A2 | Netcode | Snapshot sözleşmesi tanımsız — `snapshot[tick]` hangi durumu içerir? | ✅ Çözüldü | `NetworkSignalBus.cs:256` |
| A3 | Netcode | (Yorum işareti yok — içerik geri kazanılamadı, bkz. not) | ⚠️ Doğrulanamaz | `cf66e9e` gövde maddeleri |
| A4a | UI | WindowManager pending-open beklemesi `Task.Delay` polling kullanıyor | ✅ Çözüldü | `WindowManager.cs:60, 131–133` |
| A4b | UI | `IsWindowOpen`/`GetWindow` kilitsiz değil — semafor beklemesi | ✅ Çözüldü | `WindowManager.cs:53, 405–407` |
| A4c | UI | Pencere lifecycle callback'leri context teardown'ına duyarsız | ✅ Çözüldü | `WindowManager.cs:71` |
| A5 | Core | Concurrent batch dispatch'te sync throw kalan `ValueTask`'leri tamamlamıyor | ✅ Çözüldü | `SignalBus.cs:551` |
| A6 | Storage | Dosya adı hash'i MD5 (FIPS altında patlar), kriptografik gereksiz | ✅ Çözüldü | `EncryptedStorageService.cs:32–33, 605–644` |
| B1 | Storage | Dosya I/O'su shared lock içinde — yavaş okuma tüm key'leri stall eder | ✅ Çözüldü | `EncryptedStorageService.cs:34, 226, 458–464` |
| B2 | DI | Eşzamanlı singleton yapımı "spurious circular dependency" fırlatıyor | ✅ Çözüldü | `NexusDI.cs:778–802` |
| B3 | UI | `Dispose` in-flight async open/close döngülerine karşı guard'sız | ✅ Çözüldü | `WindowManager.cs:65, 442–445, 473` |
| B4 | Core | `ObservableList` çift abonelik kaydını dedupe etmiyor | ✅ Çözüldü | `ObservableProperty.cs:241` |
| B5 | Core | (Yorum işareti yok — içerik geri kazanılamadı, bkz. not) | ⚠️ Doğrulanamaz | `cf66e9e` gövde maddeleri |
| B6 | UI | Window-history sırası görsel top-most pencereyle senkron değil (early-return yolu) | ✅ Çözüldü | `WindowManager.cs:163` |
| B7 | Netcode | `FireAtTick` resimulation guard'sız — tekrarlanan rollback çift uygular | ✅ Çözüldü | `NetworkSignalBus.cs:180, 236–240`; `NetcodeTests.cs:94, 131` |
| B8 | Netcode | Deterministik replay sıralaması yalnızca sync handler'lar için garantili | ✅ Çözüldü (dokümante) | `SignalBus.cs:765`; `NetworkSignalBus.cs:98` |

---

## A Grubu (Storage / Netcode / UI / Core)

### A1 — Save yazımları atomik değil (HIGH)

**Bulgu:** `SaveKeyToDisk` önce silme sonra taşıma deseni kullanıyordu; silme ile taşıma
arasında çökme olursa önceki kayıt da yok oluyor (silent data loss penceresi).

**Düzeltme:** `WriteRawDataAtomically` — payload temp dosyaya yazılır, ardından tek dosya
sistemi işlemiyle yerine taşınır (dosya varsa `File.Replace`, yoksa `File.Move`). Çökme
yalnızca ya önceki iyi dosyayı ya da yeni tam dosyayı bırakabilir.

**Kanıt:** `Runtime/Services/Storage/EncryptedStorageService.cs:28–29` (yorum), `:402–410`
(`SaveKeyToDisk`), `:493–510` (`WriteRawDataAtomically`).

**Testler:** `Tests/Editor/EncryptedStorageAndAntiCheatTests.cs` — şifreleme/şifre çözme,
tamper reddi, device-binding reddi.

### A1b — Retry backoff main thread'i blokluyor (MEDIUM)

**Bulgu:** A1 düzeltmesinin retry döngüsü `Thread.Sleep(10)` ile main thread'i blokluyordu.

**Düzeltme:** `Thread.Yield()` kullanılıyor; contention'da main thread bloklanmıyor.

**Kanıt:** `EncryptedStorageService.cs:30–31` (yorum), `:501–508` (retry döngüsü).

### A2 — Netcode snapshot sözleşmesi tanımsız (MEDIUM)

**Bulgu:** `NetworkSignalHistory`'de `snapshot[tick]`'in model durumunu *tick'ten önce mi sonra
mı* içerdiği tanımsızdı; rollback yeniden oynatmaları tutarsız durum üretebilir.

**Düzeltme:** Sözleşme sabitlendi: **`snapshot[tick]` = o tick'in sinyallerinden ÖNCEKİ model
durumu.** Capture-before-replay sırası korunur — repeat rollback çift uygulama yapamaz.

**Kanıt:** `Runtime/Netcode/NetworkSignalBus.cs:256` (sözleşme yorumu).

**Testler:** `Tests/Runtime/NetcodeTests.cs` — 200-tick deterministik rollback, repeated-rollback
idempotency.

### A3 — (İşaretsiz alt bulgu — doğrulanamaz)

**Not:** A3 etiketi son koddaki hiçbir yorum satırında yer almıyor; orijinal bulgu kağıdı
commit'lenmediği için birebir içerik geri kazanılamıyor ve çözümü doğrulanamıyor.
`cf66e9e` gövdesindeki "ObservableList: dedupe duplicate handler subscriptions" ve
"capture-before-replay order preserved" maddeleri bu aralıktaki en olası karşılıklardır
(B4/A2 ile çözüldü), ancak bu bir eşleştirme tahminidir — bulgu içeriği kayıp.

### A4a — Pending-open beklemesi Task.Delay polling (MEDIUM)

**Bulgu:** `WindowManager` async pencere açılışı bittiğinde `Task.Delay` polling ile bekliyordu;
kısa bir aralıkta `IsWindowOpen` yanlış negatif dönebilir ve çift açılış tetiklenebilir.

**Düzeltme:** TCS tabanlı completion sinyali — pending set değişince lock altında sinyal
ateşlenir; bekleme `Wait(0)` veya polling yerine sinyalle uyanır (max-retry timeout'lu).

**Kanıt:** `Runtime/Services/UI/WindowManager.cs:60` (A4a yorumu), `:131–133`.

### A4b — IsWindowOpen/GetWindow kilitsiz değil (MEDIUM)

**Bulgu:** `IsWindowOpen`/`GetWindow` semafor alıyordu; arka plan async open/close kısa süre
semaforda kalınca yanlış "window closed" yanıtı → çift açılış.

**Düzeltme:** Lock-free volatile read-copy snapshot — mutasyonlar lock altında, okumalar
snapshot'tan (semafor alınmadan).

**Kanıt:** `WindowManager.cs:53` (A4b), `:405–407`.

**Testler:** `Tests/Editor/WindowManagerTests.cs:67` (A4b regression).

### A4c — Lifecycle callback'leri context teardown'ına duyarsız (LOW)

**Bulgu:** Pencere lifecycle callback'leri context dispose olduktan sonra da ateşlenebilirdi.

**Düzeltme:** Context lifetime token — callback'ler context ömrünü aşarsa bails out.

**Kanıt:** `WindowManager.cs:71` (A4c).

### A5 — Concurrent batch dispatch'te ValueTask sızıntısı (MEDIUM)

**Bulgu:** Concurrent batch dispatch'te senkron bir throw diğer başlatılmış `ValueTask`'lerin
tamamlanmasını atlıyordu → askıda kalan task'ler.

**Düzeltme:** Kaç task'ın gerçekten başladığı izlenir; sync throw'da kalan `ValueTask`'ler
güvenle tamamlanır.

**Kanıt:** `Runtime/Core/SignalBus.cs:551` (A5 yorumu).

### A6 — MD5 dosya adı hash'i (FIPS) (MEDIUM)

**Bulgu:** Key→dosya adı hash'i MD5 idi; FIPS enforcement altında `MD5.Create()` throw eder.
Ayrıca dosya adı hash'inin kriptografik güce ihtiyacı yoktur.

**Düzeltme:** FNV-1a 64-bit (non-crypto, FIPS-safe) dosya adı hash'i. Legacy MD5 adlı dosyalar
okunmaya devam edilir ve ilk kayıtta yeni isme migrate edilir.

**Kanıt:** `EncryptedStorageService.cs:32–33` (A6), `:605–644` (`GetFilePath`/`Fnv1aFileName`),
`:661–678` (`GetLegacyFilePath`).

---

## B Grubu (Storage / DI / UI / Core / Netcode)

### B1 — Dosya I/O shared lock içinde (HIGH)

**Bulgu:** Tüm cache/disk erişimi tek `_lock` altındaydı; yavaş bir ilk okuma diğer tüm key
operasyonlarını stall edebilirdi.

**Düzeltme:** Yavaş kısımlar (dosya varlık kontrolü, okuma, şifre çözme) lock DIŞINA taşındı;
yalnızca küçük cache/dirty-set güncellemeleri lock alır.

**Kanıt:** `EncryptedStorageService.cs:34` (B1), `:226–232`, `:458–464` (B1 parity).

### B2 — Eşzamanlı singleton yapımı spurious hata fırlatıyor (MEDIUM)

**Bulgu:** İki thread aynı singleton'ı ilk kez çözerken biri "circular dependency" sanıp
fırlatabiliyordu (thread-local stack diğer thread'in yapımını görüyor).

**Düzeltme:** Aynı thread'deki gerçek cycle'lar thread-local `s_resolutionStack` ile yakalanır;
`Add` başarısızsa başka bir thread yapıyor demektir — builder'ı bekler (10s deadline,
exception-safe marker temizliği).

**Kanıt:** `Runtime/Core/NexusDI.cs:778–802`.

**Testler:** `tools/nexus-benchmark/CrossThreadSuite.cs` — concurrent first-resolve.

### B3 — Dispose guard'sız in-flight async (MEDIUM)

**Bulgu:** `WindowManager.Dispose()` in-flight async open/close döngülerine karşı guard'sızdı;
dispose sonrası devam eden döngüler `ObjectDisposedException` üretebilir veya bekleyen entry
sızıntısı yapabilirdi.

**Düzeltme:** `_disposed` flag döngünün BAŞINDA set edilir — in-flight döngüler bails out;
bekleyen açılış entry'leri temizlenir.

**Kanıt:** `WindowManager.cs:65` (B3), `:442–445`, `:473`.

### B4 — ObservableList çift abonelik (LOW)

**Bulgu:** `ObservableList` aynı handler'ın çift kaydını dedupe etmiyordu → çift callback.

**Düzeltme:** Paylaşılan observer çekirdeğinde kayıt dedupe edilir.

**Kanıt:** `Runtime/Models/ObservableProperty.cs:241` (B4 fix korundu).

**Testler:** `tools/nexus-benchmark` — ObservableList mutation zero-GC + dedupe.

### B5 — (İşaretsiz alt bulgu — doğrulanamaz)

**Not:** B5 etiketi son koddaki hiçbir yorum satırında yer almıyor; orijinal bulgu kağıdı
commit'lenmediği için birebir içerik geri kazanılamıyor ve çözümü doğrulanamıyor.
`cf66e9e` gövdesindeki "WindowManager: window-history update on early-return path" maddesi
bu etikete en olası karşılıktır (B6 ile birlikte çözüldü), ancak bu bir eşleştirme
tahminidir — bulgu içeriği kayıp.

### B6 — Window-history erken-çıkış yolunda güncellenmiyor (MEDIUM)

**Bulgu:** `CloseTopWindow` erken-çıkış (early-return) yolunda history sırası görsel top-most
pencereyle senkron tutulmuyordu.

**Düzeltme:** History, visual top-most pencereyle senkron güncellenir.

**Kanıt:** `WindowManager.cs:163` (B6).

### B7 — FireAtTick resimulation guard (HIGH)

**Bulgu:** `FireAtTick` rollback/resimulate sırasında tick pointer'ı sürerken senkron ateşleme
yapabiliyordu; aynı tick'te zaten var olan sinyal tekrar uygulanabilirdi (double-apply).

**Düzeltme:** Senkron yerel ateşleme yalnızca hedef tick == current tick iken yapılır
(resimulation guard).

**Kanıt:** `NetworkSignalBus.cs:180, 236–240`; `Tests/Runtime/NetcodeTests.cs:94, 131`.

### B8 — Deterministik replay sıralaması sözleşmesi (MEDIUM)

**Bulgu:** Replay sıralama garantisinin kapsamı (sync vs async handler) dokümante edilmemişti;
async handler'larda sıralama garantisi yanlış varsayılabilirdi.

**Düzeltme:** Sözleşme dokümante edildi: deterministik sıralama yalnızca **sync-only**
sinyaller için garanti edilir; async katılımda sıralama garantisi daraltılır.

**Kanıt:** `SignalBus.cs:765` (B8), `NetworkSignalBus.cs:98`.

---

## 🏗️ Mimari Derinleştirme — 2026-08-02 (C1–C5)

A1–B8 bulguları çözüldükten sonra yapılan **mimari derinleştirme turu** (sığ modülleri
derinleştirme, arayüz = test yüzeyi) beş adayı uygulandı. Harness 214/214 PASS'ta kaldı.

| ID | Modül | Sorun | Çözüm | Kanıt |
|---|---|---|---|---|
| C1 | `AssemblyCatalog` | `GetLoadedAssemblies()` → filtre → `GetTypes()` döngüsü 11 editor dosyasında ~24 kez, **4 farklı filtre yüklemiyle** yeniden yazılmıştı; araçlar hangi kodu görebilecekleri konusunda çelişiyordu | Tek derin modül: yineleme + yüklemler (framework/third-party/test/editor) + güvenli `GetTypesSafe` (kısmi yük → uyarı + parsiyel tipler). 11 çağrı sitesi de buradan geçer | `Editor/Core/AssemblyCatalog.cs`; dönüştürülen: NexusWindow, BuildValidation ×10, NexusEditorDataProvider, NexusCodeGenerator ×2, Dashboard/Explorer/GameManager/Graph/TypeAnalyzer/Wizard |
| C2 | `NexusFieldInspector` | `ExplorerPlugin.CreateSignalFieldUI` ve `HierarchyPlugin.CreateFieldUI` neredeyse aynı tip→UI Toolkit switch ağaçlarıydı ve çoktan ayrışmıştı (read-only, undo, fallback) | Tek modül: `CreateField` + `EnumerateMembers`. Host'lar kendi undo/fallback'lerini callback ile korur | `Editor/Core/NexusFieldInspector.cs` |
| C3 | `RecoveryEngine` | ~70 satırlık karar ağacı `HandleErrorWithDecision` / `…Async` ikizlerinde kopyalanmıştı | Tek `BuildPlan` + iki ince giriş noktası | `Runtime/Core/RecoveryEngine.cs`; `RecoveryRegression` suite'i PASS |
| C4 | Test harness | `NexusTestContext` paralel bir `Bind` yüzeyi ve `[SignalHandler]` yeniden ayrıştırması tutuyordu — üretimle sürüklenme riski | Üretim `IContextBuilder`'ı expose edildi; kayıt `SignalBus.RegisterCommandType` ortak yolu üzerinden (Context taramasıyla aynı) | `Runtime/Testing/NexusTestContext.cs`, `Runtime/Core/SignalBus.cs`, `Runtime/Core/Context.cs` |
| C5 | `DashboardSections` | Tek adaptörlü dikiş: modül yalnızca Dashboard arkasında; Dashboard'da ayrıca sığ passthrough vardı | Modül korundu (silme testini geçiyor), passthrough kaldırıldı, çağrılar doğrudan modüle | `Editor/Plugins/Dashboard/DashboardSections.cs`, `Editor/Plugins/DashboardPlugin.cs` |

**İnceleme sonrası düzeltmeler:** inceleyici bulgularına göre `GetTypesSafe` hiç throw etmediği
için kalan per-site try/catch blokları (BuildValidation ×10, NexusWindow, ExplorerPlugin,
DashboardPlugin, GameManagerPlugin, NexusCodeGenerator.GenerateBinder) ve yalnızca o catch'lere
hizmet eden `name` değişkenleri kaldırıldı; `NexusTestContext`'teki gereksiz ön-bind'ler silindi
(kayıt defteri komut tipini kendisi bind ediyor); NexusCodeGenerator'daki yorum-satırı birleşmesi
(foreach'i yorumlayan kritik hata) düzeltildi.

---

## 🛡️ Güvenlik & Sağlamlık Denetimi — 2026-08-02 (A8)

**Kapsam:** `com.nexus.core` runtime'ın 12 maddelik Unity/MVCS denetim skill'i ile taranması.
**Kanıt:** `tools/nexus-benchmark` — **207/207 PASS, 0 FAIL, 0 uyarı** (Release).

| Bulgu | Kök Neden | Çözüm | Kanıt |
|---|---|---|---|
| Reentrancy guard Release'de sessizce dönüyor | `#if UNITY_EDITOR || DEVELOPMENT_BUILD` yalnızca editor/dev'de fırlatıyor, Release'de log+return ile durum bozulmasını gizliyordu | Guard artık **tüm build'lerde** `NexusReentrancyException` fırlatıyor (sync + async her iki yol) | `SignalBus.cs` (sync ~:351, async ~:510) |
| GameSaveManager atomik olmayan yazma | delete-then-move çökme penceresinde tek iyi save'i de siliyordu | `File.Replace` / overwrite-rename (EncryptedStorageService ile aynı desen) | `GameSaveManager.cs:110–121` |
| Context.Dispose sync-over-async | Senkron teardown `DisposeAsync().AsTask().GetAwaiter().GetResult()` ile main thread'i blokluyordu | `Context` artık `IAsyncDisposable` (`DisposeAsync()`); senkron yol thread pool'a non-blocking erteletiyor | `Context.cs`, `NexusDI.cs:1200–1212` |
| OfflineTimeCalculator yalnızca wall-clock | Saat ileri alınınca tavan (28800s) kadar haksız offline ödül | `Environment.TickCount64` monotonic tick kaydı + gerçek elaps'a clamp (reboot'ta wall-clock fallback) | `OfflineTimeCalculator.cs` |
| CS0619 susturmaları (ObjectPoolService) | Unity 6.5+'ta obsolete `GetInstanceID()` reflection hack + 3 pragma disable | Modern `Object.GetEntityId()` API'si; tüm pragmalar kaldırıldı | `ObjectPoolService.cs:232–240` |
| Captive dependency doğrulaması yok | Singleton'ın transient bağımlılık yakalaması sessizce yaşam süresi sızıntısı yaratıyordu | `Validate()` artık `CaptiveDependency` raporluyor (polimorfik binding'lerde dedupe) | `ContextBuilder.cs:410–460`, `DiValidationIssue.cs` |
| CS0649 uyarısı (NexusBinding) | Inspector-atanan `_customTargets` derleyicide uyarı üretiyordu | Varsayılan `Array.Empty` başlatıcı | `NexusBinding.cs:42` |
| walkthrough.md yok | Skill'in zorunlu doküman listesinde eksikti | `walkthrough.md` oluşturuldu (kurulum→sertleştirme turu) | `walkthrough.md` |

**Not:** Harness'teki GS5 offline testi, gerçek 2 saatlik yokluğun hem wall-clock hem monotonic tick'i
ilerletmesi gerektiği için iki saati de tutarlı simüle edecek şekilde güncellendi.

---

## 🛡️ Mimari Kusursuzluk Turu — 2026-08-02 (A9)

**Kapsam:** `com.nexus.core` runtime + `tools/nexus-benchmark` harness'ında kalan mimari açıkların
taranması: async fire-and-forget güvenliği, `#if` build-varyant davranış farkları, static mutable
state thread-safety, per-frame allocation'lar ve event leak'leri.
**Kanıt:** `tools/nexus-benchmark` — **209/209 PASS, 0 FAIL, 0 uyarı** (Release).

| Bulgu | Kök Neden | Çözüm | Kanıt |
|---|---|---|---|
| NetworkMonitor latency verisi data race | `s_latencyHistory` (plain `Dictionary`) network/arka plan thread'lerinden lock'suz yazılıyor, game/editor thread'i okuyordu — `PerformanceMonitor`'un BUG-17'de düzelttiği sınıfın aynısı burada eksikti; ayrıca paylaşılan `ConnectionStatus` nesnesi yarışıyordu | Adanmış `s_historyLock` tüm okuma/yazma/clear erişimini koruyor; `CurrentStatus` + `OnConnectionStatusChanged` payload'ı tek `SnapshotStatus()` helper'ı ile savunmacı kopya veriyor | `NetworkMonitor.cs` |
| Async overflow Release'de sessizce komut düşürüyor | `#if UNITY_EDITOR || DEVELOPMENT_BUILD` / `#else` ayrımı: Release'de log+drop, editor/dev'de fırlatma — A8 reentrancy fix'inin kapatıp yasakladığı sınıfın birebir aynısı | Overflow artık **tüm build'lerde** `NexusAsyncOverflowException` fırlatıyor; guard tek `EnterAsyncInFlight()` helper'ına alındı ve kompozit async yollarına da uygulandı (önceden 100-komut limitini tamamen bypass ediyorlardı) | `CommandExecutor.cs:43–56, 234, 334, 523, 550` |

**Yeni harness regresyon testleri:**
- `26b. NetworkMonitor_Concurrent_Access` — 8 yazıcı thread + 200 eşzamanlı okuma; exception yok, her peer için max latency == 99, status snapshot doğru.
- `34b. Async_Overflow_Throws_AllBuilds` — gate'te bloklanan 101 eşzamanlı async komut; en az 1 `NexusAsyncOverflowException` fault'u, 0 beklenmedik fault.

**Taranıp temiz çıkan alanlar:** fire-and-forget task'ler (`EconomyService`, `UIManager`, `WindowManager`, `SafeAsyncRunner`) dahili try/catch'li ve exception-safe; `NexusBinding` static event'i `OnDestroy`'de unsubscribe ediyor; kalan `#if UNITY_EDITOR || DEVELOPMENT_BUILD` bölgeleri yalnızca debug diyagnostikleri (davranış aynı); `Context.s_assemblyScanCache`, `CausalTracing.s_sinks`, `NexusRuntime` registry tamamen lock korumalı ve `Reset()` tüm statik cache'leri temizliyor.

---

## 📌 Sonuç

`cf66e9e` (ve çevresindeki sertleştirme commit'leri) storage/DI/UI/netcode alanlarındaki
bulguları çözmüştür; her çözüm yukarıda dosya:satır kanıtıyla eşleştirilmiştir. A3 ve B5
etiketleri son kodda açık yorum işaretine sahip değildir (orijinal bulgu kağıdı
commit'lenmemişti); bu doküman o boşluğu kapatır ve en olası karşılıkları dürüstçe işaretler.

**Harness doğrulaması:** `tools/nexus-benchmark` — tüm suite'ler PASS (214/214), zero-GC
korumalı.

---

## 🔗 İlgili Dokümanlar

- [CONTRIBUTING.md](CONTRIBUTING.md) — PR kontrol listesi ve standartlar
- [REVIEW_VALIDATION.md](REVIEW_VALIDATION.md) — önceki (31 Temmuz) rapor maddeleri
- [SERVICE_AUDIT.md](SERVICE_AUDIT.md) — servis katmanı denetimi
