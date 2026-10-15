# Nexus Entegrasyon Rehberi (UPM / npm / Submodule)

> **Amaç:** `com.nexus.core` paketinin başka bir Unity projesine entegre edilmesi sırasında
> karşılaşılabilecek sorunları ve doğru entegrasyon yollarını belgelemek.
>
> **Tarih:** 2026-08-02 · **Kapsam:** UPM (Git URL), npm, git submodule

---

## 1. Paket Yapısı Özeti

`com.nexus.core` bir **Unity Package Manager (UPM)** paketidir ve monorepo içinde
`Nexus/Packages/com.nexus.core/` yolunda yaşar. Paketin entegrasyon yüzeyi:

| Öğe | Konum | Entegrasyon Etkisi |
| --- | --- | --- |
| `package.json` | paket kökü | UPM metadata: `name`, `version`, `unity`, `dependencies` |
| `Runtime/com.nexus.core.asmdef` | Runtime | `UnityEngine.UI`'ya referans verir (→ `com.unity.ugui` gerekir) |
| `Editor/com.nexus.core.editor.asmdef` | Editor | `includePlatforms: Editor` → üretim build'ine girmez |
| `Runtime/DOTS/com.nexus.core.dots.asmdef` | Runtime | `Unity.Collections`'a bağlı, `UNITY_COLLECTIONS` define'ı ile korunur (opsiyonel) |
| `Tests/**` asmdef'leri | Tests | `autoReferenced: false` + `UNITY_INCLUDE_TESTS` → tüketiciyi kirletmez |
| `Runtime/link.xml` | Runtime | IL2CPP koruma kuralları; UPM paket içindeki link.xml'i otomatik birleştirir |
| `Editor/csc.rsp` | Editor | Yalnızca `-nowarn:0649` |
| `Samples~/` | paket | `~` soneki → derlemeye girmez; UPM konvansiyonu doğru |
| Harici SDK referansları | — | **Yok** (`AdService` `[StubService]`; IAP/Ads SDK'sı paket dışı) |

---

## 2. UPM (Git URL) — Önerilen Yol ✅

### 2.1 Kurulum

`Packages/manifest.json`'a ekleyin:

```json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/CevatAYDN/GameContainer.git?path=Nexus/Packages/com.nexus.core"
  }
}
```

`?path=` sayesinde Unity, repoyu klonlayıp **yalnızca paket dizinini** içe aktarır —
demo projesi (`Nexus/Assets`), benchmark harness ve dokümanlar tüketici projesine girmez.

Sürüm sabitleme için tag/branch eklenebilir:
`...git?path=Nexus/Packages/com.nexus.core#v0.4.0`

### 2.2 UPM Entegrasyon Kontrol Listesi

| # | Kontrol | Durum (2026-08-02) | Not |
| --- | --- | --- | --- |
| 1 | `package.json` geçerli (name/version/unity) | ✅ | `0.4.0`, `unity: 6000.0` |
| 2 | `com.unity.ugui` bağımlılığı beyanı | ✅ | `dependencies` içinde `com.unity.ugui: 2.0.0` (runtime asmdef `UnityEngine.UI` referansı için zorunlu) |
| 3 | Test asmdef'leri tüketiciyi kirletmez | ✅ | `autoReferenced: false` + `UNITY_INCLUDE_TESTS` define kısıtı |
| 4 | Editor kodu üretim build'ine girmez | ✅ | `com.nexus.core.editor` asmdef `includePlatforms: Editor` |
| 5 | DOTS köprüsü isteğe bağlı | ✅ | `com.nexus.core.dots` yalnızca `UNITY_COLLECTIONS` varsa derlenir |
| 6 | IL2CPP koruma kuralları paketle taşınır | ✅ | `Runtime/link.xml` |
| 7 | Harici SDK/derleme bağımlılığı yok | ✅ | Paket tek başına derlenebilir (Ads/IAP stub) |
| 8 | Örnekler derlemeye girmez | ✅ | `Samples~` soneki |
| 9 | Unity sürüm uyumu | ⚠️ | **Unity 6 (6000.x) zorunlu**; 2023 ve altı desteklenmez |
| 10 | Roslyn/CodeAnalysis bağımlılığı | ✅ | Paket Editor'unda yok (demo projesinin `Assets/Plugins`'inde) |

### 2.3 UPM'de Dikkat Edilecek Noktalar

1. **Unity 6 zorunludur.** `package.json`'da `"unity": "6000.0"`. Daha eski bir Unity
   sürümünde UPM uyarı verir veya paketi kabul etmez. C# 9+ / .NET Standard 2.1 özellikleri
   kullanılır.
2. **`com.unity.ugui` bağımlılığı** — runtime asmdef `UnityEngine.UI` assembly'sine
   referans verir (UIManager, View/Mediator sistemi). Bu bağımlılık `package.json`'da
   beyan edilmiştir; UPM otomatik çözer. (Not: eski sürümlerde beyan eksikti; bu belgeyle
   birlikte düzeltilmiştir.)
3. **DOTS isteğe bağlıdır** — projenizde `com.unity.collections` yoksa `com.nexus.core.dots`
   derlenmez, paketin geri kalanı çalışmaya devam eder.
4. **İlk import** sonrası `NexusGeneratedBinder.g.cs` benzeri jeneratör çıktıları
   `Assets/Scripts/Nexus/` altında üretilir — bunlar tüketici projesine aittir, pakete değil.

### 2.4 Tüketici Projesinde Doğrulama Adımları (Manuel Test)

1. Boş bir Unity 6 projesi açın (örn. 6000.5.x).
2. `Packages/manifest.json`'a yukarıdaki Git URL'ini ekleyin.
3. Package Manager'da (Window → Package Manager) `Nexus Observable Architecture`'ın
   listelendiğini ve hata olmadan import edildiğini doğrulayın.
4. Console'da kırmızı hata olmadığını kontrol edin (Assembly-CSharp derlenmeli).
5. `GameObject → Nexus → Create Root` menüsünü çalıştırın — Root Wizard açılmalı.
6. `Nexus → Validate Architecture` menüsünü çalıştırın — mimari doğrulama yeşil olmalı.
7. Test etmek isterseniz EditMode testlerini koşun: `Window → General → Test Runner →
   EditMode → Run All`.

---

## 3. npm — Uygulanamaz ❌

Bu **npm paketi değildir**:

- npm config'i, `package-lock.json`'ı veya npm registry metadata'sı yoktur.
- npm, JavaScript/Node ekosistemi içindir; Unity kendi paket yöneticisi (UPM) kullanır.
- `npm install` ile bu paketi çekmek mümkün değildir ve bunu denemek anlamsızdır.
- (Bir özel registry'de npm biçiminde Unity paketi barındırmak teknik olarak yapılabilir,
  ancak Unity bu akışı desteklemez ve standart değildir.)

**Sonuç:** npm entegrasyonu için bir gereksinim yoktur; Unity paketleri UPM üzerinden
tüketilir.

---

## 4. Git Submodule — Dikkatli Kullanım ⚠️

### 4.1 Neden Riskli?

Bu repo bir **monorepo**'dur: pakete ek olarak `Nexus/Assets` (demo Unity projesi: sahneler,
`GameContextData.asset`, `NexusEditorSettings`, Roslyn plugin'leri) ve
`tools/nexus-benchmark` (harness) içerir. Git submodule'leri **alt dizin seçemez** — bir
submodule her zaman tüm repoyu (commit bazında) çeker. Tümünü submodule yaparsanız tüketici
projesine demo içeriği de girer → istenmeyen asset'ler, potansiyel GUID/asset çakışmaları.

Ayrıca repoda hazır `.gitmodules` yoktur; submodule kurulumu tamamen tüketici tarafındadır.

### 4.2 Çalışan Kombinasyonlar

**A) UPM Git URL (önerilen, §2'deki):** submodule kullanmadan sürüm sabitleme yapmanın en
temiz yoludur — `?path=` ile yalnızca paket klonlanır.

**B) Monorepo submodule + `file:` referansı:**

```bash
git submodule add https://github.com/CevatAYDN/GameContainer.git <konum>/GameContainer
```

`Packages/manifest.json`'a:

```json
{
  "dependencies": {
    "com.nexus.core": "file:<konum>/GameContainer/Nexus/Packages/com.nexus.core"
  }
}
```

⚠️ Bu durumda submodule içindeki demo projesi ve benchmark kodları diske iner; Unity'nin
yalnızca paket klasörünü referans alması için **yolun doğru verilmesi** şarttır. Submodule'ü
projenin `Assets/` altına koymayın (demo içeriği import edilir).

**C) Paket için ayrı repo (en temiz submodule deneyimi):** Paket ayrı bir GitHub reposuna
split edilirse (ör. `nexus-core`), submodule tek başına paketi çeker. Bu, tekrarlayan
entegrasyonlarda en sürdürülebilir çözümdür.

### 4.3 Submodule Kontrol Listesi

- [ ] Paketi `Assets/` altına submodule yapmayın — demo içeriği projeye girer.
- [ ] `file:` referansı doğru klasörü (`.../Nexus/Packages/com.nexus.core`) gösteriyor.
- [ ] Submodule'ü güncellerken manifest'teki commit ile senkronizasyonu unutmayın.
- [ ] Offline geliştirme istiyorsanız `file:` path kullanın; dağıtımda Git URL'ine dönün.

---

## 5. Adapter Entegrasyonu (Ads / IAP)

`com.nexus.core` paketi **gerçek SDK bağımlılığı taşımamaz** — tüm monetizasyon servisleri
**adapter pattern** ile soyutlanmıştır. Tüketici projesinde gerçek adapter'ları kaydetmeniz gerekir.

### 5.1 Ads Adapter Kayıtı

```csharp
using Nexus.Core.Services;
using UnityEngine;

public class AdsBootstrap : MonoBehaviour
{
    private async void Awake()
    {
        // Context initialize olana kadar bekle
        var root = FindObjectOfType<Nexus.Core.Root>();
        if (root == null) return;

        await WaitForContext(root);

        var factory = root.Context.TryResolve<AdAdapterFactory>();
        if (factory == null) return;

        // Seçenek A: Built-in mock (development/test)
        // factory.CreateAdapter("mock"); // Zaten varsayılan olarak kayıtlı

        // Seçenek B: AdMob (Google Mobile Ads Unity Plugin gerekli)
        // factory.RegisterProvider("admob", () => new AdMobAdapter());

        // Seçenek C: AppLovin MAX (AppLovin MAX Unity Plugin gerekli)
        // factory.RegisterProvider("applovin", () => new AppLovinMaxAdapter());

        // Seçenek D: IronSource LevelPlay (IronSource Unity Plugin gerekli)
        // factory.RegisterProvider("ironsource", () => new IronSourceAdapter());

        // AdService'e bağla
        var adService = root.Context.TryResolve<AdService>();
        var adapter = factory.CreateAdapter("admob"); // veya "applovin", "ironsource", "mock"
        if (adapter != null && adService != null)
        {
            adService.SetNetworkAdapter(adapter);
            Debug.Log("[AdsBootstrap] Ad adapter registered successfully.");
        }
    }

    private System.Threading.Tasks.Task WaitForContext(Nexus.Core.Root root)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        System.Action check = null;
        check = () =>
        {
            if (root.IsInitialized)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                System.Threading.Tasks.Task.Delay(100).ContinueWith(_ => check());
            }
        };
        check();
        return tcs.Task;
    }
}
```

**AdMob Adapter Örneği** (consumer projesinde implement edilir):

```csharp
using GoogleMobileAds.Api;
using Nexus.Core.Services;
using System;
using UnityEngine;

public sealed class AdMobAdapter : IAdNetworkAdapter
{
    private bool _initialized;
    private readonly System.Collections.Generic.Dictionary<string, InterstitialAd> _interstitials = new();
    private readonly System.Collections.Generic.Dictionary<string, RewardedAd> _rewardeds = new();

    public void Initialize(Action onInitialized)
    {
        MobileAds.Initialize(initStatus =>
        {
            _initialized = true;
            onInitialized?.Invoke();
        });
    }

    public bool IsInterstitialReady(string placement)
    {
        return _interstitials.TryGetValue(placement, out var ad) && ad.CanShowAd();
    }

    public void ShowInterstitial(string placement, Action onClosed)
    {
        if (!_interstitials.TryGetValue(placement, out var ad) || !ad.CanShowAd())
        {
            onClosed?.Invoke();
            return;
        }

        ad.OnAdFullScreenContentClosed += () =>
        {
            onClosed?.Invoke();
            LoadInterstitial(placement); // Preload next
        };
        ad.Show();
    }

    public bool IsRewardedReady(string placement)
    {
        return _rewardeds.TryGetValue(placement, out var ad) && ad.CanShowAd();
    }

    public void ShowRewarded(string placement, Action<bool> onCompleted)
    {
        if (!_rewardeds.TryGetValue(placement, out var ad) || !ad.CanShowAd())
        {
            onCompleted?.Invoke(false);
            return;
        }

        ad.OnUserEarnedReward += reward => onCompleted?.Invoke(true);
        ad.OnAdFullScreenContentClosed += () => onCompleted?.Invoke(false);
        ad.OnAdFullScreenContentFailed += error => onCompleted?.Invoke(false);
        ad.Show();
    }

    public void ShowBanner(string placement, string position)
    {
        // Banner implementation with AdSize.Banner, position handling
    }

    public void HideBanner()
    {
        // Hide banner
    }

    private void LoadInterstitial(string placement)
    {
        var request = new AdRequest();
        InterstitialAd.Load(GetAdUnitId(placement), request, (ad, error) =>
        {
            if (error == null) _interstitials[placement] = ad;
        });
    }

    private string GetAdUnitId(string placement) => placement switch
    {
        "default" => "ca-app-pub-XXXXXXXXXXXXXXXX/YYYYYYYYYY",
        "gameover" => "ca-app-pub-XXXXXXXXXXXXXXXX/ZZZZZZZZZZ",
        _ => "ca-app-pub-XXXXXXXXXXXXXXXX/YYYYYYYYYY"
    };
}
```

### 5.2 IAP Adapter Kayıtı

```csharp
using Nexus.Core.Services;
using UnityEngine;

public class IapBootstrap : MonoBehaviour
{
    private async void Awake()
    {
        var root = FindObjectOfType<Nexus.Core.Root>();
        if (root == null) return;

        await WaitForContext(root);

        var factory = root.Context.TryResolve<IapAdapterFactory>();
        if (factory == null) return;

        // Seçenek A: Built-in mock (development/test)
        // factory.CreateAdapter("mock"); // Zaten varsayılan olarak kayıtlı

        // Seçenek B: Unity IAP (com.unity.purchasing gerekli)
        // factory.RegisterProvider("unityiap", () => new UnityIapAdapter());

        // Seçenek C: RevenueCat (RevenueCat Unity SDK gerekli)
        // factory.RegisterProvider("revenuecat", () => new RevenueCatAdapter());

        var iapService = root.Context.TryResolve<IapService>();
        var adapter = factory.CreateAdapter("unityiap"); // veya "revenuecat", "mock"
        if (adapter != null && iapService != null)
        {
            iapService.SetStoreAdapter(adapter);
            Debug.Log("[IapBootstrap] IAP adapter registered successfully.");
        }
    }

    private System.Threading.Tasks.Task WaitForContext(Nexus.Core.Root root)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        System.Action check = null;
        check = () =>
        {
            if (root.IsInitialized)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                System.Threading.Tasks.Task.Delay(100).ContinueWith(_ => check());
            }
        };
        check();
        return tcs.Task;
    }
}
```

### 5.3 Package.json Bağımlılıkları

Gerçek SDK kullanıyorsanız tüketici projenizin `Packages/manifest.json`'una ekleyin:

```json
{
  "dependencies": {
    "com.nexus.core": "https://github.com/CevatAYDN/GameContainer.git?path=Nexus/Packages/com.nexus.core",
    
    // Ads (birini seçin)
    "com.google.ads.mobile": "8.13.0",           // AdMob
    "com.applovin.mediation.unity": "7.2.0",     // AppLovin MAX
    "com.ironsource.mediation": "7.5.0",         // IronSource LevelPlay
    
    // IAP (birini seçin)
    "com.unity.purchasing": "4.9.0",             // Unity IAP
    "com.revenuecat.purchases": "6.0.0"          // RevenueCat
  }
}
```

> **Not:** Bu bağımlılıklar `com.nexus.core` paketinde **yoktur** — sürüm çakışmasını önlemek ve consumer'ın kendi SDK sürümünü kontrol edebilmesi için.

---

## 6. Sürüm ve Bakım Notları

- Paket sürümü `package.json` → `0.4.0`; değişiklikler `CHANGELOG.md`'de (paket içi)
  işlenir (NEXUS_READY Kapı 9 — sürüm disiplini).
- Breaking değişiklikler `BREAKING_CHANGES.md` ve `MIGRATION.md` ile duyurulur.
- Gerçek SDK entegrasyonları (AdMob/AppLovin, IAP) paket dışıdır; `AdService`/`IapService`
  adaptör noktalarından tüketici tarafında bağlanır.

---

## 6. Sonuç Tablosu

| Entegrasyon Yolu | Uygunluk | Yorum |
| --- | --- | --- |
| UPM Git URL (`?path=`) | ✅ **Önerilen** | Sparse checkout, sürüm sabitleme, temiz |
| npm | ❌ Uygulanamaz | Unity paketleri npm ile tüketilmez |
| Git submodule (monorepo tamamı) | ⚠️ Riskli | Örnek içerik girer; paket tecrit edilmezse sorun çıkar |
| Git submodule (ayrı paket repo) | ✅ Temiz | Split sonrası en iyi submodule deneyimi |
