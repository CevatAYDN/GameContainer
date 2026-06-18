# Nexus: Observable Architecture

Nexus, Unity için tasarlanmış, **0 GC allocation (steady-state)** hedefleriyle çalışan, yüksek düzeyde izlenebilir (observable) açık kaynaklı bir MVCS mimari çatısıdır.

## 📋 Sistem Gereksinimleri ve Uyumluluk

Nexus, modern C# ve Unity editör yeteneklerini kullanır. Aşağıdaki Unity sürümleriyle tam uyumlu olarak çalışmaktadır:

* **Unity 6 (ve tüm 6000.x sürümleri)**
* **Unity 2023.x**
* **Unity 2022.3 LTS**
* **Unity 2021.3 LTS**

### Gereksinimler:
* **C# 9.0+ ve .NET Standard 2.1** desteği (Unity 2021.3 LTS ve sonrasında varsayılan olarak desteklenmektedir).
* **UI Toolkit (UIElements)** (Editör araçlarının çalışabilmesi için gereklidir).
* **Unity 5 ve daha eski (Legacy) sürümler desteklenmemektedir** (eski .NET sürüm kısıtlamaları ve UI Toolkit eksikliği nedeniyle).

## 🚀 Ana Vaat
> **"Unity'de sisteminizin neden çalışmadığını 10 saniyede görün."**

Nexus, geleneksel DI ve Event-Driven kütüphanelerindeki izlenebilirlik (debuggability) sorunlarını çözmek amacıyla tasarlanmıştır. Canlı sinyal zinciri takibi, neden-sonuç (causal chain) analizleri ve derleme zamanı (compile-time/editor-time) mimari doğrulama araçları sunar.

---

## ✨ Öne Çıkan Özellikler

1. **Kademeli Keşif (Progressive Disclosure):** Karmaşık konfigürasyonlara ihtiyaç duymadan, yalnızca bir sinyal (`struct`) ve bir komut (`ICommand`) tanımlayarak saniyeler içinde çalışmaya başlayın.
2. **0 GC Allocation (Steady-State):** Güçlü tipli generic delegeler ve otomatik komut havuzlama (`CommandPool`) sayesinde runtime'da sıfır çöp üretimi.
3. **4 Farklı Komut Çalıştırma Modu:**
   - **Sequential (Varsayılan):** Önceliğe göre deterministik sıralı çalıştırma.
   - **Concurrent:** Parallel asenkron I/O ve yükleme işlemleri.
   - **Exclusive:** Tekil handler garantisi.
   - **Composite Trigger:** Çoklu sinyalin (fan-in) birikmesini bekleyen orkestrasyon komutları.
4. **Causal Tracing (Neden-Sonuç Zinciri):** Hangi sinyalin hangi komutu tetiklediğini ve o komutun hangi alt sinyalleri fırlattığını gösteren 0-alloc nedensellik takibi.
5. **Mimarî Doğrulama (Build Validation):** Öncelik çakışmalarını, mixed-mode ihlallerini, concurrent komutlardaki yan etkileri derleme öncesinde yakalar.
6. **Hata Kurtarma (Error Recovery):** Retry, Fallback ve Abort mantıkları sunan özelleştirilebilir kurtarma stratejileri.

---

## 📦 Kurulum (UPM)

Nexus, bağımsız bir **Unity Package Manager (UPM)** paketidir. Projenize entegre etmek için:

1. Projenizin `Packages/manifest.json` dosyasını açın.
2. `dependencies` bloğuna paketin Git URL'ini veya local disk path'ini ekleyin:

```json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/<username>/<repo>.git?path=Nexus/Packages/com.nexus.core",
    ...
  }
}
```

---

## ⚡ Hızlı Başlangıç

Bu rehber, sahnede bağlam (context) kurmaktan başlayıp veri bağlama, iş mantığı (command) yürütme ve görünüm (view/mediator) katmanlarına kadar tam bir MVCS döngüsünü adım adım gösterir.

### Adım 1: Sahnede Bağlam (Context) Kurulumu
1. Sahnede boş bir GameObject oluşturun veya Unity menüsünden `GameObject -> Nexus -> Create Root` yolunu seçin.
2. Bu işlem sahnede `{ContextName}Root` adında bir nesne oluşturacak ve `Assets/Settings/{ContextName}ContextData.asset` yapılandırma dosyasını kaydedecektir.
3. Nesnenin `ScopeTag` değerini `Gameplay` yapın.

---

### Adım 2: Model ve Sinyal Tanımlama
Veri modelinizi (State) ve veri akışını tetikleyecek sınırlandırılmış (immutable) sinyali tanımlayın:

```csharp
// 1. Sinyal (Immutable Struct)
public readonly struct DamageSignal
{
    public readonly int Amount;
    public DamageSignal(int amount) => Amount = amount;
}

// 2. Model Arayüzü ve Sınıfı
public interface IPlayerModel
{
    int Health { get; set; }
}

public class PlayerModel : IPlayerModel
{
    public int Health { get; set; } = 100;
}
```

---

### Adım 3: Bağımlılıkların Bağlanması (Lifecycle)
Oluşturduğunuz model ve servisleri Nexus DI konteynerına bağlamak için bir lifecycle sınıfı yazın. `ScopeTag` değeriniz `Gameplay` ise, sınıf adını `{ScopeTag}Lifecycle` yaparak otomatik keşif (auto-discovery) özelliğinden yararlanabilirsiniz:

```csharp
using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    // Nexus bu sınıfı Gameplay context'i için otomatik olarak bulur ve çalıştırır.
    // Sahneye el ile sürükleyip eklemenize gerek yoktur.
    public class GameplayLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            // Model bağımlılığını Singleton olarak bağlayın
            builder.BindModel<IPlayerModel, PlayerModel>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}
```

---

### Adım 4: İş Mantığı ve Komut Eşleme (Command)
Sinyal fırlatıldığında çalışacak iş mantığını `ICommand` veya `IAsyncCommand` kullanarak oluşturun:

```csharp
using Nexus.Core;
using UnityEngine;

[SignalHandler(typeof(DamageSignal))] // DamageSignal fırlatıldığında bu komut çalışır
public class DamageCommand : ICommand
{
    // Tetiklenen sinyal içeriği Nexus tarafından otomatik enjekte edilir
    private readonly DamageSignal _signal; 
    
    [Inject] private IPlayerModel _playerModel; // Model bağımlılığı

    public void Execute()
    {
        _playerModel.Health -= _signal.Amount;
        Debug.Log($"Damage processed: {_signal.Amount}. New Health: {_playerModel.Health}");
    }
}
```

---

### Adım 5: Görünüm ve Mediator Bağlantısı (View & Mediator)
Görünümleri (UI/MonoBehaviour) iş mantığından ve modellerden tamamen izole etmek için `View` ve `Mediator` çiftini kullanın:

```csharp
using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

// 1. Görünüm (Scene Component)
[Mediator(typeof(PlayerMediator))] // Bu view açıldığında PlayerMediator otomatik bağlanır
public class PlayerView : View
{
    [SerializeField] private Text healthText;

    public void UpdateHealthUI(int currentHealth)
    {
        healthText.text = $"Health: {currentHealth}";
    }
}

// 2. Mediator (UI Controller)
public class PlayerMediator : Mediator<PlayerView>
{
    [Inject] private IPlayerModel _playerModel;

    protected override void OnBind()
    {
        // UI'ı mevcut can değeriyle ilklendir
        View.UpdateHealthUI(_playerModel.Health);

        // DamageSignal fırlatıldığında tetiklenecek eventi dinle
        Subscribe<DamageSignal>(OnDamageReceived);
    }

    private void OnDamageReceived(DamageSignal signal)
    {
        // Model güncellendikten sonra UI'ı tazele
        View.UpdateHealthUI(_playerModel.Health);
    }
}
```

---

### Adım 6: Sinyali Fırlatın (Execution)
Artık herhangi bir MonoBehaviour içinden, Mediator'dan veya servis sınıfından sinyalinizi fırlatıp tüm döngüyü tetikleyebilirsiniz:

```csharp
// Sinyali fırlatın (Fire-and-forget fix ile asenkron/senkron sıralı yürütülür)
SignalBus.Fire(new DamageSignal(10));
```

---

---

## 🛠️ Araçlar
- **Root Wizard (`GameObject -> Nexus -> Create Root`):** 30 saniyede Nexus Root GameObject ve ContextData yapılandırmasını sahnede hazırlar.
- **Architectural Validator (`Nexus -> Validate Architecture`):** Projenin mimari kurallara uygunluğunu kontrol eder.

---

## 📄 Lisans
Bu proje MIT lisansı ile lisanslanmıştır.
