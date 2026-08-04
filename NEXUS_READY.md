# Nexus Üretim Hazırlığı — Kapılar (Production Readiness Gates)

Bu belge "Nexus oyunlarda kullanılmak için hazır" demenin **ölçülebilir tanımıdır**.
Bir sürüm ancak aşağıdaki kapıların tamamından geçerse `v1.0` etiketini alabilir.
Her kapının durumu sürüm notunda işaretlenir; `--coverage --json` çıktısı bunun
kanıtıdır.

| # | Kapı | Ölçüt | Durum |
| --- | --- | --- | --- |
| 1 | **Harness tam kanıt** | `tools/nexus-benchmark` tam pipeline yeşil: 49 stress + 2 fuzz + 2 cross-thread + 8 dogfood + recovery + benchmark + demo-compatibility (demo binding grafiği strict validation altında boot), soak 10/10 temiz (committedΔ < 32MB, sıfır state creep) | ✅ Geçildi — 2026-08-04 yerel koşu: `dotnet run -c Release` exit 0, tüm suite'ler PASS (benchmark, recovery, stress, fuzz, cross-thread, dogfood, services, registry, concurrent-diff, demo-compat). Not: soak (`--soak`) bu oturumda tekrar koşulmadı; önceki sürüm koşularında temiz geçtiği commit geçmişinde kayıtlı |
| 2 | **Kapsam raporu** | `--coverage --json`: derlenen runtime dosyası sayısı + kapsam dışı listesi her sürümde arşivlenir | ✅ 86/86 derleniyor, 0 kapsam dışı (2026-08-04 `--coverage` ile doğrulandı) |
| 3 | **CI sürekliliği** | `.github/workflows/nexus-ci.yml` harness + coverage + JSON rapor koşar; rapor artifact olarak saklanır | ⏸️ **Pasif** — GitHub Free planı; yalnızca manuel tetikleme (`workflow_dispatch`). push/PR otomatik koşusu askıya alındı (2026-08-01) |
| 4 | **Unity editör kanıtı** | Unity 6000.5.x'te EditMode + PlayMode suite'leri yeşil — .NET 10'daki yeşillik Unity'deki yeşilliğin yerini tutmaz | 🔶 Yerelde doğru Unity sürümüyle çalıştırılıyor (2026-08-04); sonuçlar henüz arşivlenmedi |
| 5 | **Self-hosted Unity runner** | Unity 6000.5 kurulu bir makinede GitHub Actions runner `[self-hosted, unity-6000]` etiketiyle kayıtlı; workflow'daki unity-tests job'ı yeşil | ❌ Kurulmadı (CI pasifken askıda) |
| 6 | **Monetizasyon altyapısı** | Ads (AdMob/AppLovin), IAP (Unity IAP/RevenueCat) adapter factory + mock implementasyonları hazır; gerçek SDK entegrasyonu consumer tarafında | ✅ Altyapı tamam (2026-08-04: `AdAdapterFactory`, `IapAdapterFactory`, `MockAdNetworkAdapter`, `MockIapStoreAdapter`) |
| 7 | **Cihaz soak + chaos** | Gerçek cihazlarda (IL2CPP, zayıf donanım): uzun oturum soak'u, ağ kesilmesi, anlık kill, saat değişimi, ANR izleme | ❌ Cihaz gerektirir |
| 8 | **SLO belgesi** | `docs/SLO.md`: ns/op, GC, TTI, memory, crash-rate, ANR hedefleri + ölçüm altyapısı + hata bütçesi politikası | ✅ Tamamlandı (2026-08-04) |
| 9 | **Sürüm disiplini** | `package.json` semver + CHANGELOG güncel + breaking-change politikası | ✅ 0.4.0, CHANGELOG ve dokümanlar mevcut |
| 10 | **Dogfood örnek oyun** | Harness dogfood'u (GameSessionSuite) ✅; Unity örneği: tek Global context (Gameplay/UI state ayrı context değil, signal/command ile yönetilir), 4 ekran (MainMenu, GameplayHUD, GameOver, Shop), Ads/IAP mock, Economy/Progression + Save/Load | ✅ Tamamlandı (2026-08-04: `DemoBootstrap` + 1 Lifecycle (`DemoGlobalLifecycle`), 4 View/Mediator, Signals/Commands/Models/Services; tüm servis bağımlılıkları bağlı — 2026-08-04 boot düzeltmesiyle doğrulama artık Unity'de koşulmayı bekliyor) |

## v1.0 için zorunlu (GO) kapılar

> **Askı durumu (2026-08-04):** CI pasife alındığı için Kapı 3 ve Kapı 5
> (otomatik CI koşusu gerektiren kapılar) şu an sağlanamaz durumda ve v1.0
> GO kriterlerinden **geçici olarak kaldırıldı**. CI yeniden aktifleştirilince
> (paylaşımlı/self-hosted runner veya ücretli plan) bu kapılar tekrar GO listesine
> girer.

1. Kapı 4: Unity EditMode + PlayMode suite'leri 6000.5'te yeşil (0 failed).
2. Kapı 8: `docs/SLO.md` mevcut ve güncel.
3. Kapı 9: CHANGELOG'da son değişiklikler işli.
4. Kapı 10: Unity dogfood örneği derlenip çalışır durumda.

Bu kapılar geçilirse: **"Nexus çekirdeği üretim-hazır"** — gerçek oyun
projesinde kullanılabilir. Kapı 6 (gerçek SDK entegrasyonu consumer'da), Kapı 7 (cihaz soak) cihaz+SDK gerektirdiği için ilk oyunun
sertifikasyon aşamasına aittir; bunlar geçmeden "her platformda sorunsuz"
iddiası verilmez.

## "Kanıt"ın epistemik sınırı

Harness'ler "kapsanan alt küme, uygulanan kontratlar altında, bu ortamda doğru"
der — "hiçbir yerde hata yok" demez. Sınırlar: 86/86 runtime dosyası derleniyor
ancak Unity-bağımlı yollar (GUI, DOTS/Collections, inspector-serialized alanlar)
stub ortamında yalnızca *derlenip boşta* doğrulanır, davranışları Unity'de
çalışmaz, fuzz deterministik seed'lerle sınırlı, tek
makine/OS/runtime, testler "doğru" olduğuna inanılan davranışı assert eder.
Bu belgedeki kapıların amacı bu sınırların her sürümde görünür kalmasıdır.
