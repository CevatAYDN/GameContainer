# Nexus Üretim Hazırlığı — Kapılar (Production Readiness Gates)

Bu belge "Nexus oyunlarda kullanılmak için hazır" demenin **ölçülebilir tanımıdır**.
Bir sürüm ancak aşağıdaki kapıların tamamından geçerse `v1.0` etiketini alabilir.
Her kapının durumu sürüm notunda işaretlenir; `--coverage --json` çıktısı bunun
kanıtıdır.

| # | Kapı | Ölçüt | Durum |
| --- | --- | --- | --- |
| 1 | **Harness tam kanıt** | `tools/nexus-benchmark` tam pipeline yeşil: 39 stress + 2 fuzz + 2 cross-thread + 8 dogfood + recovery + benchmark, soak 10/10 temiz (committedΔ < 32MB, sıfır state creep) | ✅ Geçildi (her yerel koşuda doğrulandı) |
| 2 | **Kapsam raporu** | `--coverage --json`: derlenen runtime dosyası sayısı + kapsam dışı listesi her sürümde arşivlenir (şu an 57/80) | ✅ Uygulandı; CI arşiviyle süreklileşecek |
| 3 | **CI sürekliliği** | `.github/workflows/nexus-ci.yml` her push/PR'de harness + coverage + JSON rapor koşar; rapor artifact olarak saklanır | 🔶 Workflow hazır; ilk başarılı koşu bekleniyor |
| 4 | **Unity editör kanıtı** | Unity 6000.5.0f1'de EditMode (24 test) + PlayMode (20 test) suite'leri yeşil — .NET 10'daki yeşillik Unity'deki yeşilliğin yerini tutmaz | ❌ Yerelde 2020.3.20f1 kurulu (sürüm uyumsuz); Unity 6000.5 gerekiyor |
| 5 | **Self-hosted Unity runner** | Unity 6000.5 kurulu bir makinede GitHub Actions runner `[self-hosted, unity-6000]` etiketiyle kayıtlı; workflow'daki unity-tests job'ı yeşil | ❌ Kurulmadı |
| 6 | **Monetizasyon entegrasyonu** | Ads (AdMob/AppLovin), IAP gerçek SDK'larla cihazda doğrulandı — bu SDK'lar harness kapsamı dışında (Unity-bağımlı) | ❌ Cihaz + SDK gerektirir |
| 7 | **Cihaz soak + chaos** | Gerçek cihazlarda (IL2CPP, zayıf donanım): uzun oturum soak'u, ağ kesilmesi, anlık kill, saat değişimi, ANR izleme | ❌ Cihaz gerektirir |
| 8 | **SLO belgesi** | ns/op kapıları harness'te tanımlı (<=128B, <25us vb.); TTI/GC-spike/crash-rate hedefleri canlı telemetriyle eklenecek | 🔶 Çekirdek kapılar yazılı; canlı SLO'lar eklenecek |
| 9 | **Sürüm disiplini** | `package.json` semver + CHANGELOG güncel + breaking-change politikası | ✅ 0.4.0, CHANGELOG ve dokümanlar mevcut |
| 10 | **Dogfood örnek oyun** | Harness dogfood'u (GameSessionSuite) ✅; gerçek Unity örnek oyunu (boot→UI→ads→save→crash uçtan uca) | 🔶 Harness seviyesinde ✅; Unity örneği yok |

## v1.0 için zorunlu (GO) kapılar

1. Kapı 3: CI harness job'ı son 10 commit'te sürekli yeşil.
2. Kapı 4: Unity EditMode + PlayMode suite'leri 6000.5'te yeşil (0 failed).
3. Kapı 5: Unity testleri CI'da otomatik koşuyor.
4. Kapı 9: CHANGELOG'da son değişiklikler işli.

Bu dört kapı geçilirse: **"Nexus çekirdeği üretim-hazır"** — gerçek oyun
projesinde kullanılabilir. Kapı 6/7 cihaz+SDK gerektirdiği için ilk oyunun
sertifikasyon aşamasına aittir; bunlar geçmeden "her platformda sorunsuz"
iddiası verilmez.

## "Kanıt"ın epistemik sınırı

Harness'ler "kapsanan alt küme, uygulanan kontratlar altında, bu ortamda doğru"
der — "hiçbir yerde hata yok" demez. Sınırlar: 23 runtime dosyası harness
kapsamı dışında (Unity-bağımlı), fuzz deterministik seed'lerle sınırlı, tek
makine/OS/runtime, testler "doğru" olduğuna inanılan davranışı assert eder.
Bu belgedeki kapıların amacı bu sınırların her sürümde görünür kalmasıdır.
