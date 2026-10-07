# Projenize uygun entegrasyon yolu

Nexus, Unity 6 mobil ve PC oyunlarının mimari temelidir. Oyun türüne ait hareket,
dövüş, kamera ve bölüm tasarımı oyununuzda kalır. Paket eklemek için .NET SDK
kurmanız gerekmez. Önce [kurulum rehberini](GETTING_STARTED_TR.md) uygulayın.

| Durumunuz | Başlangıç | Sonraki adım |
|---|---|---|
| Unity veya mimari paketlere yeni başlıyorum | Package Manager → Samples → Code-first Startup; boş GameObject'e `ScoreBootstrap` | Sinyalin puan miktarını değiştirin, sonra model ve komutu kendi özelliğinize uyarlayın |
| Sahne ve Inspector üzerinden çalışıyorum | Nexus penceresindeki Kurulum Sihirbazı | Örnek sayaç sahnesini inceleyin; View/Mediator akışını öğrenin |
| Mevcut oyunum var | Tek bir özellik için `ContextFactory.StartAsync` | Envanter, görev veya ekonomi gibi diğer özellikleri aşamalı ekleyin |
| Büyük ekip / çok sahneli oyun | Uygulama ve sahne context'lerini ayırın | Named binding, açık kayıtlar, AOT üretimi ve otomatik doğrulama kullanın |

## İlk çalışan özellik

Code-first Startup örneği bir dosyada model, sinyal, komut, başlatma, iptal ve
kapanışı gösterir. Play'e basınca Console'da `Nexus score: 1` görünmelidir.
Başlangıç tamamlanmadan context'i kullanmayın. Sahibi yok edildiğinde context'i
kapatın; örnek bu sahiplik düzenini içerir.

Akış: `ScoreAdded` → `AddScore` → `ScoreModel.Score`.
UI eklediğinizde mediator, model değişikliğini view'a taşır. Modelin içine UI veya
GameObject referansı koymayın. Ekran kapanınca abonelikleri kapatın.

## Mevcut projeye aşamalı ekleme

1. Mevcut projenizi version control ile koruyun; paketi UPM üzerinden kurun.
2. Tek özellik seçin. Kodla başlangıç, sahne varlığı ve assembly taraması gerektirmez.
3. Özelliğin model ve komutlarını startup callback'inde açıkça kaydedin.
4. Mevcut MonoBehaviour, döndürülen context üzerinden sinyal yayımlasın.
5. Özelliğin durumu, iptal ve kapanışı doğru çalışınca bir sonraki özelliğe geçin.

Komut constructor'larına servis/model bağımlılıklarını alın; kullanıcı seçenekleri
için `builder.BindFluent<MyCommand>().WithParameter(options)` kaydını komut
kaydından önce yapabilirsiniz. Komut kaydı yerel transient seçeneklerini korur.

`builder.BindInstance(instance)` context'e temizleme sahipliği verir. Başka bir
sistemin sahibi olduğu `IDisposable` nesneyi bu yolla devretmeyin. Düşük seviyeli
DI API'sinde `container.BindInstance(instance, disposeWithContainer: false)` vardır;
context kurulumunda aynı nesneyi sahipli bir kayıtla tekrar kaydetmeyin. Ömrü
context ile sınırlı bir adaptör kullanmak mevcut servislerle güvenli bir sınır sağlar.

Nexus'un `IDependencyAdapter` köprüsü mevcut DI sisteminizle birlikte çalışma
içindir. Köprü named binding çözmez; named bağımlılıkları Nexus'ta kaydedin.

## Yükleme ekranında hazırlık

Code-first örneğinin başlangıcındaki kullanım:

```csharp
// Context başladıktan sonra, ilk oyun sinyalinden önce:
_context.Prewarm<ScoreAdded>(4);
_context.SignalBus.Fire(new ScoreAdded(1));
```

`Prewarm` komutu çalıştırmaz, subscriber çağırmaz ve one-shot kaydını tüketmez.
Constructor/factory çalışır; bunlar oyun durumunu değiştirmemelidir. Havuz, istenen
kadar **boşta** komut tutar; varsayılan ilk kapasite daha büyükse mevcut nesneleri
silmez. İstek maksimum havuz kapasitesini aşarsa açık hata verir. Tekrar çağrılması
gereksiz nesne üretmez. İç içe veya eşzamanlı komut sayınıza göre kapasite seçin;
tüm tipleri gereksiz yere hazırlamak bellek ve açılış maliyetini artırır.

Typed composite komut havuzunu doğrudan hazırlamak için
`context.PoolManager.Prewarm<MyCompositeCommand>(4)` kullanın. Async, debug,
interceptor/decorator ve immutable composite maliyetlerini ayrıca profil edin.

## Hataları oyun başlamadan görmek

`ContextFactory.StartAsync`, kayıtları başlangıçta doğrular. Düşük seviyeli DI
kullanıyorsanız kayıtları tamamladıktan sonra `container.ValidateBindings()`
çağırıp dönen sorunları inceleyin. Doğrulama constructor/factory çalıştırmaz.

| Rapor | Düzeltme |
|---|---|
| Eksik bağımlılık / named binding | Kaydı, bağımlılığı oluşturan context'in kendi scope'una veya parent'ına ekleyin |
| Circular dependency | Rapordaki zinciri kırın; model verisini ve servis sorumluluklarını ayırın |
| Captive dependency | Uzun yaşayan nesnenin transient bağımlılığını cached yapın veya ömrünü açık factory ile yönetin |
| Invalid binding | Uyumlu implementation, kullanılabilir constructor ve geçerli özel parametre değeri sağlayın |

Factory'ler, önceden sağlanan/oluşturulmuş instance'lar ve external adapter içi
doğrulamaya kapalı sınırlar oluşturur. İç davranışlarını kendi testlerinizde
doğrulayın. `LazyInjection<T>` erişim anında çözülür; başlangıçtaki eager cycle
kontrolü lazy erişimleri ve fabrikaların kendi iç döngülerini incelemez.

## Performans için kararlar

- Her kare DI çözümlemek yerine model/service referansını başlangıçta alın.
- Sık sinyallerde struct ve generic senkron komut kullanın; yoğun akışları async yapmayın.
- Üretim bütçesini debug/tracing açık ve kapalı olarak ayrı ölçün.
- Mono'da uygun constructor'lar bir kere derlenir; transient grafikte gereksiz argüman
  dizileri kaldırılır. İlk derleme açılış maliyetidir. Açık generic, optional/value
  parametreler ve özel parametre override'ları mevcut fallback yolunu korur.
- IL2CPP/AOT dinamik IL üretmez. AOT binder/source generator kullanın; gerçek player'ı test edin.
- Başlangıç süresi, kullanılan bellek ve frame p95/p99 değerlerini hedef cihazda ölçün.

Reklam/IAP/analytics için gerçek provider adaptörlerinizi kaydedin. Mock provider
ile ödeme veya reklam entegrasyonunun tamamlandığını varsaymayın. Network sinyal
ve rollback katmanı, tek başına multiplayer backend kurmaz.

Bkz. [doğrulama kapsamı](HARDENING.md), [oyun kalıpları](GAME_PATTERNS.md),
[mimari](ARCHITECTURE.md), [sorun giderme](../TROUBLESHOOTING.md).

## Sahne bileşenleri ve hazır context

NexusBehaviour'ı kendi Root'unun altında kullanın; birden fazla bağımsız code-first
context varsa Context'i açıkça atayın. OnNexusAwake binding/validation tamamlandığında,
OnNexusStart Unity Start ve async servis başlangıcı tamamlandığında çağrılır. Context
değişimi/disposal takip edilen abonelikleri ve inject edilmiş field/property'leri
temizler; yeni context'e bağlanınca hook'lar yeniden çalışır. Disable abonelikleri
sonlandırmaz. AutoInject kapalıysa elle atadığınız referansları siz yönetirsiniz.
Bootstrap sahnesinde tek Global Root kullanın; serialized global flag sahne Root'ları
kurulmadan keşfedilir, ikinci global reddedilir. Runtime Root'ları aktive etmeden önce
konfigüre edin.

Collections/DOTS isteğe bağlıdır. Ana thread'de AsParallelWriter alın, producer job'u
schedule edin ve Unity'ye kontrol vermeden hemen AddProducerDependency(jobHandle)
çağırın. Her producer'ı kaydedin. Drain/yeniden kurulum/destroy job'ları tamamlar;
uzun job'ları kendi update planınızda daha erken bitirin. NativeSignalQueue doğrudan
kullanılıyorsa job tamamlama sahipliği sizdedir; Enqueue tek yazarlıdır.

## Büyük oyun para birimleri

BigDouble API, aynı currency'nin güncel long cache'ini diskten önce kullanarak tek
canonical bakiyeye geçer. Long gözlemlenebilir nesne aynı kalır, kesirleri keser ve
long.MaxValue'da doyar. Bu nesneye elle long yazmak canonical bakiyeyi o tam sayıya
ayarlar. BigDouble yaklaşık double mantissa kullanır; idle oyun sayılarını taşır,
büyük tam sayılarda kesin muhasebe doğruluğu sağlamaz. NaN/Infinity ve negatif Big
miktarlar reddedilir. Backend varsa INetworkBigEconomyValidator desteği gerekir;
destek yoksa SpendBig false, EarnBig/SetBigBalance NotSupportedException döndürür.
Yerel doğrulama/obfuscation sunucu otoritesi yerine geçmez.
