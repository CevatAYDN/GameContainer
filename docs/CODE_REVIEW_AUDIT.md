# Code review audit — doğrulanan bulgular

İncelenen HEAD: `a5d613c3c76fb8feb44e0e38812e100a260be60b`.

Bu rapor tam depo auditi tamamlandı iddiası değildir. İncelenmeyen kod için hata yok sonucu çıkarılmaz. Çalışma ağacında önceden mevcut staged/unstaged değişiklikler vardır; HEAD tek başına çalışma ağacını tanımlamaz. Ürün kodu değiştirilmedi.

## Çalıştırılan kontroller

- `dotnet run --project tools/nexus-benchmark`: tamamlandı; çıktı 646 PASS, 0 FAIL satırı içeriyor ve ALL BENCHMARKS PASSED ile bitiyor. Log: `Nexus/artifacts/audit-current-host.log` (artifact, git-ignored).
- `python tools/check-docs.py`: başarılı.
- `python tools/check-versions.py`: exit 0; 28 dependency row, 0 problem. Offline kontrol; en yeni SDK sürümleri doğrulanmadı.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/nexus-code-audit.ps1`: tamamlandı. Bu araç yalnızca üç satır-regex kontrolü yapar; tam audit değildir.
- `mete run --continue`: CLI PATH üzerinde bulunamadığı için önceki handoff alınamadı.
- Unity Editor/PlayMode, gerçek SDK entegrasyonu, player/IL2CPP ve cihaz testleri bu incelemede çalıştırılmadı.

## 1. Statik audit yorumları metot diye sayıyor — düşük önem, doğrulanmış araç hatası

**Kanıt:** `tools/nexus-code-audit.ps1:26` her satırda `async\s+void` arıyor; yorum veya string ayrımı yapmıyor. `Nexus/Packages/com.nexus.core/Editor/Analyzer/CodeHealthRules.cs:118` satırındaki `// Rule NEXUS002: Async Void` yorumunu da eşleştiriyor. Aynı dosyadaki hata mesajı/recommendation stringleri de taramaya giriyor.

**Etki:** `Async Void Methods` toplamı metot sayısı değildir. Çıktıdaki WARN satırları bağımsız olarak doğrulanmadan hata kanıtı sayılamaz.

**Çözüm:** Roslyn syntax tree üzerinden MethodDeclaration/LocalFunction/Lambda düğümlerini inceleyin. Yorum, string, gerçek async void ve izin verilen event handler fixture'larını test edin. Regex korunacaksa çıktıyı “text matches” olarak adlandırın.

## 2. Network history replay yeniden girişe karşı korunmuyor — orta önem, kod yolundan türetilen kusur

**Kanıt:** `Nexus/Packages/com.nexus.core/Runtime/Netcode/NetworkSignalBus.cs:181–221`: `ReplaySignals` `_replayLock` altında ortak `_replayBuffer` dizisine history kopyalıyor (191), ardından kullanıcı handler'ını çağırıyor (206) ve aynı diziyi okumaya devam ediyor. C# monitor lock aynı thread için yeniden girişlidir; bu lock recursive çağrıyı engellemez.

**Tetikleme:** Bir signal handler history'ye yeni kayıt ekler ve aynı history üzerinde `ReplaySignals` çağırırsa iç çağrı dış çağrının kullandığı buffer'ı tekrar doldurur. Örneğin dış replay tick 1 için çalışırken handler history'yi temizleyip tick 2 kayıtlarını ekler ve tick 2 replay yaparsa dış loop kalan tick 1 kayıtlarını artık aynı snapshot'tan okuyamaz.

**Etki:** Metodun dış çağrıda yakaladığı history snapshot'ı handler boyunca sabit değildir; kalan sinyaller atlanabilir. Bunun üretimde yaşandığı iddia edilmiyor. Bu incelemede ayrı executable regression testi yazılmadı; mevcut host testlerinin geçmesi bu yeniden giriş yolunu doğrulamaz.

**Çözüm:** Recursive replay'i açıkça reddeden reentrancy guard ekleyin veya her çağrı derinliği için bağımsız/pool edilmiş buffer kullanın. Handler içinde Clear/Add/nested ReplaySignals yapan regression testi ekleyin; dış çağrının snapshot sözleşmesini açıkça tanımlayın.

## Kapsam eksikleri / sonuç sınırı

- Host testleri Unity motoru ve SDK davranışının yerine geçmez. Core runtime'ın harness dışında kalan dosyaları için ayrıca compile-check ve Unity testleri gerekir.
- Paketler listelendi; ancak tüm editor, ads providers, Firebase, IAP, DOTS, FSM, generator, samples ve araçlar satır satır incelenmedi. Bu alanlar için “sorunsuz” sonucu verilmemiştir.
- CI, release gate ve önceki artifact belgeleri bu HEAD/çalışma ağacı için yeniden çalıştırılmış kanıt olarak sayılmadı.
- `ResourcesAssetLoadService` ve CodeFirst sample başlangıç/iptal akışı okundu; bu sınırlı okumada yeni doğrulanmış bulgu çıkarılmadı. Bu bir güvenlik veya doğruluk garantisi değildir.

Tam audit kapanışı için her paket ve assembly'nin kapsam matrisi, gerçek Unity test sonuçları, optional SDK compile matrisi, IL2CPP/device sonuçları ve yukarıdaki yeniden giriş regression testi gereklidir.
