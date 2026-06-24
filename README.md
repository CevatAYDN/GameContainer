# Nexus: Observable Architecture

[![Language](https://img.shields.io/badge/language-English%20%7C%20T%C3%BCrk%C3%A7e-blue)](#-english-version)

[English Version](#-english-version) | [Türkçe Versiyonu](#-türkçe-versiyonu)

---

## 🌐 English Version

Nexus is an open-source MVCS architecture framework designed for Unity, focusing on **0 GC allocation (steady-state)** and high observability.

### 📋 System Requirements & Compatibility

Nexus utilizes modern C# and Unity editor/runtime capabilities. This project is currently targeted and validated on Unity 6:

* **Unity 6 (6000.x versions)**

#### Requirements:
* Support for **C# 9.0+ and .NET Standard 2.1**.
* **UI Toolkit (UIElements)** (required for editor tools to function).
* **Unity 2023.x and older versions are not officially supported**. The package metadata uses `unity: 6000.0`.

### 🚀 Core Promise
> **"See why your system is not working in Unity in 10 seconds."**

Nexus is designed to solve debuggability and observability issues common in traditional DI and event-driven libraries. It offers live signal chain tracking, causal chain analysis, and compile-time/editor-time architectural validation tools.

---

### ✨ Key Features

1. **Progressive Disclosure:** Start working in seconds by defining just a signal (`struct`) and a command (`ICommand`) without complex configurations.
2. **0 GC Allocation (Steady-State):** Zero garbage generation at runtime thanks to strongly-typed generic delegates and automatic command pooling (`CommandPool`).
3. **4 Command Execution Modes:**
   - **Sequential (Default):** Deterministic sequential execution based on priority.
   - **Concurrent:** Parallel asynchronous I/O and loading operations.
   - **Exclusive:** Single handler guarantee.
   - **Composite Trigger:** Orchestration commands that wait for multiple signals (fan-in) to accumulate.
4. **Causal Tracing:** Zero-allocation causality tracking that shows which signal triggered which command, and which sub-signals were dispatched by that command.
5. **Architectural Validation (Build Validation):** Catches priority conflicts, mixed-mode violations, and side effects in concurrent commands before compiling.
6. **Error Recovery:** Customizable recovery strategies offering Retry, Fallback, and Abort logic.

---

### 📦 Installation (UPM)

Nexus is a standalone **Unity Package Manager (UPM)** package. To integrate it into your project:

1. Open your project's `Packages/manifest.json` file.
2. Add the Git URL or local disk path of the package to the `dependencies` block:

```json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/<username>/<repo>.git?path=Nexus/Packages/com.nexus.core",
    ...
  }
}
```

---

### ⚡ Quick Start

This guide walks you through a complete MVCS cycle step-by-step, starting from setting up a context in the scene, data binding, business logic execution (commands), and view/mediator layers.

#### Step 1: Setting up the Context in the Scene
1. Create an empty GameObject in the scene or select `GameObject -> Nexus -> Create Root` from the Unity menu.
2. This action will create an object named `{ContextName}Root` in the scene and save the configuration file at `Assets/Settings/{ContextName}ContextData.asset`.
3. Set the `ScopeTag` value of the object to `Gameplay`.

---

#### Step 2: Defining the Model and Signal
Define your data model (State) and the immutable signal that will trigger the data flow:

```csharp
// 1. Signal (Immutable Struct)
public readonly struct DamageSignal
{
    public readonly int Amount;
    public DamageSignal(int amount) => Amount = amount;
}

// 2. Model Interface & Class
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

#### Step 3: Binding Dependencies (Lifecycle)
Write a lifecycle class to bind your models and services to the Nexus DI container. If your `ScopeTag` is `Gameplay`, you can name your class `{ScopeTag}Lifecycle` to leverage the auto-discovery feature:

```csharp
using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    // Nexus automatically discovers and runs this class for the Gameplay context.
    // There is no need to manually drag it into the scene.
    public class GameplayLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            // Bind the model dependency as a Singleton
            builder.BindModel<IPlayerModel, PlayerModel>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}
```

---

#### Step 4: Business Logic and Command Mapping
Create the business logic that runs when the signal is dispatched. You can choose between two styles:

##### Style A: Performant Generic Command (Recommended for 0-GC & AOT)
Implement the `ICommand<TSignal>` interface and bind it explicitly in your Lifecycle class (see Step 3):

```csharp
using Nexus.Core;
using UnityEngine;

public class DamageCommand : ICommand<DamageSignal>
{
    [Inject] private IPlayerModel _playerModel; // Model dependency

    public void Execute(DamageSignal signal)
    {
        _playerModel.Health -= signal.Amount;
        Debug.Log($"Damage processed: {signal.Amount}. New Health: {_playerModel.Health}");
    }
}
```

Add this binding to your Lifecycle class:
```csharp
builder.BindCommand<DamageSignal, DamageCommand>();
```

##### Style B: Auto-Discovered Command (Reflection Fallback)
Implement the non-generic `ICommand` interface and decorate the class with the `[SignalHandler]` attribute. Nexus will automatically discover and register it:

```csharp
using Nexus.Core;
using UnityEngine;

[SignalHandler(typeof(DamageSignal))] // Auto-discovered and registered by Nexus
public class DamageCommand : ICommand
{
    // The signal payload is injected into a field matching the type or named _signal
    private readonly DamageSignal _signal; 
    
    [Inject] private IPlayerModel _playerModel; // Model dependency

    public void Execute()
    {
        _playerModel.Health -= _signal.Amount;
        Debug.Log($"Damage processed: {_signal.Amount}. New Health: {_playerModel.Health}");
    }
}
```
*(Note: Style B uses reflection to set the signal field, which introduces a minor performance overhead and prints a warning on AOT platforms like WebGL/consoles).*

---

#### Step 5: View & Mediator Connection
Use the `View` and `Mediator` pair to completely isolate your views (UI/MonoBehaviour) from business logic and models:

```csharp
using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

// 1. View (Scene Component)
[Mediator(typeof(PlayerMediator))] // When this view is opened, PlayerMediator is automatically bound
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
        // Initialize the UI with the current health value
        View.UpdateHealthUI(_playerModel.Health);

        // Listen for the event triggered when DamageSignal is dispatched
        Subscribe<DamageSignal>(OnDamageReceived);
    }

    private void OnDamageReceived(DamageSignal signal)
    {
        // Refresh the UI after the model updates
        View.UpdateHealthUI(_playerModel.Health);
    }
}
```

---

#### Step 6: Dispatch the Signal (Execution)
You can dispatch your signal and trigger the cycle from different contexts:

##### 1. Inside a Mediator
Since the `Mediator` base class provides a built-in `SignalBus` instance property, you can call it directly:
```csharp
SignalBus.Fire(new DamageSignal(10));
```

##### 2. Inside a Dependency-Injected Class (Lifecycle, Services, etc.)
Inject the `ISignalBus` interface via property or field injection:
```csharp
[Inject] private ISignalBus _signalBus;

public void DealDamage()
{
    _signalBus.Fire(new DamageSignal(10));
}
```

##### 3. From a Standard MonoBehaviour (e.g. Collision trigger in a scene)
Find the nearest parent Context Root component to access the active SignalBus:
```csharp
public class DamageTrigger : MonoBehaviour
{
    private void OnCollisionEnter(Collision collision)
    {
        var root = GetComponentInParent<Root>();
        if (root != null && root.Context != null)
        {
            root.Context.SignalBus.Fire(new DamageSignal(10));
        }
    }
}
```

---

### 🛠️ Tools
- **Root Wizard (`GameObject -> Nexus -> Create Root`):** Prepares the Nexus Root GameObject and ContextData configuration in the scene in 30 seconds.
- **Architectural Validator (`Nexus -> Validate Architecture`):** Verifies the project's compliance with architectural rules.

---

### 📄 License
This project is licensed under the MIT License.

---
---

## 🇹🇷 Türkçe Versiyonu

Nexus, Unity için tasarlanmış, **0 GC allocation (steady-state)** hedefleriyle çalışan, yüksek düzeyde izlenebilir (observable) açık kaynaklı bir MVCS mimari çatısıdır.

### 📋 Sistem Gereksinimleri ve Uyumluluk

Nexus, modern C# ve Unity editör/runtime yeteneklerini kullanır. Bu proje şu anda Unity 6 üzerinde hedeflenir ve doğrulanır:

* **Unity 6 (6000.x sürümleri)**

#### Gereksinimler:
* **C# 9.0+ ve .NET Standard 2.1** desteği.
* **UI Toolkit (UIElements)** (Editör araçlarının çalışabilmesi için gereklidir).
* **Unity 2023.x ve daha eski sürümler desteklenmiş hedef olarak ilan edilmemektedir**. Paket metadata'sı `unity: 6000.0` değerini kullanır.

### 🚀 Ana Vaat
> **"Unity'de sisteminizin neden çalışmadığını 10 saniyede görün."**

Nexus, geleneksel DI ve Event-Driven kütüphanelerindeki izlenebilirlik (debuggability) sorunlarını çözmek amacıyla tasarlanmıştır. Canlı sinyal zinciri takibi, neden-sonuç (causal chain) analizleri ve derleme zamanı (compile-time/editor-time) mimari doğrulama araçları sunar.

---

### ✨ Öne Çıkan Özellikler

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

### 📦 Kurulum (UPM)

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

### ⚡ Hızlı Başlangıç

Bu rehber, sahnede bağlam (context) kurmaktan başlayıp veri bağlama, iş mantığı (command) yürütme ve görünüm (view/mediator) katmanlarına kadar tam bir MVCS döngüsünü adım adım gösterir.

#### Adım 1: Sahnede Bağlam (Context) Kurulumu
1. Sahnede boş bir GameObject oluşturun veya Unity menüsünden `GameObject -> Nexus -> Create Root` yolunu seçin.
2. Bu işlem sahnede `{ContextName}Root` adında bir nesne oluşturacak ve `Assets/Settings/{ContextName}ContextData.asset` yapılandırma dosyasını kaydedecektir.
3. Nesnenin `ScopeTag` değerini `Gameplay` yapın.

---

#### Adım 2: Model ve Sinyal Tanımlama
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

#### Adım 3: Bağımlılıkların Bağlanması (Lifecycle)
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

#### Adım 4: İş Mantığı ve Komut Eşleme (Command)
Sinyal fırlatıldığında çalışacak iş mantığını iki farklı yöntemle tanımlayabilirsiniz:

##### Yöntem A: Yüksek Performanslı Generic Komut (0-GC & AOT için Önerilen)
`ICommand<TSignal>` arayüzünü uygulayın ve komutu Lifecycle sınıfınızda el ile bağlayın (bkz. Adım 3):

```csharp
using Nexus.Core;
using UnityEngine;

public class DamageCommand : ICommand<DamageSignal>
{
    [Inject] private IPlayerModel _playerModel; // Model bağımlılığı

    public void Execute(DamageSignal signal)
    {
        _playerModel.Health -= signal.Amount;
        Debug.Log($"Damage processed: {signal.Amount}. New Health: {_playerModel.Health}");
    }
}
```

Bu bağlantıyı Lifecycle sınıfınızda yapılandırın:
```csharp
builder.BindCommand<DamageSignal, DamageCommand>();
```

##### Yöntem B: Otomatik Keşfedilen Komut (Yansıma / Reflection Yöntemi)
Sınıfı `[SignalHandler]` özniteliği ile işaretleyin ve generic olmayan `ICommand` arayüzünü uygulayın. Nexus bu komutu otomatik olarak tarayıp kaydedecektir:

```csharp
using Nexus.Core;
using UnityEngine;

[SignalHandler(typeof(DamageSignal))] // Nexus tarafından otomatik olarak taranıp kaydedilir
public class DamageCommand : ICommand
{
    // Tetiklenen sinyal içeriği Nexus tarafından yansıma yoluyla otomatik enjekte edilir
    private readonly DamageSignal _signal; 
    
    [Inject] private IPlayerModel _playerModel; // Model bağımlılığı

    public void Execute()
    {
        _playerModel.Health -= _signal.Amount;
        Debug.Log($"Damage processed: {_signal.Amount}. New Health: {_playerModel.Health}");
    }
}
```
*(Not: Yöntem B, sinyal değerini enjekte etmek için reflection kullandığından AOT platformlarında (WebGL, Konsollar vb.) hafif bir performans uyarısı verir).*

---

#### Adım 5: Görünüm ve Mediator Bağlantısı (View & Mediator)
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

#### Adım 6: Sinyali Fırlatın (Execution)
Sinyalinizi projenin farklı katmanlarından fırlatabilirsiniz:

##### 1. Bir Mediator İçinden
`Mediator` ana sınıfı doğrudan bir `SignalBus` özelliğine (property) sahip olduğu için doğrudan çağrı yapabilirsiniz:
```csharp
SignalBus.Fire(new DamageSignal(10));
```

##### 2. Dependency Injection Alan Bir Sınıf İçinden (Servisler, Lifecycle vb.)
`ISignalBus` arayüzünü enjekte edin:
```csharp
[Inject] private ISignalBus _signalBus;

public void DealDamage()
{
    _signalBus.Fire(new DamageSignal(10));
}
```

##### 3. Standart Bir MonoBehaviour İçinden (Örn: Sahnede Çarpışma Tetikleyicisi)
En yakın ebeveyn Context Root nesnesini bularak sinyali gönderin:
```csharp
public class DamageTrigger : MonoBehaviour
{
    private void OnCollisionEnter(Collision collision)
    {
        var root = GetComponentInParent<Root>();
        if (root != null && root.Context != null)
        {
            root.Context.SignalBus.Fire(new DamageSignal(10));
        }
    }
}
```

---

### 🛠️ Araçlar
- **Root Wizard (`GameObject -> Nexus -> Create Root`):** 30 saniyede Nexus Root GameObject ve ContextData yapılandırmasını sahnede hazırlar.
- **Architectural Validator (`Nexus -> Validate Architecture`):** Projenin mimari kurallara uygunluğunu kontrol eder.

---

### 📄 Lisans
Bu proje MIT lisansı ile lisanslanmıştır.
