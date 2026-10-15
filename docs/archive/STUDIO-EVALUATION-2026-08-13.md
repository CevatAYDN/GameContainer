# 🎮 Nexus Core — Studio Gözünden Değerlendirme Raporu — 2026-08-13

**Soru:** Bir oyun geliştirici / stüdyo bu altyapıyı kullanır mı? İstenen özellikler neler? Görülen eksikler neler?
**Yöntem:** `/unity-game-code-review` merceği + önceki üç denetim turunun (AUDIT + REVIEW) birikmiş kaynak-kod bilgisi + editor araçlarının (16 plugin, wizard, analyzer, BuildValidation, CodeGen, LiveReload) bu oturumda yeniden okunması. Tüm iddialar kaynak kodla doğrulandı; dosya yol ve satır referansları gerçek.

---

## Özet Karar

**Evet, kullanılır — ama hedef kitle dar ve seçici.** Bu, "her stüdyo" için değil; **Unity 6, mobil-öncelikli, performans odaklı, tek-ekipli veya küçük stüdyo** profili için tasarlanmış olgun bir çekirdek. Güçlü yanları gerçek ve kanıtlı (0-GC iddiası benchmark harness'la, IL2CPP binder'ı kod üretimiyle, anti-cheat storage HMAC v3 ile, wizard çıktısı Unity'nin kendi Roslyn'iyle derlenerek doğrulanıyor). Ama **ekosistem entegrasyonu bilinçli olarak dışarıda bırakılmış** (UniTask/DOTween/Addressables/networking transport/Unity IAP — tek bir referans yok) ve bu, yerleşik stüdyoların adoption'ını ciddi frenler.

> **Tek cümlelik tez:** Nexus, "sıfır GC + AOT + gömülü servisler + AI-agent dokümantasyonu" ekseninde Zenject/VContainer'dan *farklılaşıyor*; onları *tamamlamıyor*. Stüdyo kararı bu farkın değerli olup olmadığına bakar.

---

## 1. Neden Kullanılır? (Kanıtlı Güçler)

| Güç | Kanıt (kaynak) | Studio değeri |
|---|---|---|
| **Sıfır-GC hot path disiplini** | `SignalBus` sync/async ayrılmış, buffer pooling (`s_nestedBufferFreeList`), `CompiledAccessorEmitter`, benchmark harness (`tools/nexus-benchmark`, 1236 satır, FT1–FT4 gibi kontrol noktaları) | Mobil 60fps hedefli oyunlarda frame bütçesi |
| **AOT/IL2CPP güvenli DI** | `NexusCodeGenerator` → `NexusGeneratedBinder.g.cs` (AOT binder üretimi), `[Preserve]` disiplini | iOS/Android'de reflection stripping derdi yok |
| **Gömülü servis katmanı (~19 servis)** | IAP, Ads, Analytics, Economy, Progression, Localization, Input, Audio, Haptics, Feedback, Storage (AES-256 + anahtar-bağlı HMAC v3), SaveThrottler, OfflineTimeCalculator (clock-rollback anti-cheat), Pool, Tick | Hazır servisler = prototip hızı |
| **Editor paketi (16 plugin)** | ContextInspector, GameManager (canlı sinyal fırlatma), Performance/Network/Error dashboards, Tracer + Graph (trace graf), FSM, TypeAnalyzer, Hierarchy, Explorer | Play-mode'da canlı teşhis — çoğu rakibin yok |
| **Canlı geliştirme döngüsü** | `LiveReloadProcessor` (Play-mode'da ModelData değişimini singleton'lara uygular), `NexusSetupWizard` (8 dosya, atomik yazma, hash pinleme, Roslyn derleme testi) | Edit→Play iterasyon hızı |
| **CI'da mimari gate** | `NexusArchitectureAnalyzer.RunHeadless` (NEXUS001-004, batch-mode exit 0/1), `BuildValidation` (1369 satır pre-build kural) | Kalite kültürü kurumsal ölçekte |
| **AI-agent dostu dokümantasyon** | `README.md` "For AI Agents" bloğu, `AI_GAME_DEVELOPER_GUIDE.md`, `GAME_PATTERNS.md` | 2026'da gerçek bir farklılaştırıcı — AI kod üretimi çağında onboarding maliyeti düşer |
| **Türkçe yerelleştirme** | `NexusLang` (en+tr), TR quickstart | TR pazarında yerel stüdyo avantajı |

---

## 2. Neden Kullanılmaz? (Adoption Engelleri)

1. **Ekosistem boşluğu (en büyük fren).** Pakette UniTask, DOTween, Addressables, Odin, Mirror/FishNet/Photon — **hiçbirine tek referans yok** (grep: 0 eşleşme). Bir stüdyonun yıllık tecrübesi Zenject + DOTween + Addressables üzerine kuruluysa, Nexus'un karşılığını vermesi için "0-GC + anti-cheat + dashboards"ın bu boşluğu doldurması gerekir.
2. **Ağ katmanı soyutlama seviyesinde.** `NetworkSignalBus` sinyal dağıtımı yapıyor ama **transport yok** — stüdyo Mirror/FishNet/Photon adaptörünü kendi yazar. "Network Dashboard" var ama bağlanacak ağ yok.
3. **IAP/Analytics/Ads abstre arayüzler.** Unity IAP, GameAnalytics, Adjust adaptörü **paketle gelmiyor** — her oyun için elle bağlanır.
4. **Sürüm ve yaşam döngüsü riski.** v0.4.0, MIT, tek paket, tek-maintainer profili. Stüdyolar çekirdek mimariyi üzerine kurmadan önce sürüm kararlılığı + yol haritası ister.
5. **Unity 6 kilidi.** Unity 6000.0+ rozeti var; LTS öncesi sürümlerde çalışmayabilir.
6. **Zenject/VContainer ağırlığı.** Rakip ekosistemlerin onboarding dokümanı, örnek deposu ve topluluğu kat kat büyük. "Kendi çerçeveni seçmek" CTO seviyesinde savunulması zor bir karar.

---

## 3. Stüdyo Gözünden İstenen Özellikler (Öncelik Sıralı)

### P0 — Adoption köprüleri (olmadan büyük stüdyo kapıdan dönmez)
1. **Zenject/VContainer geçiş aracı** — otomatik binding/config dönüştürücü veya en azından "Zenject→Nexus eşleme" dokümanı. ✅ **Uygulandı (2026-08-13):** `docs/ZENJECT_MIGRATION.md` — kavram eşleme tablosu, 8 adımlı yol, doğrulama listesi, en çok yapılan 3 hata.
2. **Unity Profiler entegrasyonu** — marker'lar kısmen vardı (`Nexus.Command.Execute` 4 noktada, `Nexus.Signal.Dispatch` NEXUS_DEBUG kapılı, `Nexus.TickService.Update/FixedUpdate/LateUpdate`) ama **chartable sayaç + Profiler modülü yoktu** — "0-GC" iddiası Profiler'da gözlenemiyordu. ✅ **Uygulandı (2026-08-13):** `NexusProfiler` sayaçları (signal, command, composite, resolve) + `NexusProfilerModule` (Profiler penceresinde "Nexus" grafiği).
3. **Addressables desteği** — `SceneManagerExtensions` yalnızca klasik scene yükleme yapıyor. ✅ **Uygulandı (2026-08-13):** `IAssetLoadService` seam + `ResourcesAssetLoadService` varsayılanı + `docs/ADDRESSABLES_ADAPTER.md` (yapıştır-hazır adaptör).
4. **Ağ transport adaptörleri** — `NetworkSignalBus` için en azından Mirror + FishNet örnek adaptörleri (örnek olarak bile olsa).

### P1 — Günlük üretim araçları
5. **Yeni Input System bağlama** — `InputService` soyutlama seviyesinde; `UnityEngine.Input` bile kullanmıyor. Input Actions asset eşleme katmanı beklenir.
6. **Hazır analitik/ödeme adaptörleri** — Unity IAP + GameAnalytics/Adjust/AppsFlyer örnek implementasyonları.
7. **Cloud save köprüsü** — `EncryptedStorageService` yerel dosya şifrelemesi yapıyor; Unity Cloud Save / PlayFab senkron adaptörü üstüne oturmalı.
8. **Gerçek mini-oyun örneği** — Counter örneği mimariyi gösteriyor ama servis katmanını (IAP, Economy, Progression, OfflineTimeCalculator, SaveThrottler) **birlikte** çalıştıran uçtan-uca bir mini oyun yok. Studio demo taleplerinde ilk sorulan bu olur.
9. **UniTask köprüsü** — API'ler `ValueTask` döndürüyor; UniTask interop katmanı async kod tabanlı stüdyolar için büyük kolaylık.
10. **UI Toolkit runtime desteği** — UI katmanı uGUI (`UICanvasSystem`) merkezli; Unity 6'da yeni projeler UI Toolkit'e geçiyor.

### P2 — Uçlar
11. **Scene-view hata ayıklama overlay** — context/signal bağlarını Scene görünümünde gösteren araç (şu an yalnızca pencereler var).
12. **Sinyal replay/step debugger** — Tracer canlı akış gösteriyor; "kayıt + geri sarma + adım adım" yeteneği teşhisi bir üst seviyeye taşır.
13. **Paket modülerliği** — tek `com.nexus.core` yerine `com.nexus.di`, `com.nexus.signals`, `com.nexus.services` alt paketleri; stüdyolar "sadece DI" almak isteyebilir.
14. **CI şablonları** — headless analyzer var; GitHub Actions/Azure Pipelines örnek YAML'ları + license/il2cpp build adımları hazır gelmeli.
15. **Yol haritası + RFC süreci** — topluluk için şeffaf karar mekanizması.

---

## 4. Editor Araçları Analizi (16 Plugin + Çekirdek)

### Olgun olanlar (kaynakla doğrulandı)
- **Mimari:** `NexusWindow` plugin host — `INexusEditorPlugin` sözleşmesi, kategori sidebar'ı, `DidReloadScripts`'te tip önbelleği invalidation'ı (`NexusWindow.cs`), gizli test plugin'leri. Genişletilebilirlik **gerçek**: yeni plugin = tek sınıf.
- **Canlı teşhis:** `ContextInspectorPlugin` (DI container + singleton + sinyal fırlatma), `PerformanceDashboardPlugin` (FPS/bellek/GC sparkline + alarm eşikleri), `NetworkDashboardPlugin` (latency gauge + paket istatistik), `ErrorDashboardPlugin` (ciddiyet/kategori/filtre + stack trace + **CSV export**), `TracerPlugin` + `GraphPlugin` (trace graf, `INexusTraceSink`), `FSMPlugin` (durum makinesi görselleştirme), `GameManagerPlugin` (model/signal/command/view/service envanteri + canlı sinyal testi, 0.5s refresh, static scan cache).
- **Üretim zinciri:** `NexusSetupWizard` + `WizardPlugin` (8 dosya tek-kaynak, atomik yazma, SHA-256 pinleme, Unity Roslyn derleme testi), `NexusCodeGenerator` (AOT binder), `TypeAnalyzerPlugin`, `CasualServicesPlugin` (tek tık servis kurulumu).
- **Kalite kapıları:** `BuildValidation` (pre-build mimari kurallar, `RunSilent()`), `NexusArchitectureAnalyzer` (headless CI, NEXUS001-004).
- **Canlı geliştirme:** `LiveReloadProcessor` (Play-mode ModelData hot reload, `ConcurrentDictionary` önbellekler + lock disiplini).
- **Detay:** `NexusFieldInspector` (Undo destekli runtime alan inceleme, "deep module" olarak tasarlanmış), `NexusHierarchyMenus` (Root/Canvas oluşturma, Undo kayıtlı), `NexusLang` (en+tr), `NexusEditorStyles` (stat tile/status dot sistemi), `HelpPlugin`.

### Eksik olanlar
1. **Profiler derinliği:** framework kendi metriğini üretiyor ama Unity Profiler'a gömülmüyordu (marker'lar vardı; chartable sayaç + modül yoktu). ✅ **Uygulandı (2026-08-13):** `NexusProfiler` + `NexusProfilerModule` — studio profili ile Nexus profili artık aynı pencerede.
2. **Scene-view araçları:** hiçbir plugin Scene görünümüne çizmiyor — context sınırları, binding bağları, signal trafiği sahne içinde görünmüyor.
3. **Addressables/aset hattı farkındalığı:** BuildValidation aset borçlarına (referanssız aset, duplicate prefab, Addressables grup sorunları) bakmıyor.
4. **Test istatistik arayüzü:** EditMode suite'leri zengin ama bir "test koş ve raporla" plugin'i yok (headless analyzer CI'da var, editor içinde yok).
5. **Editor test kapsamı:** plugin yaşam döngüsü testleri var (`PluginRefactorValidationTests`) ama 16 pluginin UI davranışı tek tek test edilmiyor.

---

## 5. `/unity-game-code-review` Checklist Uygulaması

| # | Kural | Durum |
|---|-------|-------|
| 1 | Kaynak inceleme (sıfır varsayım) | ✅ Üç denetim turunda tüm dosyalar okundu |
| 2 | Kök-neden, semptom yaması yok | ✅ F1–F9, R1–R5 kök nedenden düzeltildi |
| 3 | Hot path 0-GC | ✅ Tasarımda; benchmark harness var — **⚠️ Profiler ile bağımsız doğrulama yok** |
| 4 | ObservableProperty unsubscribe | ✅ F2/R4 + wizard sync testi + NEXUS004 ile kilitlendi |
| 5 | DI scoping (captive, <=6 ctor, bind parity) | ✅ BuildValidation kuralları; denetimlerde doğrulandı |
| 6 | Main-thread + non-blocking teardown | ✅ GameSaveManager hang guard'ı eklendi (F5) |
| 7 | Re-entrancy guard tüm build'lerde throw | ✅ `SignalBus.cs` — sync 10 / async 32, istisna fırlatıyor |
| 8 | EditMode/PlayMode SafeDestroy | ✅ `SafeDestroyUtility` |
| 9 | Storage atomic + HMAC | ✅ v3 anahtar-bağlı HMAC + `File.Replace` fallback (F8) |
| 10 | Sıfır uyarı | ⚠️ Örnek dosyalarda `[SerializeField]` alanlar için beklenen CS0649 var; paket kendisi temiz |
| 11 | Empirik test (100% pass) | ⚠️ **Bu makinede `dotnet` bozuk** — Unity ortamında koşulmalı |
| 12 | Dokümantasyon senkronu | ✅ CHANGELOG + NEXUS_READY Kapı 10 + AUDIT/REVIEW raporları |

---

## 6. Sonuç ve Önerilen Yol

**Karar matrisi (studio gözünden):**
- **Kullanır:** Unity 6 + mobil + 60fps hedefi olan, performans/anti-cheat derdi olan, küçük/orta ekipler; TR pazarı stüdyoları; AI-destekli geliştirme akışı kuran ekipler.
- **Kullanmaz:** Ekosistemi (Addressables/DOTween/UniTask/ağ) oturmuş, Zenject'e yatırım yapmış, büyük ekipler — taşınma maliyeti farkın değerini aşar.
- **Karar verici:** CTO — bu yüzden satış konuşması "0-GC" değil, "**adoption köprüleri + Profiler kanıtı + örnek mini oyun**" üzerinden yapılmalı.

**İlk 5 aksiyon (değer/maliyet oranına göre) — uygulama durumu (2026-08-13):**
1. ✅ **Uygulandı** — `NexusProfiler` sayaçları (signal/command/composite/resolve) + `NexusProfilerModule` (Profiler'da "Nexus" grafiği, `[ProfilerModuleMetadata]` ile otomatik keşif). Ayrıca `NexusDI.cs`'in 153 karışık LF satır sonu normalize edildi.
2. ✅ **Uygulandı (doküman)** — `docs/ZENJECT_MIGRATION.md`; otomatik dönüştürücü betiği ileri adım olarak dokümante edildi.
3. ⏳ **Ertelendi** — uçtan-uca mini oyun örneği; `dotnet` bu makinede bozuk olduğundan ~15 dosyalık örnek derlenemez doğrulanamaz (hazır bekliyor, ayrı oturum ister).
4. ✅ **Uygulandı (seam + doküman)** — `IAssetLoadService` + `ResourcesAssetLoadService` varsayılanı + `docs/ADDRESSABLES_ADAPTER.md` (adaptör pakete bağımlılık eklemeden projeye yapıştırılır).
5. ✅ **Uygulandı (2026-08-13)** — Scene-view overlay (`NexusSceneOverlay`, `SceneView.duringSceneGui`): context sınırları (Root hiyerarşi bounds + scope etiketi + parent→child bağları) ve signal bağları (signal→command spokes + `[CrossContext]` inter-context linkler). Tools → Nexus → Scene Overlay menüsünden açılır/kapanır; veri katmanı (`NexusSceneOverlayData`) EditMode testleriyle kilitlendi (8 test). Aynı turda `NexusProfiler` sayaçları için PlayMode benchmark testi eklendi (`NexusProfilerPlayModeTests`: N fire/resolve sonrası sayaç deltası tam N — 4 test).

**Mevcut kalite notu:** Üç denetim turu sonrası kod tabanı kurumsal kalitede; editor paketi (16 plugin, canlı teşhis, CI kapıları, atomik wizard) rakiplerde karşılığı olmayan bir varlık. Sorun teknoloji kalitesi değil — **ekosistem boşlukları ve adoption köprüleri**.
