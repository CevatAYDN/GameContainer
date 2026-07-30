# 🚀 Nexus 10 Dakikada Hızlı Başlangıç

> **Sıfırdan çalışan koda 10 dakikada ulaşın.**  
> Bu rehber, Nexus'u kurmanızı, ilk context'inizi oluşturmanızı, bir signal + command bağlamanızı, reactive model eklemenizi ve UI bağlantısı yapmanızı adım adım gösterir.

---

## ✅ Başlamadan Önce

- Unity **6000.0+** (Unity 6)
- Yeni veya mevcut bir Unity projesi (herhangi bir şablon)
- Temel C# ve Unity Editor bilgisi

---

## 📦 Adım 1: Nexus'u Kurun (1 dk)

1. **Window > Package Manager**'ı açın
2. **+** butonuna tıklayın → **Add package from disk...**
3. `Nexus/Packages/com.nexus.core/package.json` dosyasını seçin
4. **Open**'a tıklayın

> **Bu kadar.** Ek kurulum script'i, DLL kopyalama veya Bootstrapper prefab'ı gerekmez.

---

## 🧱 Adım 2: İlk Context'inizi Oluşturun (2 dk)

**Context**, Nexus'un merkezi kabıdır — modelleri, servisleri, command'leri ve signal bus'ı tutar.

### 2a. ContextData asset'i oluşturun

1. Project penceresinde sağ tıklayın → **Create > Nexus > ContextData**
2. Adını `GameContextData` olarak değiştirin
3. Üzerine tıklayın ve **Enable Auto-Discovery** seçeneğini işaretleyin
   - Bu, Nexus'a projenizi tarayıp lifecycle sınıflarını otomatik bulmasını söyler

### 2b. Root bileşenini ekleyin

1. Sahnenize boş bir **GameObject** oluşturun → adını `GameRoot` koyun
2. `Root` bileşenini ekleyin (**Add Component → Nexus → Root**)
3. `GameContextData` asset'ini Root bileşenindeki **Context Data** alanına sürükleyin

> ✅ **Tamam.** Çalışma zamanında `Root` bileşeni:
> 1. Context'inizi oluşturacak
> 2. `IContextLifecycle` sınıflarını tarayacak
> 3. Sırasıyla `OnConfigure` → `OnInitializeAsync` → `OnStartAsync` çağıracak

---

## ⚡ Adım 3: Signal + Command Ekleyin (2 dk)

Signal'ler değişmez (immutable) struct'lardır. Command'ler onları işler.

### 3a. Signal'i oluşturun

```csharp
// Signals/ScoreSignal.cs
public readonly struct ScoreSignal
{
    public readonly int Points;
    public ScoreSignal(int points) => Points = points;
}
```

### 3b. Command'i oluşturun

```csharp
// Commands/AddScoreCommand.cs
using Nexus.Core;

public class AddScoreCommand : ICommand<ScoreSignal>
{
    [Inject] private ScoreModel _score; // Bunu sonra oluşturacağız

    public void Execute(ScoreSignal signal)
    {
        _score.Total.Value += signal.Points;
    }
}
```

### 3c. Lifecycle (bağlantı) sınıfını oluşturun

```csharp
// Lifecycle/GameLifecycle.cs
using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;

public class GameLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindReactiveModel<ScoreModel>();
        builder.BindSignal<ScoreSignal>().To<AddScoreCommand>();
    }

    public ValueTask OnInitializeAsync(CancellationToken ct) => default;
    public ValueTask OnStartAsync(CancellationToken ct) => default;
    public void OnDispose() { }
}
```

> **Auto-Discovery** sayesinde `GameLifecycle` otomatik bulunur — manuel kayıt gerekmez!

---

## 📊 Adım 4: Reactive Model Ekleyin (1 dk)

Modeller gözlemlenebilir (observable) durum tutar. Değişiklikler abonelere otomatik bildirilir.

```csharp
// Models/ScoreModel.cs
using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;

public class ScoreModel : IReactiveModel
{
    public ObservableProperty<int> Total { get; } = new(0);
    public ObservableProperty<string> Rank { get; } = new("Bronze");

    public ValueTask OnBind(CancellationToken ct) => default;
}
```

> `ObservableProperty<T>` sıfır-allocation'dır — property değişikliklerinden GC baskısı oluşmaz.

---

## 🖥️ Adım 5: UI Bağlantısı Kurun (2 dk)

### 5a. Mediator + View oluşturun

```csharp
// UI/ScoreView.cs
using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

public class ScoreView : View
{
    public Text scoreText;
    public Button addButton;

    protected override void OnBind()
    {
        var model = Context.Resolve<ScoreModel>();
        model.Total.OnChanged((oldVal, newVal) =>
        {
            scoreText.text = $"Skor: {newVal}";
        });

        addButton.onClick.AddListener(() =>
        {
            Context.SignalBus.Fire(new ScoreSignal(10));
        });
    }
}
```

### 5b. Sahneye yerleştirin

1. Bir **Canvas** ekleyin, içine bir **Text** ve bir **Button** koyun
2. Bir GameObject'e `ScoreView` bileşenini ekleyin
3. Inspector'da Text ve Button referanslarını atayın

---

## ▶️ Adım 6: Çalıştırın! (1 dk)

1. **Play** tuşuna basın ☝️
2. Butona tıklayın — skor yazısı güncellenir
3. **Window > Nexus > Dashboard**'u açarak canlı inceleme yapın:
   - Anlık signal ve command akışı
   - Reactive model durumu
   - Causal trace log'u

> **Tebrikler!** Tamamen reactive bir Nexus uygulaması oluşturdunuz.

---

## 📚 Sırada Ne Var?

| Konu | Rehber |
|------|--------|
| 🏗️ Mimari derinlemesine | [ARCHITECTURE.md](ARCHITECTURE.md) |
| 🎮 Oyun pattern'leri (Idle, RPG, RTS vb.) | [GAME_PATTERNS.md](GAME_PATTERNS.md) |
| 🔌 4 command yürütme modu | [Counter Sample](../Samples~/Counter/README.md) |
| 🛠️ Editor eklentileri ve araçlar | [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) |
| 🔍 Yerelleştirme sistemi | [LOCALIZATION_KEYS.md](LOCALIZATION_KEYS.md) |
| 🤝 Katkıda bulunma | [CONTRIBUTING.md](CONTRIBUTING.md) |

---

## 🆘 Hızlı Sorun Giderme

| Belirti | Çözüm |
|---------|-------|
| **"No lifecycle found"** | Lifecycle sınıfınız `IContextLifecycle` implemente etmeli ve **ContextData'da Auto-Discovery açık olmalı** |
| **"Type not registered"** | `OnConfigure`'a `builder.Bind<MyType>()` ekleyin veya türün sistem dışı bir assembly'de olduğunu kontrol edin |
| **Signal gönderilmiyor** | Command'in `ICommand<YourSignal>` implemente ettiğini ve `builder.BindSignal<>()` ile kayıtlı olduğunu doğrulayın |
| **Test çalışmıyor** | EditMode Test Runner'ı açın, `InfrastructureValidationTests` ve `PluginRefactorValidationTests`'i seçin |

---

> **Son güncelleme:** 2026-07-30  
> **Nexus sürümü:** 0.4.0
