> **Kimin için:** seviye 2, entegrasyonun ne kadar derin olacağını seçen geliştirici (ilk özellik, mevcut proje, yükleme sırasında hazırlık, doğrulama, performans kararları).

# Entegrasyon yolunuzu seçin

Nexus, Unity 6 mobil ve PC oyunları için bir mimari temeldir. Hareket, dövüş, kamera
ve bölüm tasarımı oyununuza aittir. Paketi kurmak .NET SDK gerektirmez.
[Kurulum rehberiyle](GETTING_STARTED_TR.md) başlayın.

| Durumunuz | Buradan başlayın | Sonraki adım |
|---|---|---|
| Unity mimari paketlerine yeni | Package Manager → Samples → Code-first Startup; `ScoreBootstrap`'ı ekleyin | Skor yükünü (payload) değiştirin, sonra model ve komutu kendi özelliğinize uyarlayın |
| Sahneleri ve Inspector'ı tercih ediyorum | Nexus penceresindeki Kurulum Sihirbazı | Sayaç sahnesini ve View/Mediator akışını inceleyin |
| Zaten bir oyunum var | Tek bir özellik için `ContextFactory.StartAsync` | Envanter, görev veya ekonomiyi aşamalı ekleyin |
| Büyük ekip / birden çok sahne | Uygulama ve sahne context'lerini ayırın | Açık kayıtlar, named binding'ler, AOT üretimi ve otomatik kontroller kullanın |

## İlk çalışan özelliğiniz

Code-first Startup bir model, sinyal, komut, başlatma, iptal ve dispose işlemini tek
dosyada toplar. Play Mode'a girin; Console'da `Nexus score: 1` bekleyin. Context'i
kullanmadan önce başlatmayı bekleyin (await). Sahibi sona erdiğinde context'i dispose
edin; örnek bunu gösterir.

Akış: `ScoreAdded` → `AddScore` → `ScoreModel.Score`. UI eklediğinizde bir mediator
model değişikliklerini gözler ve view'ı günceller. Modeller UI veya GameObject
referansı içermemelidir. Ekranları kapandığında abonelikleri dispose edin.

## Nexus'u mevcut bir projeye ekleme

1. Projenizi version control ile koruyun ve UPM paketini kurun.
2. Tek bir özellik seçin. Code-first başlatma sahne asset'i veya assembly taraması gerektirmez.
3. Özelliğin model ve komutlarını startup callback'inde açıkça kaydedin.
4. Sinyalleri mevcut MonoBehaviour'ınızdan, dönen context üzerinden yayımlayın.
5. Sonraki özelliği eklemeden önce durumu, iptali ve dispose'u kontrol edin.

Komut bağımlılıkları için constructor kullanın. Açık seçenekleri, komutun sinyal
handler'ını kaydetmeden önce `builder.BindFluent<MyCommand>().WithParameter(options)`
ile kaydedin; komut kaydı yerel transient seçenekleri korur.

`builder.BindInstance(instance)` temizleme sahipliğini context'e devreder. Başka bir
sistemin sahip olduğu bir `IDisposable`'ı bu yolla devretmeyin. Düşük seviyeli DI API'si
`container.BindInstance(instance, disposeWithContainer: false)` destekler; context
yapılandırması sırasında aynı nesneyi sahipli ömürle yeniden kaydetmeyin. Context'e ait
bir adaptör, mevcut servislere net bir sınır sağlayabilir.

`IDependencyAdapter` köprüsü başka bir DI sistemiyle birlikte çalışmayı destekler.
Named binding çözmez; named bağımlılıkları Nexus'ta kaydedin.

## Yükleme sırasında hazırlık

Code-first örneğinde, başlatmadan sonra ve ilk oyun sinyalinden önce:

```csharp
_context.Prewarm<ScoreAdded>(4);
_context.SignalBus.Fire(new ScoreAdded(1));
```

Prewarm hiçbir zaman komut çalıştırmaz, subscriber çağırmaz veya one-shot tüketmez.
Constructor/factory'leri çalıştırır; bunlar oyun durumunu değiştirmemelidir. İstenen
sayıda **boşta** komut bulunmasını sağlar; daha büyük bir varsayılan ilk kapasite korunur.
Maksimum havuz kapasitesini aşan istekler açıkça hata verir. Tekrarlanan çağrılar gereksiz
nesne üretmez. Kapasiteyi iç içe çağrı/eşzamanlılık için seçin; her tipi gereksiz yere
hazırlamak bellek kullanımını ve başlatma işini artırır.

Typed composite komutlar için havuzu doğrudan
`context.PoolManager.Prewarm<MyCompositeCommand>(4)` ile hazırlayın. Async, tracing,
interceptor/decorator ve immutable composite'leri ayrı profilleyin.

## Yapılandırma hatalarını oyun başlamadan yakalayın

`ContextFactory.StartAsync` kayıtları başlatmada doğrular. Düşük seviyeli DI için
kayıttan sonra `container.ValidateBindings()` çağırın ve sonucunu inceleyin.
Doğrulama constructor veya factory çalıştırmaz.

| Sorun | Yapılacak |
|---|---|
| Eksik bağımlılık / named binding | Onu, oluşturan binding'in sahibi olan scope'a veya parent'ına kaydedin |
| Circular dependency | Raporlanan zinciri kırın; veri ve servis sorumluluklarını ayırın |
| Captive dependency | Bağımlılığı cache'leyin veya ömrünü bir factory üzerinden açıkça yönetin |
| Invalid binding | Atanabilir bir implementation, kullanılabilir bir constructor ve uyumlu parametre değerleri sağlayın |

Factory'ler, sağlanan/önceden oluşturulmuş instance'lar ve dış adaptörler opak
sınırlardır. İç yapılarını kendiniz test edin. `LazyInjection<T>` erişildiğinde çözülür;
başlatmadaki eager-cycle analizi lazy erişimleri veya factory içindeki döngüleri izlemez.

## Performans kararları

- Model/servis referanslarını her kare yerine başlatmada çözün.
- Sık olaylar için struct sinyaller ve generic senkron komutlar kullanın.
- Debug/tracing açık ve kapalı durumları ayrı ölçün.
- Mono'da uygun constructor'lar bir kez derlenir ve transient grafiklerde argüman
  dizilerinden kaçınır. Derlemenin kendisi başlatma işidir. Açık generic'ler, optional/value
  parametreler ve açık parametre override'ları yetkili fallback yollarını korur.
- IL2CPP/AOT hiçbir zaman dinamik IL üretmez. AOT binder/source generator kullanın ve gerçek bir player'ı test edin.
- Başlatma süresini, belleği ve frame p95/p99 değerlerini hedef cihazlarda ölçün.

Reklam, IAP ve analytics için üretim provider adaptörlerini kaydedin. Mock provider'lar
çalışan bir ödeme veya reklam entegrasyonunu kanıtlamaz. Network sinyalleri ve rollback
tek başına bir multiplayer backend oluşturmaz.

Bkz. [oyun kalıpları](GAME_PATTERNS.md), [mimari](ARCHITECTURE.md) ve
[sorun giderme](../TROUBLESHOOTING.md); doğrulama durumu deponun `docs/RELEASE_GATES.md` dosyasındadır.

## Sahne bileşenleri, global kapsam ve job'lar

Kullanışlı bir sahne bileşeni için NexusBehaviour kullanın: onu kendi Root'unun altına
yerleştirin veya birden fazla bağımsız code-first context varsa Context'i açıkça atayın.
Kayıt tek başına hazır olmak demek değildir. OnNexusAwake binding/validation'dan sonra,
OnNexusStart ise Unity Start ve async context başlatmasından sonra çalışır. Hook'ları kısa
tutun. Context değişimi veya dispose'u, takip edilen abonelikleri ve inject edilmiş
field/property'leri temizler; yeniden bağlanma, hook'ları yeni context için çalıştırır.
Disable abonelikleri yok etmez. AutoInject kapalıyken elle atanan field'lar sizin
sorumluluğunuzdadır. Tek bir Global Root bootstrap sahnesine aittir; sahne Root'ları kendi
başlatmalarından önce onun serialized flag'ini keşfeder. Yinelenen global'ler açıkça hata
verir. Runtime Root'larını aktive etmeden önce yapılandırın.

Collections/DOTS isteğe bağlıdır: `com.nexus.dots` paketini ve `com.unity.collections`'ı kurun. AsParallelWriter'ı ana thread'de alın, producer'ı schedule
edin, hemen AddProducerDependency(jobHandle) çağırın, sonra drain işini Unity Update'e bırakın.
Her producer'ı kaydedin. Job'ları tamamlamak ana thread'i bekletebilir; uzun job'ları kendi
update planınızda daha erken bitirin. Dispose, hâlâ çalışan bir producer'ı bırakıp geçemez.
NativeSignalQueue'nun kendisi, okuma/dispose öncesinde producer'ları çağıranın tamamlamasını
gerektirir; doğrudan Enqueue tek yazarlıdır, paralel yazma API'si değildir.

## Büyük oyun para birimleri

Economy'nin BigDouble erişimi bir para birimini, kaydedilmiş durumdan önce güncel cache'teki
long durumunu kullanarak yükseltir. Bundan sonra tek bir BigDouble defteri iki API'yi de
yönetir; long okumalar/observable'lar kesirleri atar ve long.MaxValue'da doyar. Elde tutulan
değiştirilebilir bir long observable'a yazmak, canonical tam sayı miktarını açıkça ayarlar.
BigDouble idle/incremental oyunlar için yaklaşık double mantissa aritmetiği kullanır; büyük
tam sayılarda kesin muhasebe garanti etmez. NaN/sonsuz ve negatif Big miktarlar reddedilir.
Var olan bir network validator'ı, Big işlemler için INetworkBigEconomyValidator'ı da
implemente etmelidir; desteklenmeyen SpendBig false döndürür, EarnBig/SetBigBalance
NotSupportedException fırlatır. İstemci validator'ları/obfuscation sunucu otoritesi sağlamaz.
