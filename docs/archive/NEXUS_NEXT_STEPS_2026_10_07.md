# Nexus — sonraki geliştirme sırası

Tarih: 7 Ekim 2026. Kod başlangıç noktası: `f1b537adb0f6c22619753c888c0e5230e8cf4927`.
Güncel durum: İlk Unity Mono core DI karşılaştırması ve optional-map başlangıç
optimizasyonu uygulandı; genel Context profili, cihaz kabulü ve sonraki maddeler açık.
CI kullanıcı isteğiyle kapsam dışıdır; doğrulamalar yereldir.
[Güncel inceleme](NEXUS_REVIEW_2026_10_07.md), eski yol haritalarındaki durum
iddialarından daha güncel kanıttır. Amaç: Unity 6 / UPM ile mobil ve PC için hızlı,
güvenilir, kolay benimsenen mimari paket.

## Başlangıç kanıtı

- Unity 6000.5.6f1: 352 EditMode geçti; 160 PlayMode geçti, 2 koşullu test atlandı.
- Temiz tarball tüketicisi: 107 test, 15 doğrulama aşaması başarılı.
- Host harness: 291 kontrol başarılı; Windows Mono development player çalıştı.
- Android ARM64 IL2CPP development build başarılı; fiziksel cihaz çalışması yapılmadı.
- .NET karşılaştırması: Nexus cached resolve 10.51 ns / 0 B;
  transient üç nesne 587.97 ns / 72 B; register/build/first resolve 4079.30 ns / 10984 B.
  VContainer transient ve başlangıç ölçümlerinde önde. Bunlar Unity ölçümü değildir.

## 1. İlk iş: Unity performans ölçümü ve darboğaz teşhisi

Sahip olunan çalışma alanları: `Runtime/Core/NexusDI.cs` içindeki Injector/resolve
yolları, `DiBindingValidator.cs`, Context başlangıcı ve benchmark araçları.

- Mevcut üç nesneli karşılaştırmayı gerçek Unity Mono player'a taşı; aynı lifetime,
  nesne grafiği, doğrulama, teardown ve aday sırası kullan. Editör ölçümünü player
  ölçümünden ayır; Nexus/VContainer/Zenject sürümlerini sabitle.
- Metadata soğuk ve sıcak durumlarını ayrı ölç. Tam Context başlangıcını core DI
  build ölçümünden ayır; validation/discovery/prewarm süre ve allocation katkılarını çıkar.
- Reflection, mevcut Mono constructor delegate ve üretilen binder yollarını ayrı
  çalıştır. Rakiplerde codegen/baking kullanıldığında bunu ayrıca raporla.
- Ham tur verisi, commit, backend, Unity sürümü, debug durumu, profiler ayarları ve
  allocation pozitif kontrolünü kaydet. Frame p95/p99 için örnek oyun iş yükü kullan;
  operasyon medyanından frame yüzdelikleri türetme.

Kabul: aynı yaşam döngüsü ve sonuçları doğrulayan tekrarlanabilir player ölçümü;
transient ve başlangıç maliyetlerinin ölçülmüş kaynakları. Önce bu kanıt elde edilir,
sonra optimizasyon seçilir. Doğrulamalar mevcut yerel Unity ve .NET araçlarıyla yapılır.

## 2. Ölçülen maliyetleri azalt

- Transient resolve ve başlangıç allocation'ında en yüksek katkı yapan yolları düzelt.
  Mevcut cached resolve davranışını, named/default scope sahipliğini, optional/override
  injection ve disposal sözleşmelerini koru.
- Reflection cache veya factory değişikliği ancak profil gerekçe gösteriyorsa yapılır.
  Startup validation kaldırılmaz; nesne tahsisini saklamak için lifetime değiştirilmez.
- Üretilen binder'ı Android IL2CPP üzerinde gerçek bootstrap/resolve/disposal ile çalıştır;
  derlemenin başarılı olması generator runtime kabulü sayılmaz.

Kabul: sabit koşullarda önce/sonra ham ölçümler; geçerli regresyon kapılarının tamamı
başarılı; iyileşmeyen veya gerileyen senaryolar açıkça raporlanır. Rekabetçi hedef,
transient ve başlangıçta VContainer ile farkı kapatmaktır; sonuç ölçülmeden üstünlük
veya sıfır allocation iddiası yazılmaz. Windows IL2CPP için C++ build araçları gerekir.

## 3. Gerçek cihaz ve uzun çalışma

Android cihazda IL2CPP player: soğuk açılış, sahne/scope değişimi, background/resume,
uzun sinyal/komut yükü, storage kesintisi ve art arda teardown. PC'de aynı iş yükü;
iOS için macOS/Xcode ve cihaz gerektiği ayrıca kaydedilir.

Kabul: cihaz/OS/termal durum ve build bilgisiyle p95/p99, GC, bellek eğilimi ve hata
kayıtları; üretim ayarlarında ölçüm. Development APK build'i cihaz kabulünün yerine geçmez.
Unity 6000.0 ile 6000.5 API dalları ayrı uyumluluk koşularında doğrulanır.

## 4. Kolay entegrasyonun kullanıcı kabulü

EN/TR quickstart ve mevcut üç sample üzerinden yeni ve deneyimli geliştiricilerle
kurulum denemesi. Yeni boş proje ile mevcut projeye ekleme ayrı senaryolar olsun.
Kurulum süresi, yardım ihtiyacı, hata mesajı anlaşılabilirliği ve doğru teardown ölçülsün.

Gerçek sağlayıcılarla input/save/network örnekleri ve iki uçtan uca oyun kesiti:
mobil casual ilerleme/kayıt akışı ve PC aksiyon sahne/scope/input akışı. Sağlayıcı
bağımlılıkları isteğe bağlı modüllerde tutulur. Bu iki örnek tüm türlerin kabulü sayılmaz.

Kabul: rehberden başlayarak çalışan örnek ve temiz kapanış; gereken manuel adımlar
belgeli, kullanıcı denemeleri sonuçları kayıtlı, sağlayıcıların hata/iptal davranışı testli.

## 5. Yerel sürüm doğrulaması

Kullanıcı kararı: CI geliştirmesi veya GitHub Actions çalıştırması yapılmaz.
Host testleri, Unity Edit/Play, temiz immutable UPM tüketicisi ve backend build/runtime
kanıtları yerelde çalıştırılır ve ayrı sonuçlar olarak saklanır. Ham log erişim tokenlarını yayınlama.

Kabul: başarısız veya atlanan kontrol gizlenmeden raporlanır; SHA ile bağlı paket
üretilir, migration/versioning ve EN/TR rehberleri aynı kaynakla eşleşir. Lisans veya
cihaz erişimi olmayan kapılar başarılı ilan edilmez. Önce bu kapılar, sonra kararlı sürüm.

Bir sonraki uygulama adımı: **tam Context başlangıcı ve .NET transient maliyetinin
profili**, ardından ölçülen darboğazın düzeltilmesi. Native core DI runner hazırdır;
güncel sonuçlar paket içindeki PERFORMANCE_COMPARISON.md belgesindedir. Önceki tarball
kanıtı kendi tarihli kaynak sürümüne aittir; bu optimizasyon için yeni tarball doğrulaması
henüz yapılmış sayılmaz.
