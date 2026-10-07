# Nexus — amaçlı performans geliştirmesi, 7 Ekim 2026

CI kapsam dışıdır. Bu çalışma GitHub Actions kullanmadı ve yeni workflow eklemedi.
Amaç: mevcut karşılaştırmada geride kalan DI başlangıç maliyetini azaltmak ve
Unity'deki rekabet durumunu gerçek player ile ölçmek.

## Uygulanan değişiklik ve amacı

Named binding, cross-boundary ve lazy service tabloları her NexusDI kurulurken
oluşturuluyordu. Artık özellik ilk kaydedildiğinde oluşturuluyor. Kullanılmayan
özelliklerin maliyeti her scope'a yüklenmiyor; kullanılan özellikler kendi tablolarını
oluşturuyor. Default binding, lifetime, validation, scope sahipliği ve dictionary
concurrency politikası korundu.

Tek private yardımcı metot, ilk oluşturmayı disposal lock'u altında yapıp volatile
olarak yayınlıyor. Yeni public ayar/arayüz, alternatif container veya kullanıcı
migrasyonu eklenmedi. Mevcut Injector ve CompiledAccessorEmitter sorumlulukları
korundu. İşlem tekrarı tek noktaya alındı; performans uğruna validation kaldırılmadı.

Altı regresyon: sonradan named kayıt/parent sahipliği, eşzamanlı ilk named kayıtlar,
sonradan cross-boundary kayıt, yeniden validation, Type resolve ile lazy bildirim,
eşzamanlı lazy bildirimlerde referansa göre tekilleştirme.

## Yerel doğrulama

- EditMode: **358 geçti, 0 hata, 0 atlama**.
- PlayMode: **160 geçti, 0 hata, 2 koşullu debug testi atlandı**.
- .NET host harness: **291 PASS**, exit 0.
- Güncel Unity runtime/editor derlemesi ve Mono player build: **0 C# diagnostic**.
- Gerçek Windows x64 Unity **6000.5.6f1 non-development Mono player** çalıştı.
- Bağımsız kaynak incelemesi optional-map yayınlama/disposal ve benchmark koşullarında
  yeni bir engel bulmadı; testlerin yerine geçirilmedi.

Önceki turdaki 107 tarball consumer testi ve Android IL2CPP build, kendi kaynak
sürümünün kanıtıdır; bu değişiklik için yeniden yapılmış sayılmaz. Güncel player,
çalışma alanındaki paketi UPM file dependency üzerinden derledi.

## Ölçümler

VContainer 1.19.0: `5401e5a7ebc4980a2b82141ffc26391a6547edd7`;
Zenject 9.2.0: `c2e33500a84f9408a809deca2af2c55494ab2482`.
Gerçek çekirdekler; aynı readonly üç nesneli grafik, 4.000 warmup, 7 tur, dönen aday
sırası, doğru değer ve lifetime kontrolü. Tam Context startup ve Unity köprüleri
bu mikrobenchmark'ın kapsamında değildir.

| Unity Mono iş yükü | Nexus ns/op | VContainer ns/op | Zenject ns/op |
|---|---:|---:|---:|
| Cached resolve | 17.66 | 48.11 | 599.71 |
| Transient üç nesne | 1132.08 | 1681.55 | 5896.43 |
| Register/build/first resolve + mevcut disposal, warm metadata | 12947.30 | 17565.60 | 24853.40 |

Nexus bu üç Mono senaryosunda önde. Önceki commit `8d8f79a` git archive ile ayrı bir
pakete çıkarılarak aynı runner ile ölçüldü: Nexus başlangıcı **23283.90 → 12947.30
ns/op**, bu koşularda **%44,39 düşüş**. Diğer adayların süreleri de değişti; sonuç
her makine/oyun için garanti değildir. Transient için yeni bir hızlanma iddia edilmiyor.

.NET 10.0.12 host'ta başlangıç tahsisi **10984 → 5768 B/op**, **%47,49 azalma**.
Güncel host transient Nexus **541.41 ns / 72 B**, VContainer **204.50 ns / 72 B**;
VContainer burada hâlâ önde. Host allocation değeri Unity allocation değeri değildir.

Unity Mono'nun .NET byte sayacı 4096-byte pozitif kontrolde **0** döndürdü.
Native JSON bu yüzden `AllocationCounterValid=false`, `BytesPerOperation=-1`
kaydediyor. Bu bir sıfır-allocation sonucu değildir. .NET sayacı geçerli kalibrasyona sahip.
GC.Alloc olay sayısını kontrol eden Unity testleri ayrı kanıttır.

Otomatik binder ilk Unity denemelerinde aktifti. Bu sonuçlar superseded olarak
korundu ve yukarıdaki karşılaştırmada kullanılmadı. Güncel runner `NexusDI.ClearCaches()`
ile üretilen factory/injector kayıtlarını warmup öncesi temizliyor; hiçbir adayda
codegen/baking aktif değil. Startup wrapper ve mevcut disposal maliyetini içerir;
Zenject core container'ın disposal aksiyonu yoktur.

## Tekrar çalıştırma ve sonraki amaç

[Yerel runner](../tools/nexus-comparison/README.md),
[paket performans rehberi](../Nexus/Packages/com.nexus.core/docs/PERFORMANCE_COMPARISON.md),
[devam sırası](NEXUS_NEXT_STEPS_2026_10_07.md).

Bir sonraki işin amacı **.NET transient farkının ve tam Context başlangıcının maliyet
kaynağını bulmak**; sadece profilin gösterdiği darboğazlar değiştirilir. Ardından
Android IL2CPP cihaz ölçümü ve gerçek entegrasyon akışları gelir. Tüm alanlarda
üstünlük için bu alanların da ayrı kabul kanıtları gerekir; bir DI tablosu bunu kanıtlamaz.

Yerel, gitignore kapsamındaki ham kanıtlar:

- [Fair baseline Mono](../Nexus/artifacts/performance-20261007/baseline-fair-mono/unity-results.json)
- [Fair current Mono](../Nexus/artifacts/performance-20261007/current-fair-mono/unity-results.json)
- [Current source identity](../Nexus/artifacts/performance-20261007/current-fair-mono/source-identity.json)
- [Original host baseline](../Nexus/artifacts/performance-20261007/before-host.json)
- [Current host](../Nexus/artifacts/performance-20261007/current-fair-mono/host-results.json)
- [EditMode](../Nexus/artifacts/performance-20261007/editmode-final.xml)
- [PlayMode](../Nexus/artifacts/performance-20261007/playmode.xml)
- [Host harness](../Nexus/artifacts/performance-20261007/host-regressions.log)

`baseline-fair-mono/host-results.json` mevcut host kodunun yardımcı koşusudur;
.NET before/after karşılaştırması için yukarıdaki original host baseline kullanılır.
CLI access token'ları sahip olunan loglardan çıkarıldı. Mobil cihaz/frame bütçesi,
Windows IL2CPP, cold metadata ve generated binding rakip karşılaştırması henüz açık.
