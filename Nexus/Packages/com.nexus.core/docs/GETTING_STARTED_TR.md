# Unity 6 oyununa Nexus ekleme

Nexus, mobil ve PC oyunları için UPM paketidir. Depodaki demo Unity projesini
oyununuza kopyalamanız gerekmez.

## Kurulum

Unity'de **Window → Package Manager → + → Install package from git URL** seçin:

```text
https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core
```

Yerel geliştirmede **Install package from disk** ile `Packages/com.nexus.core/package.json`
dosyasını seçebilirsiniz. Üretim projesinde URL sonuna `#<commit-veya-etiket>` ekleyerek
test ettiğiniz sürümü sabitleyin. Paketin kod-only kurulumu için .NET 10 kurulması gerekmez;
bu çalışma .NET 10'u bağımsız host doğrulaması için kullanır.

## En kısa çalışan örnek

1. Package Manager'da Nexus'u seçin.
2. Samples bölümünden **Code-first Startup** örneğini içe aktarın.
3. Sahnenizde boş GameObject oluşturup `ScoreBootstrap` component'ini ekleyin.
4. Play'e basın. Console'da `Nexus score: 1` beklenir.

Tek dosya model, sinyal, command, kurulum, iptal ve kapanışı gösterir. Kendi oyununuz
için bu tipleri değiştirin. Bindings `ContextFactory.StartAsync` callback'inde tanımlanır;
doğrulama ve model/service initialization tamamlanmadan context döndürülmez. Sahibi
context'i field'da tutar ve `OnDestroy` içinde kapatır. Başlangıcı Unity ana thread'inde
çalıştırın. Asenkron kapanışı önceden bekleyebildiğiniz akışta `await context.DisposeAsync()`
kullanın; Unity, `OnDestroy` mesajındaki asenkron işlemleri beklemez.

Tam script ve gelişmiş örnekler: [GETTING_STARTED.md](GETTING_STARTED.md).

## Sahne, UI ve gelişmiş mimari

Root + ContextData + IContextLifecycle yoluyla sahne sahipliği, parent/child context,
discovery ve mediator binding kullanabilirsiniz. [MVCS hızlı başlangıç](10_MIN_QUICKSTART_TR.md)
bu akışı açıklar. Dashboard'daki Setup Wizard mevcut örnek klasörünü, ayarları ve sahneyi
silmez; çakışan çıktı varsa oluşturmayı durdurur. Yeni starter sahnesini mevcut sahneleri
kapatmadan ekler. Kendi projenize gerçek backend, reklam ve satın alma adapter'larınızı bağlayın.

## Performans

İki sinyali birleştiren sıcak senkron akışlarda `ICompositeCommand<TFirst,TSecond>` ve
`BindComposite<TFirst,TSecond,TCommand>()` kullanın. Bu yol struct değerleri boxing
yapmadan geçirir; warmup sonrasında host ölçümünde completion başına 0 B bulundu.
Arbitrary-arity/async `CompositeContext` saklanabilir immutable snapshot üretir ve tahsis yapar.
Startup, async işlemler, interceptor/decorator ve debug tracing maliyetlerini ayrı ölçün.

Network input'larını sahibi olan thread'de drain edin; pending input varken tick ilerletme,
top-level owner fire veya rollback açık hata verir. Rollback modelleri için senkron handler
kullanın. [Ölçümler ve doğrulama sınırları](HARDENING.md), Unity/IL2CPP cihaz kanıtından ayrıdır.
