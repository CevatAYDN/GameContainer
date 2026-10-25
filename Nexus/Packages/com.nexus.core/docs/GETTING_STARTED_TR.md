> **Kimin için:** seviye 2, var olan bir oyuna Nexus ekleyen geliştirici. Üç yol: tek bileşen (code-first), sahne kökü veya örnek. Zenject'ten geliyorsanız bundan sonra `ZENJECT_MIGRATION_TR.md`'yi okuyun.

# Var olan bir Unity 6 projesine Nexus ekleme

Nexus bir UPM paketidir; deponun demo projesi gerekmez. Bir yol seçin.

## Kurulum

**Window → Package Manager → + → Install package from git URL** içinde şunu kullanın:

```text
https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core#<commit>
```

Yerel bir checkout için **Install package from disk** ile
`Nexus/Packages/com.nexus.core/package.json` dosyasını seçin. Üretimde git bağımlılığını sabitlerken test edilmiş
bir sürüm etiketi veya commit (`#<revision>`) kullanın. Unity, paketin uGUI, profiling ve Input System bağımlılıklarını çözer.
Yerleşik klavye/gamepad desteği için Player Settings → Active Input Handling ayarını
Input System Package (New) veya Both yapın. Özel bir IInputProvider replay, UI
veya sanal kontroller sağlayabilir; onun nötr girişi belirleyicidir.
Bu paket Unity 6'yı hedefler; bu rehber eski Unity sürümleri için söz vermez.

## Yeni başlayan: örneği çalıştırın

Package Manager'da Nexus'u seçin, **Samples** bölümünü açın ve **Counter Example**'ı içe aktarın.
Örneğin README'sini izleyin. Örnek, MVCS turunun tamamını içerir; deponun `Assets` klasöründeki hiçbir
dosya ön koşul değildir. Üretilmiş bir sahne için Nexus Dashboard'daki Setup Wizard'ı (Kurulum Sihirbazı)
açın. Mevcut başlangıç dosyalarını, ayarları ve sahneleri korur; yeni sahneyi mevcutların yanına ekleyerek oluşturur.

## Code-first: tek başlatma çağrısı

Package Manager'dan **Code-first Startup**'ı içe aktarın, `ScoreBootstrap`'ı boş bir
GameObject'e ekleyin ve Play Mode'a girin. Script'in tamamı budur; Unity'nin bileşeni
ekleyebilmesi için dosya adını `ScoreBootstrap.cs` olarak koruyun.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples.CodeFirst
{
    public readonly struct ScoreAdded
    {
        public readonly int Amount;
        public ScoreAdded(int amount) => Amount = amount;
    }

    public sealed class ScoreModel : IReactiveModel
    {
        public ObservableProperty<int> Score { get; } = new(0);
        public ValueTask OnBind(CancellationToken ct) => default;
    }

    public sealed class AddScore : ICommand<ScoreAdded>
    {
        private readonly ScoreModel _model;
        public AddScore(ScoreModel model) => _model = model;
        public void Execute(ScoreAdded signal) => _model.Score.Value += signal.Amount;
    }

    /// <summary>Attach to an empty GameObject. No Root, scene asset or assembly scanning needed.</summary>
    public sealed class ScoreBootstrap : MonoBehaviour
    {
        private readonly CancellationTokenSource _startup = new();
        private Context _context;

        private async void Start()
        {
            try
            {
                _context = await ContextFactory.StartAsync("Gameplay", builder =>
                {
                    builder.BindReactiveModel<ScoreModel>();
                    builder.BindCommand<ScoreAdded, AddScore>();
                }, ct: _startup.Token);
                _context.Prewarm<ScoreAdded>(4);
                _context.SignalBus.Fire(new ScoreAdded(1));
                Debug.Log($"Nexus score: {_context.Resolve<ScoreModel>().Score.Value}");
            }
            catch (OperationCanceledException) { } // Owner destroyed during startup.
            catch (Exception error) { Debug.LogException(error); }
        }

        private void OnDestroy()
        {
            try { _startup.Cancel(); }
            finally
            {
                try { _context?.Dispose(); }
                finally { _startup.Dispose(); }
            }
        }
    }
}

```

`StartAsync` yapılandırmayı doğrulamadan önce çalıştırır ve dönmeden önce model, servis ve
lifecycle başlatmasını tamamlar. Başlatma başarısız olursa temizlik yapılır; iptal `ct:` üzerinden
kabul edilir. Unity işleri Unity ana thread'inde başlamalıdır. Context'i sahibi olan bir field'da
tutun ve o sahip sona erdiğinde context'i dispose edin.

## Sahne öncelikli ve gelişmiş projeler

Sahne sahipliği, parent/child context'ler, auto-discovery, mediator'lar ve editör yapılandırması için
bir Root + ContextData asset'i ve bir IContextLifecycle kullanın. [English quickstart](10_MIN_QUICKSTART.md)
veya [Türkçe hızlı başlangıç](10_MIN_QUICKSTART_TR.md) rehberini izleyin. Yapılandırmayı ilgisiz
MonoBehaviour update'lerinde binding yapmak yerine lifecycle'da tutun. Oyun ihtiyaç duyduğunda gerçek
analytics/reklam/IAP/backend adaptörlerini bağlayın; stub servisler entegrasyon noktalarıdır.

## Performansa duyarlı composite'ler

```csharp
public readonly struct MapReady { }
public readonly struct PlayerReady { }
public sealed class BeginPlay : ICompositeCommand<MapReady, PlayerReady>
{
    public void Execute(MapReady map, PlayerReady player) { /* start gameplay */ }
}

var registration = context.SignalBus.BindComposite<MapReady, PlayerReady, BeginPlay>();
context.SignalBus.Fire(new MapReady());
context.SignalBus.Fire(new PlayerReady());
// Dispose registration to stop it, or let the owning context dispose its subscriptions.
```

İki farklı sinyal tipinin ikisi de gelmelidir. Tekrarlanan değerler o tipin bekleyen değerinin yerini alır;
tamamlanan çiftler sıfırlanır ve `oneShot: true` ilk başarılı tamamlanmadan sonra durur. Başarısız bir çift, one-shot hakkını sonraki bir çift için serbest bırakır. Bu senkron yol
tipli değerler ve havuzlanmış komutlar kullanır; teslim sırasını kayıt sırası belirler. Değişmez (immutable)
saklanan snapshot'lar gerektiğinde, keyfi sayıda sinyalli veya async composite'ler için eski CompositeContext'i
kullanın. Debug tracing/decorator'ların kendi maliyeti vardır. Bir frame bütçesi belirlemeden önce hedef
cihazda doğrulayın; host'taki 0-GC sonuçları IL2CPP ölçümü değildir.
