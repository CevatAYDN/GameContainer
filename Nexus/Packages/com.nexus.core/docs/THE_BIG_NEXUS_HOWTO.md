# The Big Nexus How-To

> Neden önce, nasıl sonra. Bu rehber Nexus'u *kural listesi* olarak değil, bir
> *hikâye* olarak anlatır: her kalıbın **neden** var olduğunu, hangi problemi
> çözdüğünü ve onu ne zaman kullanıp ne zaman kullanmayacağınızı. Kurallar
> (canonical listesi) için [CANONICAL-PATTERNS.md](./CANONICAL-PATTERNS.md)'e
> bakın. Burada amaç anlamak.

---

## Bölüm 1 — Neden bir "çerçeve" var?

Küçük bir oyun yazarken her şey bir MonoBehaviour'un içine sığar: `Update()`
girdiyi okur, parayı arttırır, bir text'i günceller, ses çalar. 500 satırda hâlâ
idare edersiniz. Ama üçüncü ekran, ikinci oyun modu ve ilk ağ senkronizasyonu
geldiğinde aynı dosya 3000 satır olur. Şimdi:

- Bir satır değiştirmek için beş yeri taramak zorundasınız (buraya nerede
  dokunuyordu?).
- Kimse "bu ekranı kapatınca neler temizlenir?" sorusuna tek bakışta cevap
  veremez.
- Test yazmak için sahneyi açmanız gerekir çünkü mantık Unity'ye gömülüdür.

Nexus'un cevabı tek bir fikir: **sorumlulukları birbirinden kopar ve aralarına
disiplinli, arama yapılabilir bağlantılar koy.** Her parça tek bir iş yapar;
parçalar birbirini doğrudan tanımaz; aralarındaki her bağlantı kod tabanında
görünürdür (greppable).

Bunu yapan ilk framework StrangeIoC idi (ve onun atası Robotlegs). Nexus aynı
dili konuşur — Context, Signal, Command, Mediator — ama üç şeyi kökten değiştirir:

1. **Performans:** sinyaller `struct`, komutlar ve mediator'ler havuzlanır,
   çözümleme sıcak yolda sıfır GC üretir.
2. **Disiplin:** iki kayıt yolu (açık `BindCommand` ve prototip `[SignalHandler]`)
   vardır ama canonical olan birdir ve bu rehber size hangisini ne zaman
   kullanacağınızı nedenleriyle söyler.
3. **Yetenek:** Strange'den bilinen genel Binder, adlandırılmış enjeksiyon ve
   `[PostConstruct]` Nexus'a *yerli* olarak eklenmiştir — aşağıda Bölüm 7'de.

---

## Bölüm 2 — İlk buluşma: Signal ve Command

En temel ayrım **olay taşıma** ile **iş yapma** arasındadır.

Bir butona basıldı. Bunu bildirmenin yolu bir **Signal**'dir: "bu oldu" diyen,
veri taşıyan küçük bir struct.

```csharp
public readonly struct CoinsAddedSignal
{
    public readonly int Amount;
    public CoinsAddedSignal(int amount) => Amount = amount;
}
```

Signal'in *yaptığı* hiçbir şey yoktur. O sadece bir mesajdır. Mesajı *alan ve
iş yapan* şey **Command**'dır:

```csharp
public class AddCoinsCommand : ICommand<CoinsAddedSignal>
{
    [Inject] public PlayerWallet Wallet { get; set; }

    public void Execute(CoinsAddedSignal signal)
    {
        Wallet.Add(signal.Amount);
    }
}
```

Neden bu ayrım? Çünkü iki taraf da birbirini tanımıyor. Butonun `Wallet`'tan
haberi yok; cüzdanın butondan haberi yok. Aradaki tek bağ, ikisinin de bildiği
bir mesaj. Birini değiştirmek diğerini bozmaz.

Bağı kurarsınız — açıkça, `OnConfigure` içinde:

```csharp
public void OnConfigure(IContextBuilder builder)
{
    builder.BindCommand<CoinsAddedSignal, AddCoinsCommand>();
}
```

Signal'i ateşlemek (fire etmek) ise şudur:

```csharp
_signalBus.Fire(new CoinsAddedSignal(10));
```

İşte bütün döngü. Basit görünüyor çünkü öyledir — güç, bu küçük parçaların
bileşimindedir.

---

## Bölüm 3 — Neden Command, neden doğrudan metot çağrısı değil?

Dürüst bir soru: `button.OnClick += () => wallet.Add(10)` neden yeterli değil?

Üç nedeni vardır:

1. **Görünürlük.** `BindCommand` satırı, "bu sinyal bu komutu tetikler" der ve
   kod tabanında aranabilir. `OnClick +=` lambda'ları projeye dağılır; kimsenin
   onları bulması kolay değildir.
2. **Birden çok dinleyici.** Aynı sinyale başka bir komut daha bağlamak için
   çağrı tarafını değiştirmezsiniz; sadece bir satır eklersiniz. Çağıran,
   kimlerin dinlediğini bilmek *zorunda değildir*.
3. **Zamanlama kontrolü.** `ExecutionMode` ile komutların sırayla mı, paralel mi,
   yoksa özel olarak mı (Exclusive) çalışacağını yönetirsiniz. Doğrudan çağrıda
   böyle bir politika katmanı yoktur.

> **Ne zaman kullanmayın:** Tek bir tüketicisi olan, oyuna özgü olmayan,
> birbirine sıkı bağlı iki MonoBehaviour arasındaki iletişim. Orada düz
> `GetComponent<T>()` + metot çağrısı daha az tören gerektirir. Sinyaller
> *çapraz* iletişim içindir; komşu için değil.

---

## Bölüm 4 — Mediator: View'ı temiz tutmak

Şimdi görsel katman. Bir UI paneli düşünün: para sayacını gösteriyor.

**View** yalnızca görselle ilgilenir: sayıyı nasıl çizeceğini bilir, ne anlama
geldiğini bilmez.

```csharp
public class WalletHudView : NexusView
{
    public Text AmountLabel;

    public void SetAmount(int amount) => AmountLabel.text = amount.ToString("N0");
}
```

**Mediator**, View ile oyun arasındaki tercümandır: sinyalleri dinler ve View'ı
günceller, View'dan gelen kullanıcı eylemlerini sinyale çevirir.

```csharp
[Mediator(typeof(WalletHudView))]
public class WalletHudMediator : Mediator<WalletHudView>
{
    [Inject] public SignalBus SignalBus { get; set; }

    public override void OnBind()
    {
        SignalBus.Subscribe<CoinsAddedSignal>(OnCoinsAdded);
    }

    public override void OnUnbind()
    {
        SignalBus.Unsubscribe<CoinsAddedSignal>(OnCoinsAdded);
    }

    private void OnCoinsAdded(CoinsAddedSignal s) => View.SetAmount(s.Amount);
}
```

Neden iki sınıf? Çünkü **View yeniden kullanılabilir, Mediator değil** (oyuna
özgüdür) ve **Mediator test edilebilir, View değil** (Unity nesnesidir).
Görseli değiştirmek için mediator'ü bozmazsınız; mantığı değiştirmek için
View'ı bozmazsınız.

Mediator'ler Nexus'ta havuzlanır (tip başına 64'e kadar) ve `OnUnbind`/`OnReset`
sözleşmesi havuz hijyenini garanti eder. Bu, Strange'deki `OnRemove()`'un
performans bilinçli karşılığıdır.

---

## Bölüm 5 — Context: hepsinin sahibi

Signal, Command, Mediator — hepsi bir **Context**'in içinde yaşar. Context,
uygulamanın (veya bir sahnenin) bağımsız bir bölgesidir: kendi DI container'ı,
kendi SignalBus'ı, kendi yaşam döngüsü.

Kurulum sözleşmesi bir MonoBehaviour üzerindedir ve isim konvansiyonuyla
otomatik keşfedilir: `{ScopeTag}Lifecycle`.

```csharp
public class GameLifecycle : MonoBehaviour, IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        // Bağımlılıklar: servisler, modeller, komutlar, binder'lar.
        builder.BindService<IPlayerWallet, PlayerWalletService>();
        builder.BindCommand<CoinsAddedSignal, AddCoinsCommand>();
        builder.Bind<WalletHudMediator>();
    }

    public async ValueTask OnInitializeAsync(CancellationToken ct) { /* async hazırlık */ }
    public async ValueTask OnStartAsync(CancellationToken ct) { /* oyunu başlat */ }
    public void OnDispose() { /* kaynakları bırak */ }
}
```

Yaşam döngüsü dört aşamadır ve **asenkron** çalışır:

```
OnConfigure ─▶ OnInitializeAsync ─▶ OnStartAsync ─▶ (oyun) ─▶ OnDispose
```

Neden asenkron? Çünkü yükleme ekranları, ağ bağlantıları ve asset yüklemeleri
`await` gerektirir. Senkron bir kurulum bu işleri bloklar; Nexus size başlangıcı
bekletme kontrolü verir.

---

## Bölüm 6 — Enjeksiyon: bağımlılıkları kim verir?

Command'ın `[Inject] public PlayerWallet Wallet` satırını hatırlıyor musunuz?
`PlayerWallet`'ı kim yarattı, kim verdi? **Container.**

`OnConfigure`'da bir şeyi `Bind...` ile kaydedersiniz:

```csharp
builder.BindInstance<PlayerWallet>(new PlayerWallet());
builder.BindService<IPlayerWallet, PlayerWalletService>();
```

Ve ihtiyaç duyan her yerde `[Inject]` ile alırsınız. Container, tipi çözer,
singleton ise aynı örneği her seferinde verir, geçici ise her seferinde
yeniden üretir.

Enjeksiyonun amacı gizem değil, **sorumluluk devri**dir: bir sınıf
bağımlılıklarını *kendisi bulmak* yerine (new, GetComponent, static singleton)
onları *talep eder*. Bu, sınıfı test edilebilir kılar çünkü bağımlılıkları
testte değiştirebilirsiniz.

---

## Bölüm 7 — Strange'den gelen yetenekler (bu sürümde eklendi)

Nexus artık Strange kullanıcılarının aşina olduğu üç yeteneği yerli olarak
sunuyor.

### 7.1 Genel Binder — `Bind(herhangiBirŞey).To(herhangiBirŞey)`

MVCS container'ı *tipler için* çalışır. Peki ya bir **tablo** bağlamak
istiyorsanız? Birim kataloğu, config kaydı, tema tablosu, çoklu yükleyici —
yani "şu anahtar → şu değer" eşlemesi?

`NexusBinder<TKey, TValue>` budur. Onu context'e kaydedersiniz:

```csharp
// OnConfigure
builder.BindBinder<UnitType, UnitDefinition>();
```

Her yerde enjekte edersiniz:

```csharp
[Inject] public IBinder<UnitType, UnitDefinition> Units { get; set; }
```

Doldurur ve okursunuz:

```csharp
Units.Bind(UnitType.Warrior).To(new UnitDefinition("Warrior", 120));
Units.Bind(UnitType.Warrior).ToName("elite").To(new UnitDefinition("Elite", 200));
Units.Bind(UnitType.Archer).ToFactory(() => new UnitDefinition($"Archer_{n++}", 70));
Units.Bind(UnitType.Wizard).To<WizardDefinition>(); // container'dan çözülür

var def = Units.Get(UnitType.Warrior);          // varsayılan
var elite = Units.Get(UnitType.Warrior, "elite"); // adlandırılmış
```

`ToFactory` her `Get`'te taze örnek verir; `To<T>()` somut tipi DI
container'ından çözer; okuma yolları sıfır GC'dir (sadece dictionary lookup).

**Ne zaman kullanın:** MVCS kalıplarının dışına taşan, *veri* eşlemeleri.
**Ne zaman kullanmayın:** zaten Command/Service/Model olarak yaşaması gereken
davranışları yapıştırmak için.

### 7.2 Adlandırılmış enjeksiyon — `[Inject(Name = "...")]`

Aynı tipten birden fazla bağımlılık ister misiniz? Strange'de
`[Inject(NamedInjections.PRIMARY)]` vardı; Nexus'ta:

```csharp
builder.BindInstance<string>("gameName", "Idle Project");
builder.Bind<BStorage>("primary");
builder.BindInstance<BStorage>("secondary", new BStorage("backup"));
```

```csharp
[Inject(Name = "gameName")] public string GameName { get; set; }
```

#### Adlandırılmış çözümleme strict'tir

Açıkça istenen ama kayıtlı olmayan bir ad **sessizce default bağlamaya düşmez**
— yazım hatası maskelenmesin diye `Resolve`'ta hata fırlatır. Sözleşme:

| Çağrı | Ad kayıtlı | Sonuç |
|---|---|---|
| `Resolve<T>(name)` | hayır | `InvalidOperationException` (strict) |
| `TryResolve<T>(name)` | hayır | `null` |
| `IsRegistered(type, name)` | hayır (default da yok) | `false` |
| `Resolve<T>("")` veya `Resolve<T>(null)` | — | her zaman default yola düşer |

```csharp
// "primary" kayıtlı, "primray" değil:
builder.Bind<BStorage>("primary");

var ok = ctx.Container.Resolve<BStorage>("primary");          // ✓ çalışır
var nullTry = ctx.Container.TryResolve<BStorage>("primray");  // null (opsiyonel)
try { ctx.Container.Resolve<BStorage>("primray"); }
catch (InvalidOperationException) { /* typo yakalandı — strict davranış */ }
```

Bu, "birincil/ikincil" gibi varyantlar için doğal yapıdır ve yanlış yazılmış bir
`[Inject(Name = "...")]` strict injection'da **kayıt zamanında** değil, o
bağımlılık çözülürken yakalanır — yani yazım hatası ancak o tip ilk kez
enjekte edildiğinde patlar. Opsiyonel adlı bağımlılıklar için `[Optional]` ile
birlikte `TryResolve(type, name)` kullanın; boş ad her zaman varsayılan yolu
izler. Bu, kayıtsız adın default'a sessizce düşüp hatayı gizlemesini engeller
(kod review'da düzeltilen bir asimetri).

### 7.3 `[PostConstruct]` — enjeksiyon bitti, hazırlan

Enjeksiyon tamamlandıktan **sonra** çalışan metot:

```csharp
public class CatalogService
{
    [Inject] public IBinder<UnitType, UnitDefinition> Units { get; set; }

    [PostConstruct]
    private void OnConstructed()
    {
        // Burada Units hazırdır — kurulum mantığını constructor'a koymayın,
        // çünkü enjeksiyon henüz yapılmamış olabilir.
        Units.Bind(UnitType.Warrior).To(new UnitDefinition("Warrior", 120));
    }
}
```

Birden çok metot `Order` ile sıralanır:

```csharp
[PostConstruct(Order = 0)] private void First() { }
[PostConstruct(Order = 10)] private void Second() { }
```

**Neden gerekli?** Constructor, enjeksiyondan *önce* çalışır — `[Inject]`
alanlar henüz null'dur. `[PostConstruct]` size "bağımlılıklarım hazır" garantisi
veren tek yerdir.

### 7.4 `[Deconstruct]` — ölmeden önce temizle

Container singleton'ları `Dispose` ederken, imha edilen nesnenin
`[Deconstruct]`-işaretli metotları `Order` sırasına göre **önce** çalıştırılır
(`IDisposable.Dispose`'tan önce). Bu, dış kaynakları (listener, abonelik,
handle) enjeksiyonlar hâlâ canlıyken bırakmanızı sağlar:

```csharp
public class MatchSession
{
    [Inject] public IPlayerRegistry Registry { get; set; }

    [Deconstruct]
    private void LeaveRoom()
    {
        // Registry hâlâ non-null — bağımlılıklarla temizlik yapılabilir.
        Registry.Remove(this);
    }
}
```

`[PostConstruct]` ile simetriktir: biri doğuşta hazırlar, diğeri ölümde temizler.

### 7.5 `[Construct]` — tercih edilen constructor

Bir sınıfta birden çok constructor varsa ve hangisinin DI tarafından
kullanılacağını açıkça işaretlemek istiyorsanız (Strange'deki `[Construct]`
alias'ı):

```csharp
public class MatchSession
{
    public MatchSession() { /* parametresiz — varsayılan */ }

    [Construct]
    public MatchSession(IPlayerRegistry registry) { /* DI bunu seçer */ }
}
```

Nexus ayrıca `[Inject]`'i constructor'da da kabul eder; iki yazım da aynı şeyi
seçer. Hiçbiri yoksa ve birden çok constructor varsa parametresiz olan tercih
edilir.

### 7.6 `.Once()` — tek seferlik komut

Strange'deki `Bind(GameEvent.HIT).To<Cmd>().Once()` kalıbının Nexus karşılığı:
komut bir kez ateşlenir, çalışır ve **kendini kayıttan siler**. İkinci ateşleme
sessizce hiçbir şey yapmaz — handler gerçekten kaldırılmıştır, guard değil:

```csharp
builder.BindCommandOnce<StartSignal, StartCommand>();
builder.BindAsyncCommandOnce<BootstrapSignal, BootstrapCommand>();

// Fluent form da aynı şeyi yapar:
builder.BindSignal<StartSignal>().Once().To<StartCommand>();
```

**Eşzamanlılık garantisi:** one-shot handler, çalıştırılmadan **önce** atomik
olarak claim edilir (`TryClaimOneShot`). Aynı read-copy anlık görüntüsünü gören
birden çok thread aynı anda ateşlerse, kazanan çalışır, kaybedenler claim'i
kaybedip atlar — komut **tam olarak bir kez** çalışır. Async one-shot'lar da
ilk `await`'ten önce senkron tüketilir, böylece komut hâlâ beklerken ikinci bir
ateşlemenin araya girmesi imkânsızdır (sync, async-sequential ve
async-concurrent üç yol da aynı sözleşmeyi taşır).

**Throw semantiği:** claim, çalıştırmadan önce gerçekleştiği için, komut
çalışırken exception fırlatsa bile o one-shot **tüketilmiş sayılır** ve kayıttan
kalkar — yani bir kez ateşlenmeye çalışıldı, tekrar çalışmaz. Bu, kayıtlı
kalmış kırık bir handler'ın her fire'da tekrar patlamasından daha güvenlidir.

**Ne zaman kullanın:** uygulama başlangıcı, tek seferlik kurulum, kullanıcı
ilk etkileşimi, one-time ödül. Sonraki tetiklemelerde yeniden kayıt
gerektirmez — her `Fire` taze havuzlanmış örnek kullanır ve ardından
kayıt silinir.

### 7.7 Polimorfik binding — bir concrete, birçok interface

Strange'deki `Bind<IHittable>().Bind<IUpdateable>().To<Romulan>()` kalıbının
Nexus karşılığı: tek concrete sınıf, birden çok interface altında kayıtlı ve
**tüm anahtarlar aynı singleton örneği paylaşır**:

```csharp
builder.BindMultiple<IBUnit, IAttackable, IUpdatable, CombatUnit>();

// Üç interface de AYNI CombatUnit örneğini döndürür.
var unit = ctx.Resolve<IBUnit>();
var attackable = ctx.Resolve<IAttackable>(); // unit ile aynı referans
```

**Ne zaman kullanın:** bir sınıf birden çok rol oynuyor ve tüketicilerin her
rolü ayrı interface'ten görmesi gerekiyor. Ayrı ayrı `Bind` yapsaydınız her
interface farklı singleton üretirdi; `BindMultiple` bunu önler.

---

## Bölüm 8 — İki kayıt yolu ve canonical seçim

Nexus komutları iki yolla bağlar. Farkı bilmek, doğru olanı seçmektir:

| Yol | Kod | Ne zaman |
|---|---|---|
| Açık `BindCommand` | `builder.BindCommand<S, C>();` | **Gerçek özellikler.** Aranabilir, AOT-güvenli, review'da tartışmasız. |
| Attribute `[SignalHandler]` | `[SignalHandler(typeof(S))] class C : ICommand<S>` | **Sadece prototip.** Hızlıdır ama dağınıktır. |

Karar kuralı tek cümle: *feature gerçek olduğu anda açık bağlamaya geçin.*

Bu "çift yol" bilinçlidir: Strange'den gelen ekipler hızlı denemek ister,
üretim disiplini ise açık kayıt ister. Nexus ikisini de sunar, canonical olanı
söyler.

---

## Bölüm 9 — Performans sözleşmesi (neden bu kadar hızlı)

Nexus'un diğer framework'lere üstünlüğü kavramlarda değil, uygulamada:

- **Struct sinyaller:** `SignalBus.Fire` kutusuz (boxing-free) çalışır.
- **Komut havuzu:** `CommandPoolManager` komut örneklerini geri dönüştürür;
  `Get`/`Return` döngüsü sabit durumda ~0 bayt tahsis eder.
- **Mediator havuzu:** tip başına 64'e kadar geri dönüşüm.
- **AOT binder:** reflection yerine derleme anında üretilmiş çözücüler.
- **Ölçümlenmiş sözleşme:** `tools/nexus-benchmark` harness'ı her koşuda bu
  sayıları doğrular (ör. 5000 dispatch ≤ 128 bayt).

Harness'ı kendiniz çalıştırabilirsiniz:

```bash
cd GameContainer/tools/nexus-benchmark
dotnet run -c Release
```

---

## Bölüm 10 — Hızlı karar rehberi

| Sorunuz | Cevap |
|---|---|
| "Bu oldu" diye bildirmem lazım | `Signal` (struct) |
| Bir mesaja tepki olarak iş yapmam lazım | `Command` + `BindCommand` |
| UI'yı güncellemem lazım | `View` (görsel) + `Mediator` (tercüman) |
| Kalıcı tek örnek lazım (servis) | `BindService<TInterface, TImpl>()` |
| Oyun içi durum lazım (model) | `BindReactiveModel<TInterface, TImpl>()` |
| Tip dışı anahtar→değer tablosu lazım | `BindBinder<TKey, TValue>()` |
| Aynı tipten birden fazla bağımlılık | `Bind<T>(name)` + `[Inject(Name = "...")]` |
| Enjeksiyon sonrası hazırlık | `[PostConstruct]` |
| İmha öncesi temizlik | `[Deconstruct]` |
| Birden çok ctor'dan birini seçmek | `[Construct]` |
| Tek seferlik komut | `BindCommandOnce` / `.Once()` |
| Bir concrete → birçok interface | `BindMultiple<T1, T2, ..., TImpl>()` |
| Sadece deneme/prototip | `[SignalHandler]` |
| Sıra/paralellik/teklik politikası | `ExecutionMode` |

---

## Sonsöz — Felsefe

Strange size "her şeyi her şeye bağlayan bir Binder" verdi. Nexus aynı ruhu
taşır ama bir kural ekler: **bağlama disiplinlidir.** Her bağlantı ya açıkça
`OnConfigure`'da görünür ya da canonical dokümanda gerekçelendirilir. Bu,
Strange'in esnekliği ile üretim oyununun ihtiyaç duyduğu kontrol arasındaki
denge noktasıdır.

Kavramlar aynıysa da, Nexus'u farklı kılan şey kavramlar değil — **havuzlama,
struct sinyaller, async yaşam döngüsü ve ölçümlenmiş performans sözleşmesi**dir.
Bu rehber size *neden*ini verdi; kod, *nasıl*ını zaten söylüyor.
