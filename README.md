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
7. **AES-256 Anti-Cheat & Device-Bound Encryption:** Tamper-proof encrypted local saves (`EncryptedStorageService`) with HMAC-SHA256 integrity checks, hardware-bound seeds, and RAM memory obfuscation (`SecureObservableInt`) against memory scanners (GameGuardian/Cheat Engine).
8. **13 Production-Ready Core Services:** Out-of-the-box engine suite tailored for Casual & Hybrid-Casual games (UI Window Stack, Audio, Haptics, Combined Juice Feedback, Object Pooling, FSM, Multi-Currency Economy, Level Progression, Mediation Ads, IAP Sandbox, Dynamic Localization, Analytics, and Encrypted Storage).
9. **Live Play-Mode Dashboard & Custom Inspectors:** Full control via custom Inspector badges and `NexusWindow` Live Debugger for real-time Economy, Progression, and TimeScale adjustments.

---

### 📦 13 Production-Ready Core Services Catalog

| # | Service Name | Implementation | Description |
| :-: | :--- | :--- | :--- |
| **1** | **`EncryptedStorageService`** | `Nexus.Core.Services.EncryptedStorageService` | AES-256 encrypted, HMAC-SHA256 tamper-proof, device-bound local save storage. |
| **2** | **`ObjectPoolService`** | `Nexus.Core.Services.ObjectPoolService` | Universal GameObject/Component pool with prewarming, `IPoolable` callbacks, and timed auto-despawn. |
| **3** | **`WindowManager`** | `Nexus.Core.Services.WindowManager` | Multi-layered UI canvas stack (`HUD`, `Screen`, `Popup`, `Modal`), async loading, back-button history navigation. |
| **4** | **`AudioService`** | `Nexus.Core.Services.AudioService` | BGM playlist crossfading, 2D/3D SFX AudioSource pool, random pitch variation, volume channels. |
| **5** | **`HapticService`** | `Nexus.Core.Services.HapticService` | Zero-alloc Android JNI handle caching, iOS Taptic Engine integration, desktop preview. |
| **6** | **`FeedbackService`** | `Nexus.Core.Services.FeedbackService` | Orchestrates combined Audio + Haptics ("Juice") presets (`CoinCollect`, `SuccessFanfare`, `Impact`). |
| **7** | **`AdService`** | `Nexus.Core.Services.AdService` | Mediation provider adapter pattern (AppLovin MAX/LevelPlay), interstitial cooldown, rewarded callbacks, ILRD. |
| **8** | **`IapService`** | `Nexus.Core.Services.IapService` | Store catalog definition, purchase & restore flows, editor test sandbox. |
| **9** | **`EconomyService`** | `Nexus.Core.Services.EconomyService` | Multi-currency transaction engine (`Coins`, `Gems`, `Energy`), `CanAfford`, reactive balance properties. |
| **10** | **`ProgressionService`** | `Nexus.Core.Services.ProgressionService` | Level index tracking, max unlocked levels, linear/exponential/polynomial upgrade cost formulas. |
| **11** | **`TickService`** | `Nexus.Core.Services.TickService` | Central update loop driver (`ITickable`, `IFixedTickable`, `ILateTickable`), `TimeScale`, global pause. |
| **12** | **`LocalizationService`** | `Nexus.Core.Services.LocalizationService` | Dynamic language table registration, RTL formatting, fallback language support. |
| **13** | **`AnalyticsService`** | `Nexus.Core.Services.AnalyticsService` | Cross-platform event tracking abstraction, parameter dictionary pooling, user property management. |

---

### 📦 Installation (UPM)

Nexus is a standalone **Unity Package Manager (UPM)** package. To integrate it into your project:

> [!NOTE]
> **Repository vs. Package Naming**
> The root repository is named `GameContainer` because it serves as the parent container/monorepo for the game and architectural modules. However, the package is named `com.nexus.core` to ensure clean, decoupled modularity inside the Unity Package Manager.

1. Open your project's `Packages/manifest.json` file.
2. Add the Git URL or local disk path of the package to the `dependencies` block:

```json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/CevatAYDN/GameContainer.git?path=Nexus/Packages/com.nexus.core",
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
Create the business logic that runs when the signal is dispatched.

##### Style A: Performant Generic Command (Single Recommended Path)
Implement the `ICommand<TSignal>` or `IAsyncCommand<TSignal>` interface and bind it explicitly in your Lifecycle class (see Step 3). This approach is 0-GC and AOT/IL2CPP compatible:

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

##### Style B: Auto-Discovered Command (Automatic Registration)
You can still leverage the `[SignalHandler]` attribute for automatic discovery and registration, but the command **must** implement the generic `ICommand<TSignal>` or `IAsyncCommand<TSignal>` interface. The non-generic reflection-based fallback has been completely removed to enforce 0-GC AOT/IL2CPP compliance:

```csharp
using Nexus.Core;
using UnityEngine;

[SignalHandler(typeof(DamageSignal))] // Auto-discovered and registered by Nexus
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
*(Note: All commands registered under a signal must implement generic interfaces. Non-generic commands will fail compilation or trigger a build-time validation error).*

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
7. **AES-256 Anti-Cheat & Cihaza Özel Kriptolu Kayıt:** HMAC-SHA256 bütünsellik doğrulaması, donanım ID'li şifreleme anahtarları (`EncryptedStorageService`) ve RAM bellek tarayıcılarına (GameGuardian/Cheat Engine) karşı XOR bellek maskelemesi (`SecureObservableInt`).
8. **13 Prodüksiyon Seviyesinde Çekirdek Servis:** Casual & Hybrid-Casual mobil oyunlar için kutudan çıkan hazır motor servisleri (UI Katman Yığını, Ses, Titreşim, Kombine Feedback/Juice, Obje Havuzlama, FSM, Çoklu Para Birimli Ekonomi, Seviye İlerlemesi, Mediasyon Reklamları, IAP Sandbox, Dinamik Yerelleştirme, Analitik ve Şifreli Kayıt).
9. **Canlı Oyun-İçi Editör Panosu & Özel Müfettişler:** Özel Inspector rozetleri ve `NexusWindow` Canlı Hata Ayıklayıcı ile Play-Mode anında Ekonomi, Seviye ve Zaman Ölçeği (TimeScale) üzerinde tam hakimiyet.

---

### 📦 13 Prodüksiyon Seviyesinde Çekirdek Servis Kataloğu

| # | Servis Adı | Uygulama | Açıklama |
| :-: | :--- | :--- | :--- |
| **1** | **`EncryptedStorageService`** | `Nexus.Core.Services.EncryptedStorageService` | AES-256 şifreli, HMAC-SHA256 kurcalama korumalı ve cihaza özel donanım anahtarlı kayıt servisi. |
| **2** | **`ObjectPoolService`** | `Nexus.Core.Services.ObjectPoolService` | Prewarming, `IPoolable` ve süreli oto-despawn destekli evrensel GameObject/Component havuzu. |
| **3** | **`WindowManager`** | `Nexus.Core.Services.WindowManager` | Katmanlı UI yığını (`HUD`, `Screen`, `Popup`, `Modal`), asenkron yükleme ve mobil geri tuşu geçmişi. |
| **4** | **`AudioService`** | `Nexus.Core.Services.AudioService` | BGM playlist yumuşak geçişleri (crossfade), 2D/3D SFX audio havuzu ve pitch varyasyonu. |
| **5** | **`HapticService`** | `Nexus.Core.Services.HapticService` | Zero-alloc Android JNI handle cachi'li titreşim yönetimi, iOS Taptic Engine ve masaüstü önizleme. |
| **6** | **`FeedbackService`** | `Nexus.Core.Services.FeedbackService` | Oyun içi "Juice" hissi için Ses + Titreşim kombinasyon hazır ayarları (`CoinCollect`, `SuccessFanfare`, `Impact`). |
| **7** | **`AdService`** | `Nexus.Core.Services.AdService` | Mediasyon adaptör deseni (AppLovin MAX/LevelPlay), interstitial cooldown sayacı, rewarded callbacks ve ILRD. |
| **8** | **`IapService`** | `Nexus.Core.Services.IapService` | Mağaza ürün kataloğu, satın alım/restore akışları ve editör test sandbox'ı. |
| **9** | **`EconomyService`** | `Nexus.Core.Services.EconomyService` | Çoklu para birimi (`Coins`, `Gems`, `Energy`), `CanAfford`, reaktif bakiyeler ve otomatik kayıt. |
| **10** | **`ProgressionService`** | `Nexus.Core.Services.ProgressionService` | Seviye takibi, kilit açma ve Linear/Exponential/Polynomial yükseltme maliyet eğrileri. |
| **11** | **`TickService`** | `Nexus.Core.Services.TickService` | Güncelleme döngüsü sürücüsü (`ITickable`, `IFixedTickable`, `ILateTickable`), `TimeScale` ve global duraklatma. |
| **12** | **`LocalizationService`** | `Nexus.Core.Services.LocalizationService` | Dinamik dil tablosu kaydı, RTL Arapça desteği ve varsayılan dil fallback yapısı. |
| **13** | **`AnalyticsService`** | `Nexus.Core.Services.AnalyticsService` | Çapraz platform analitik olay takibi, parametre sözlüğü havuzlama ve kullanıcı özellikleri. |

---

### 📦 Kurulum (UPM)

Nexus, bağımsız bir **Unity Package Manager (UPM)** paketidir. Projenize entegre etmek için:

> [!NOTE]
> **Repository vs. Package Naming**
> Kök repository'nin adı `GameContainer` olmasının nedeni, mimarinin ana konteyner/ monorepo hedefleri olmasıdır. Ancak paket adı `com.nexus.core` olarak seçilmiştir; bu, Unity Package Manager içinde temiz, dekuple modülerlik sağlar.

1. Projenizin `Packages/manifest.json` dosyasını açın.
2. `dependencies` bloğuna paketin Git URL'ini veya local disk path'ini ekleyin:

```json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/CevatAYDN/GameContainer.git?path=Nexus/Packages/com.nexus.core",
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
Sinyal fırlatıldığında çalışacak iş mantığını tanımlayın.

##### Yöntem A: Yüksek Performanslı Generic Komut (Tek Önerilen Yol)
`ICommand<TSignal>` veya `IAsyncCommand<TSignal>` arayüzünü uygulayın ve komutu Lifecycle sınıfınızda el ile bağlayın (bkz. Adım 3). Bu yöntem 0-GC ve AOT/IL2CPP uyumludur:

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

##### Yöntem B: Otomatik Keşfedilen Komut (Otomatik Kayıt)
Otomatik keşif özelliğinden yararlanmak için yine `[SignalHandler]` özniteliğini kullanabilirsiniz, ancak komutunuz **mutlaka** generic `ICommand<TSignal>` veya `IAsyncCommand<TSignal>` arayüzünü uygulamalıdır. Eski reflection tabanlı non-generic yapı, 0-GC ve AOT/IL2CPP uyumluluğunu zorunlu kılmak amacıyla tamamen kaldırılmıştır:

```csharp
using Nexus.Core;
using UnityEngine;

[SignalHandler(typeof(DamageSignal))] // Nexus tarafından otomatik olarak taranıp kaydedilir
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
*(Not: Sinyale bağlanan tüm komutlar generic arayüzleri uygulamak zorundadır. Non-generic komut kullanılması durumunda derleme hatası verilir veya build validation işlemi başarısız olur).*

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
