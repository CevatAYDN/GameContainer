# Nexus: Observable Architecture

Nexus, Unity 6.5+ için tasarlanmış, **0 GC allocation (steady-state)** hedefleriyle çalışan, yüksek düzeyde izlenebilir (observable) açık kaynaklı bir MVCS mimari çatısıdır.

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

### 1. Sinyal Tanımlayın (Immutable Struct)
```csharp
public readonly struct DamageSignal
{
    public readonly int Amount;
    public DamageSignal(int amount) => Amount = amount;
}
```

### 2. Komutu Yazın (Business Logic)
```csharp
using Nexus.Core;
using UnityEngine;

[SignalHandler(typeof(DamageSignal))]
public class DamageCommand : ICommand
{
    // Sinyal içeriği otomatik enjekte edilir
    private readonly DamageSignal _signal; 
    
    [Inject] private IPlayerModel _playerModel; // Model enjeksiyonu

    public void Execute()
    {
        _playerModel.Health -= _signal.Amount;
        Debug.Log($"Damage processed: {_signal.Amount}. New Health: {_playerModel.Health}");
    }
}
```

### 3. Sinyali Fırlatın
```csharp
_signalBus.Fire(new DamageSignal(10));
```

---

## 🛠️ Araçlar
- **Root Wizard (`GameObject -> Nexus -> Create Root`):** 30 saniyede Nexus Root GameObject ve ContextData yapılandırmasını sahnede hazırlar.
- **Architectural Validator (`Nexus -> Validate Architecture`):** Projenin mimari kurallara uygunluğunu kontrol eder.

---

## 📄 Lisans
Bu proje MIT lisansı ile lisanslanmıştır.
