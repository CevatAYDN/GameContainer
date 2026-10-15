# Nexus Rakip Analizi — Unity DI / MVCS / Messaging / Reactive Kütüphaneleri

**Tarih:** 2026-10-15
**Kapsam:** VContainer, Zenject, Extenject (Mathijs-Bakker fork), StrangeIoC, Reflex; ek olarak MessagePipe (signal/event rakibi), R3 ve UniRx (reactive model rakibi) ve GitHub'da bulunan diğer aktif Unity DI container'ları (Saneject, UniDi, usyrup, tarject, VitalRouter).
**Amaç:** Nexus'un clarity, performance ve ease of use eksenlerinde nerede iyileşmesi gerektiğine karar verebilmek için, yalnızca PRIMARY kaynaklardan (GitHub repo, releases, README, docs sitesi, package.json, kaynak kod) doğrulanmış, tarihli olgular toplamak. Bu belgede **Nexus sütunu yoktur**; owner koddan dolduracaktır.

## Yöntem

Tüm veriler 2026-10-15 tarihinde aşağıdaki kaynaklardan çekildi:

| Veri | Kaynak / endpoint |
|---|---|
| Stars, forks, license, default branch, `pushed_at`, `archived` | `GET https://api.github.com/repos/{owner}/{repo}` |
| Son release sürümü ve tarihi | `GET https://api.github.com/repos/{owner}/{repo}/releases/latest` ve `/releases?per_page=N` |
| Default branch üzerindeki son commit | `GET https://api.github.com/repos/{owner}/{repo}/commits?per_page=1` (`commit.committer.date`) |
| Açık issue / PR sayıları (ayrı ayrı) | `GET https://api.github.com/search/issues?q=repo:{r}+is:issue+is:open` ve `...+is:pr+is:open` (`total_count`) |
| En çok yorum alan açık issue'lar | `GET https://api.github.com/search/issues?q=repo:{r}+is:issue+is:open&sort=comments&order=desc&per_page=3` |
| `package.json` `unity` alanı | `raw.githubusercontent.com` üzerinden ilgili dosya |
| README / docs içeriği | `raw.githubusercontent.com` (README.md, `website/docs/*.mdx`, `website/src/components/*.tsx`) |
| OpenUPM aylık indirme | `GET https://package.openupm.com/downloads/point/last-month/{package}` (aralık: 2026-09-15 → 2026-10-14) |
| OpenUPM paket kaynağı | `https://raw.githubusercontent.com/openupm/openupm/master/data/packages/{package}.yml` |
| Diğer DI container taraması | `GET https://api.github.com/search/repositories?q=unity+dependency+injection&sort=stars` ve `q=topic:dependency-injection+topic:unity&sort=stars` |

Not: `open_issues_count` alanı GitHub'da PR'ları da içerir; bu yüzden issue ve PR sayıları search API ile ayrı ayrı alındı.

## Doğrulanamayanlar (açık liste)

- **Unity Asset Store sayfaları** fetch edilmedi; Asset Store linkleri yalnızca README'lerde yazıldığı şekliyle aktarıldı. Asset Store indirme / rating sayıları **bilinmiyor**.
- **VContainer benchmark grafiği**: sayılar docs sitesinin React component'inden (`BenchmarkGraph.tsx`, `GCAllocGraph.tsx`, `CodeSizeGraph.tsx`) alındı. Benchmark kodu bu araştırmada çalıştırılmadı; ölçüm ortamı yalnızca docs'ta yazıldığı kadarıyla biliniyor ("Unity 2019.x / IL2CPP Standalone macOS").
- **Reflex benchmark tablosu**: README'deki tablo aynen alındı; Unity sürümü, donanım ve VContainer/Zenject sürümü README'de **belirtilmiyor**.
- **MessagePipe "78x faster than Prism"** iddiası: README'de yalnızca görsel grafik var; metin olarak metodoloji yok. Benchmark kaynak kodu `tests/MessagePipe.Benchmark` altında mevcut, fakat çalıştırılmadı.
- **StrangeIoC**: GitHub API `license` alanı `null` döndü; lisans README içindeki Apache 2.0 metninden alındı. README "Current version: v1.0.0" derken repo'daki tek tag'ler `v0.6.0` ve `v0.6.1`; bu çelişki çözülemedi.
- **Zenject (modesttree)**: `pushed_at` 2024-01-26 ama master'daki son commit 2021-04-06. Aradaki push'un hangi branch'e yapıldığı doğrulanmadı.
- **Unity 6 desteği**: Hiçbir rakibin `package.json` `unity` alanı Unity 6'yı (`6000.x`) **açıkça** belirtmiyor. "Unity 6 destekleniyor" yorumları yalnızca release notlarındaki Unity 6.x API fix'lerine dayanıyor; bunlar açıkça belirtildi.
- **IL2CPP**: Yalnızca README/docs'ta açıkça yazanlar işaretlendi.
- Extenject (Mathijs-Bakker) ve StrangeIoC için OpenUPM paketi yok ya da farklı fork'a işaret ediyor (aşağıda açıklandı).

---

## 1. VContainer

Repo: https://github.com/hadashiA/VContainer — Docs: https://vcontainer.hadashikick.jp/

### 1.1 Sürüm / aktivite / lisans
- Son release: **1.19.0**, 2026-07-01 (`/releases/latest`). Önceki: 1.18.0 (2026-05-14), 1.17.0 (2025-07-22). https://github.com/hadashiA/VContainer/releases
- Default branch `master` son commit: **2026-10-06** (`fb24d75`) (`/commits?per_page=1`).
- Stars **3.072**, forks 272 (`/repos`).
- Açık issue **87**, açık PR **9** (search API).
- Lisans: **MIT** (`/repos` `license.spdx_id`).
- `archived: false`.

### 1.2 Unity minimum sürüm / Unity 6 / IL2CPP
- `package.json` (`VContainer/Assets/VContainer/package.json`): `"unity": "2018.4"`, `"version": "1.19.0"`. https://raw.githubusercontent.com/hadashiA/VContainer/master/VContainer/Assets/VContainer/package.json
- README: "*Requires Unity 2018.4+*" https://github.com/hadashiA/VContainer#installation
- Source Generator docs: ":::caution This feature requires Unity 2021.3 or later." https://vcontainer.hadashikick.jp/optimization/source-generator
- Unity 6: `package.json`'da açıkça yok. Release notları Unity 6.x API uyumluluk fix'leri içeriyor: 1.18.0 — "fix: resolve TreeView deprecation in Unity 6.2+" (PR #844) ve "fix: resolve GetInstanceID deprecation in Unity 6.4+" (PR #845). https://github.com/hadashiA/VContainer/releases/tag/1.18.0
- IL2CPP: docs'ta açıkça: "It works in the IL2CPP environment." (codegen.mdx) ve "Note that classes that are targeted for code generation are `[Preserve]` and therefore not subject to code-stripping by IL2CPP." (source-generator.mdx). Open generic için: "We have confirmed that this works with IL2CPP from Unity 2022.1 onwards, but older versions may not support dynamic typedefs." https://vcontainer.hadashikick.jp/registering/register-type

### 1.3 Kurulum yolları (README)
- UPM git URL: `"jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.19.0"`
- OpenUPM: `openupm add jp.hadashikick.vcontainer`
- `.unitypackage` (releases sayfası)
- Asset Store: README'de **bahsedilmiyor**.
Kaynak: https://github.com/hadashiA/VContainer#installation

### 1.4 Özellik envanteri (kaynak: README "Features" + docs)
- Constructor / Method / Property & Field injection: ✅ (README: "Constructor Injection / Method Injection / Property & Field Injection")
- Lifetimes: ✅ Singleton / Scoped / Transient (`Lifetime` enum; docs: "VContainer always requires a `Lifetime` argument explicitly.") https://vcontainer.hadashikick.jp/scoping/lifetime-overview
- Child scope / hiyerarşi: ✅ `LifetimeScope` parent-child, `CreateChild`, `CreateChildFromPrefab`, `EnqueueParent` (README "Flexible Scoping with async")
- Factories: ✅ `Func<>` tabanlı (`RegisterFactory`) https://vcontainer.hadashikick.jp/registering/register-factory
- Lazy injection: ❌ (docs'ta yok; açık feature request #519 "[Feature Request] Lazy inject", 2023-05-22)
- Named / keyed bindings: ✅ `.Keyed(key)` + `[Key(...)]` attribute; "VContainer supports keys of any type." (1.17.0 "Injection by ID" PR #764 ile eklendi) https://vcontainer.hadashikick.jp/registering/register-with-keys
- MonoBehaviour / prefab injection: ✅ `RegisterComponentInHierarchy`, `RegisterComponentInNewPrefab` vb. (docs/registering/register-monobehaviour.mdx)
- Scene scopes: ✅ Additive scene için `LifetimeScope.EnqueueParent` (README)
- Entry points (ITickable-style): ✅ `IInitializable, IPostInitializable, IStartable, IAsyncStartable, IPostStartable, IFixedTickable, IPostFixedTickable, ITickable, IPostTickable, ILateTickable, IPostLateTickable` — kendi `PlayerLoopSystem`'i üzerinden https://vcontainer.hadashikick.jp/integrations/entrypoint
- Source generator / AOT codegen: ✅ Roslyn Source Generator (1.13.0'dan itibaren; önceden Mono.Cecil IL weaving). Sınırlama: "Nested classes, Struct, access level less than internal" desteklenmez. https://vcontainer.hadashikick.jp/optimization/source-generator
- Validation / diagnostics: ✅ Diagnostics Window (`VContainerSettings` → "Enable Diagnostics"); uyarı: "'Enable Diagnostics' will significantly degrade performance; GC allocation will also be greatly increased." https://vcontainer.hadashikick.jp/diagnostics/diagnostics-window
- Editor-time object graph validation (Zenject tarzı): ❌ docs'ta yok
- Signal bus / messaging: ❌ yerleşik değil; MessagePipe entegrasyon sayfası var (docs/integrations/messagepipe.mdx)
- Reactive / observable model: ❌ yerleşik değil; UniRx entegrasyon sayfası var (docs/integrations/unirx.mdx)
- Async: ✅ `IAsyncStartable` — UniTask yüklüyse `VCONTAINER_UNITASK_INTEGRATION` otomatik tanımlanır; aksi halde "`async Awaitable StartAsync()` is available." (entrypoint.mdx) https://vcontainer.hadashikick.jp/integrations/unitask
- Test helpers: ❓ dedicated test fixture docs yok; method-injection.mdx: "can be done with any class, including test cases within the Unity Test Framework"
- ECS entegrasyonu: "*beta*" (README)
- Immutable container: "Thread safety and robustness." (README)

### 1.5 Performans iddiaları (aynen)
README: "**Fast Resolve:** Basically 5-10x faster than Zenject." ve "**Minimum GC Allocation:** In Resolve, we have **zero allocation** without spawned instances." https://github.com/hadashiA/VContainer#vcontainer

Docs (what-is-vcontainer.mdx) başlığı: "Benchmark result for 10,000 iterations for each test case (Unity 2019.x / IL2CPP Standalone macOS)". Grafiğin ham verisi (`website/src/components/BenchmarkGraph.tsx`, birim grafikte belirtilmemiş; ms olduğu varsayılmamalı — **birim doğrulanamadı**):

| Test case | VContainer (CodeGen) | VContainer (Reflection) | Zenject (Reflection) |
|---|---:|---:|---:|
| Singleton | 4.3884 | 4.918865 | 21.209325 |
| Transient | 4.3569 | 19.2531 | 79.1066 |
| Combined | 15.0227 | 49.60295 | 164.6642 |
| Complex | 18.8648 | 57.0287 | 441.1458 |
| Initialize | 159.816 | 272.3955 | 683.2439 |

Kaynak: https://github.com/hadashiA/VContainer/blob/master/website/src/components/BenchmarkGraph.tsx

GC Alloc ("GC Alloc result in the Resolve Complex test case (Unity Editor profiled)", `GCAllocGraph.tsx`): VContainer **1.9**, Zenject **27.3** (birim component'te yok; README'deki profiler ekran görüntüsü KB gösteriyor — **doğrulanamadı**).

Code size (comparing-to-zenject.mdx, "Code size (v1.3.0)", `CodeSizeGraph.tsx`): iki seri: VContainer 24 / Zenject 478 ve VContainer 1078 / Zenject 9469 (etiketler component'te okunamadı — muhtemelen type count ve method count; **doğrulanamadı**).

Docs notu: "By default, both VContainer and Zenject use reflection at runtime." ve "'VContainer (CodeGen)' means optimization by pre-generating IL code of Inject methods by ILPostProcessor." https://vcontainer.hadashikick.jp/

**Ölçülmeyenler:** Reflex ile karşılaştırma yok; Unity 6 / modern donanım yok; Mono backend yok; benchmark Unity 2019.x'te yapılmış ve tarihli değil; container build süresi dışında scene-load / MonoBehaviour injection maliyeti yok.

### 1.6 API ergonomisi — hello world (docs/getting-started/hello-world.mdx)
```csharp
public class HelloWorldService
{
    public void Hello() => UnityEngine.Debug.Log("Hello world");
}

public class GameLifetimeScope : LifetimeScope          // MonoBehaviour, sahneye eklenir
{
    protected override void Configure(IContainerBuilder builder)
    {
        builder.Register<HelloWorldService>(Lifetime.Singleton);
        builder.RegisterEntryPoint<GamePresenter>();     // plain C# entry point
    }
}

public class GamePresenter : IStartable
{
    readonly HelloWorldService helloWorldService;
    public GamePresenter(HelloWorldService helloWorldService) => this.helloWorldService = helloWorldService;
    void IStartable.Start() => helloWorldService.Hello();
}
```
Kaynak: https://vcontainer.hadashikick.jp/getting-started/hello-world (README "Basic Usage" bölümü ile aynı pattern).

### 1.7 Adoption sinyalleri
- Stars 3.072 (GitHub API).
- OpenUPM son 30 gün indirme: **35.556** (`package.openupm.com/downloads/point/last-month/jp.hadashikick.vcontainer`, 2026-09-15 → 2026-10-14). OpenUPM'de 78 release build'i var (`api.openupm.com/packages/jp.hadashikick.vcontainer`).
- README/docs'ta isimli oyun/stüdyo listesi **yok**.
- MessagePipe README'si VContainer'ı birinci sınıf entegrasyon olarak listeliyor (bkz. §6).

### 1.8 Kendi issue tracker'ındaki en çok yorumlanan açık issue'lar
1. 16 yorum — "Simple registration, how?" (2023-09-30) https://github.com/hadashiA/VContainer/issues/558
2. 13 yorum — "Decorator support?" (2021-03-10) https://github.com/hadashiA/VContainer/issues/157
3. 10 yorum — "[Feature Request] Lazy inject" (2023-05-22) https://github.com/hadashiA/VContainer/issues/519

Yorum: Üç issue da **özellik eksikliği** (basit registration ergonomisi, decorator, Lazy<T>) — Nexus için fırsat alanları.

---

## 2. Zenject (modesttree) ve Extenject — hangi fork yaşıyor?

### 2.1 modesttree/Zenject
Repo: https://github.com/modesttree/Zenject
- Son release: **9.2.0**, 2020-05-25 (`/releases/latest`). Tag listesi: v9.1.0, v9.0.0, v8.0.1 (tags endpoint `9.2.0` tag'ini listede göstermedi; release var).
- master son commit: **2021-04-06** (`31f06cf`, "Update README.md"); `pushed_at`: 2024-01-26 (hangi branch olduğu doğrulanmadı).
- Stars **2.940**, forks 295. Açık issue **140**, açık PR **15**. Lisans MIT. `archived: false`.
- `UnityProject/Assets/Plugins/Zenject/package.json`: `"name": "com.svermeulen.extenject", "version": "0.0.1"` — **`unity` alanı yok**. `ProjectVersion.txt`: `m_EditorVersion: 2019.3.0f6`.
- README `Documentation/ReleaseNotes.md` en son "Version 9.1.0 (October 13, 2019)".
- Durum: **fiilen bakımsız** (5+ yıldır release yok, 4+ yıldır master commit yok).

### 2.2 svermeulen/Extenject
`https://github.com/svermeulen/Extenject` → HTTP 301 → `https://github.com/modesttree/Zenject`. Yani svermeulen fork'u artık modesttree reposuna yönlendiriyor; ayrı bir proje değil.

### 2.3 Mathijs-Bakker/Extenject
Repo: https://github.com/Mathijs-Bakker/Extenject
- README: "This project is a fork of Zenject with the goal of being actively maintained."
- Son release: **9.2.1**, 2021-11-27 (`/releases/latest`).
- master son commitler (`/commits?per_page=5`): 2026-08-04 Charlie Hess "Add Unity 6.6 Fast Enter Play Mode resets"; 2025-09-29 "Docs clean-up"; 2025-09-29 "Update manifest.json"; 2025-08-30 Mathijs Bakker "Update README.md"; 2025-08-30 "Create FUNDING.yml".
- Stars **699**, forks 113. Açık issue **36**, açık PR **0**. Lisans MIT.
- `UnityProject/Assets/Plugins/Zenject/Source/package.json`: `"name": "com.svermeulen.extenject", "version": "9.2.0", "unity": "2019.3"`. https://raw.githubusercontent.com/Mathijs-Bakker/Extenject/master/UnityProject/Assets/Plugins/Zenject/Source/package.json
- Bakımcı beyanı — issue #73 "I am going to stop." (2023-01-08, hâlâ **open**): "Ok, after many years of supporting Zenject **I am going to stop**. ... I am not using Unity in any professional or personal sense anymore." https://github.com/Mathijs-Bakker/Extenject/issues/73
- Unity 6: README/package.json'da açıkça yok; tek sinyal 2026-08-04 tarihli "Unity 6.6 Fast Enter Play Mode resets" commit'i.

**Sonuç:** Üç fork'tan hiçbiri 2021'den beri release yapmamış. Mathijs-Bakker/Extenject tek "nefes alan" repo (2025-2026'da birkaç commit) ama bakımcı 2023'te bıraktığını yazmış ve release yok. OpenUPM `com.svermeulen.extenject` paketi ise **dördüncü** bir fork'a (`starikcetin/Extenject`) bağlı ve 9.2.0'da (2020-08) donmuş (`openupm/data/packages/com.svermeulen.extenject.yml`: `repoUrl: 'https://github.com/starikcetin/Extenject'`).

### 2.4 Unity minimum / IL2CPP (README, her iki repoda aynı metin)
- "Tested in Unity 3D on the following platforms: PC/Mac/Linux, iOS, Android, WebGL, PS4 (with IL2CPP backend), Windows Store (... both .NET and IL2CPP backend)"
- "IL2CPP is supported, however there are some gotchas - see [here](#aot-support) for details"
- AOT bölümü: "IL2CPP can sometimes strip out the constructors of classes, resulting in errors on IL2CPP platforms. The recommended fix in these cases is to either edit `link.xml` ... or to add an `[Inject]` attribute above the constructor."
Kaynak: https://github.com/modesttree/Zenject#introduction ve `#does-this-work-on-aot-platforms-such-as-ios-and-webgl`

### 2.5 Kurulum yolları (README "Installation")
- Releases sayfasından `.unitypackage` (`Zenject-WithAsteroidsDemo.vX.X.unitypackage`, `Zenject.vX.X.unitypackage`, `Zenject-NonUnity.vX.X.zip`)
- Unity Asset Store: https://assetstore.unity.com/packages/tools/utilities/extenject-dependency-injection-framework-157735 (README'de yazan link; sayfa doğrulanmadı)
- UPM: README'de hâlâ "This option is a feature request (issue #24). ... the expectation is in the first or second release of 2020." yazıyor; resmi UPM paketi **yok**, `upm` branch'i + UpmGitExtension öneriliyor.
- OpenUPM `com.svermeulen.extenject` (starikcetin fork, 9.2.0): son 30 gün **920** indirme.

### 2.6 Özellik envanteri (README "Features", aynen)
"Constructor injection, Field injection, Property injection, Method injection; Conditional binding (eg. by type, by name, etc.); Optional dependencies; Support for creating objects after initialization using factories; Nested Containers aka Sub-Containers; Injection across different Unity scenes; Scene parenting; global, project-wide bindings; Convention based binding; Ability to validate object graphs at editor time (including dynamic object graphs created via factories); Automatic binding on components in the scene using the `ZenjectBinding` component; Auto-Mocking using the Moq library; Built-in support for memory pools; decorator pattern using decorator bindings; automatically mapping open generic types; Built in support for unit test, integration tests, and scene tests; Just-in-time injection using the LazyInject<> construct; Support for multiple threads for resolving/instantiating; 'reflection baking' to eliminate costly reflection operations completely by directly modifying the generated assemblies; Automatic injection of game objects using ZenAutoInjecter component"
https://github.com/modesttree/Zenject#features

Ek olarak README başlıklarında: Signals (`Documentation/Signals.md`, `SignalBus`), Memory Pools, ITickable / IInitializable / IDisposable, Identifiers (`WithId`), UniRx Integration, Creating Unity EditorWindow's with Zenject.
- Lifetimes: `AsTransient / AsCached / AsSingle` (+ `NonLazy`)
- Source generator: ❌ (Reflection Baking = build-time IL değişikliği; Roslyn değil)
- Editor-time validation: ✅ "Edit -> Zenject -> Validate Current Scene or hitting CTRL+ALT+V"
- Async (UniTask/Awaitable): ❌ README'de yok
- Multithreading: "You need to enable the define `ZEN_MULTITHREADING`"; "Circular reference errors are handled less gracefully ... a StackOverflowException might be thrown"

### 2.7 Performans iddiaları
README'de sayısal benchmark **yok**. "How is performance?" sorusunun cevabı sadece "See [here](#optimization-recommendationsnotes)". Reflection Baking için: "Reflection Baking will also reduce the time taken to instantiate new objects. This is especially true on IL2CPP platforms". https://github.com/modesttree/Zenject#how-is-performance
Ölçülmeyen: her şey (sayı yok). Zenject sayıları yalnızca rakiplerin (VContainer, Reflex) benchmark'larında var.

### 2.8 API ergonomisi — hello world (README, aynen)
```csharp
public class TestInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        Container.Bind<string>().FromInstance("Hello World!");
        Container.Bind<Greeter>().AsSingle().NonLazy();
    }
}

public class Greeter
{
    public Greeter(string message) { Debug.Log(message); }
}
```
Çalıştırmak için ayrıca: sahneye `Zenject -> Scene Context`, `Create -> Zenject -> MonoInstaller`, installer'ı SceneContext'in "Installers" listesine sürükleme (README adımları). https://github.com/modesttree/Zenject#hello-world-example

### 2.9 Adoption sinyalleri
- Stars 2.940 (modesttree) + 699 (Mathijs-Bakker).
- `Documentation/GamesThatUseZenject.md` isimli liste: Beat Saber (Beat Games), Pokémon Go ve Ingress Prime (Niantic), Slugterra: Guardian Force, Stick Soccer 2, Farm Away! / Build Away! (Futureplay), Toy Clash, Golfriends, EcsRx ve diğerleri. https://github.com/modesttree/Zenject/blob/master/Documentation/GamesThatUseZenject.md (liste 2020 tarihli; güncel kullanım doğrulanmadı)
- Trademark notu (README License): "ZENJECT and EXTENJECT are a trademark of Modest Tree Media Inc."

### 2.10 En çok yorumlanan açık issue'lar
modesttree/Zenject:
1. 46 yorum — "upm support" (2019-08-14) https://github.com/modesttree/Zenject/issues/24
2. 20 yorum — "Assets: Images for docs" (2020-10-09) https://github.com/modesttree/Zenject/issues/182
3. 10 yorum — "StaticContext container binding does not properly override bindings in SceneTests" (2020-02-08) https://github.com/modesttree/Zenject/issues/88

Mathijs-Bakker/Extenject:
1. 14 yorum — "I am going to stop." (2023-01-08) https://github.com/Mathijs-Bakker/Extenject/issues/73
2. 10 yorum — "Readme should clarify relation to modesttree/zenject and what happened to svermeulen/extenject" (2021-04-26) https://github.com/Mathijs-Bakker/Extenject/issues/4
3. 6 yorum — "Unity Package Manager from git manifest not found" (2024-01-31) https://github.com/Mathijs-Bakker/Extenject/issues/84

Yorum: En büyük ağrı **UPM dağıtımının olmaması** ve **fork karmaşası**.

---

## 3. StrangeIoC

Repo: https://github.com/strangeioc/strangeioc — Site: http://strangeioc.github.io/strangeioc/

### 3.1 Sürüm / aktivite / lisans
- Son release: **v0.6.1**, 2013-09-26 (`/releases/latest`). Tüm tag'ler: v0.6.1, v0.6.0. README ise "Current version: v1.0.0" diyor — **çelişki, çözülemedi**.
- master son commit: **2017-12-09** (`ae26e06`); `pushed_at` 2023-03-17 (branch doğrulanmadı).
- Stars **1.610**, forks 437. Açık issue **37**, açık PR **14**. `archived: false`.
- Lisans: GitHub API `null`; README içinde "Copyright 2013 ThirdMotion, Inc. Licensed under the Apache License, Version 2.0". https://github.com/strangeioc/strangeioc#readme
- Bakım durumu (FAQ): "Strange is one of those 'provided as-is' things." ve "I've got a job and unfortunately can't field requests day-to-day." http://strangeioc.github.io/strangeioc/faq.html

### 3.2 Unity minimum / Unity 6 / IL2CPP
- README: "Strange works with Unity 3.5+."
- FAQ: "Strange works with all versions of Unity from 3.5 and up." Bilinen çalışan platformlar: Android, Gear VR, iOS, Linux/Mac/Windows Standalone, Oculus Rift, Steam, Unity Editor, WebGL, WebPlayer, Windows Phone 8.0. "Strange is known NOT to run on these": PlayStation 4, Wii U, Windows Phone 8.1, Windows Store Apps, Xbox One.
- `package.json`: repo'da **yok** (git tree taraması). Unity 6 / IL2CPP: **hiç bahsedilmiyor** (❓).

### 3.3 Kurulum
- UPM / OpenUPM: **yok**. Repo'yu klonlayıp kaynak kopyalama ya da (README'de belirtilmeyen) Asset Store. ❓

### 3.4 Özellik envanteri (README, aynen)
- "Map as singleton, value or factory (get a new instance each time you need one)"
- "Name injections and/or supply specific implementations to specific consumer classes"
- "Perform constructor or setter injection"; "Tag your preferred constructor"; "Tag a method to fire after construction"; "Inject into MonoBehaviours"
- "Two shared event bus systems, EventDispatcher and Signals"; "Map events to Commands classes to separate business logic"
- "MonoBehaviour mediation"; "Optional MVCS (Model/View/Controller/Service) structure"
- "Multiple contexts ... Allow communication between contexts."
- "Promises"; "Annotated 'Implicit' Bindings"; "JSON-driven bindings"
- "Reflection binding dramatically reduces overhead of employing reflectivity"
- Lifetimes: singleton / value / factory (scoped yok); child scope → "Multiple contexts" (cross-context); source generator ❌; diagnostics window ❌; async ❌; reactive ❌; test helpers ❓.
https://github.com/strangeioc/strangeioc#readme

### 3.5 Performans iddiaları
Sayısal iddia **yok**. Yalnızca "super-lightweight" ve "Reflection binding dramatically reduces overhead".

### 3.6 API ergonomisi
README'de kod örneği yok; "The Big, Strange How-To" sayfasına link veriyor (http://strangeioc.github.com/strangeioc/TheBigStrangeHowTo.html). Bu sayfa bu araştırmada fetch edilmedi → ❓.

### 3.7 Adoption
Stars 1.610. İsimli oyun listesi README'de yok. OpenUPM paketi yok.

### 3.8 En çok yorumlanan açık issue'lar
1. 30 yorum — "windows store apps (phone 8.1 sdk)" (2015-04-21) https://github.com/strangeioc/strangeioc/issues/153
2. 15 yorum — "Duplicate Mediators" (2014-01-06) https://github.com/strangeioc/strangeioc/issues/71
3. 13 yorum — "Mediators for views that are never enabled are not cleaned up" (2014-10-24) https://github.com/strangeioc/strangeioc/issues/121

Yorum: Nexus'un MVCS yapısıyla en yakın akraba, fakat **2017'den beri ölü**. #71 ve #121 doğrudan Nexus'un mediator lifecycle alanı (ViewBinder) ile ilgili tarihsel ağrı noktaları.

---

## 4. Reflex

Repo: https://github.com/gustavopsantos/Reflex (README `.github/README.md` altında)

### 4.1 Sürüm / aktivite / lisans
- Son release: **14.4.0**, 2026-10-10 ("Adds CoreCLR support"; Debugger render order PR #150). Önceki: 14.3.1 (2026-06-18, "Fixes deprecated non generic TreeViewState and TreeViewItem on unity 6.5+"), 14.3.0 (2026-03-17, "Added factories auto injection"), 14.2.0 (2026-02-24), 14.1.0 (2026-01-25). https://github.com/gustavopsantos/Reflex/releases
- main son commit: **2026-10-10** (`70c9b4e`).
- Stars **1.634**, forks 116. Açık issue **6**, açık PR **4**. Lisans MIT. `archived: false`.

### 4.2 Unity minimum / Unity 6 / IL2CPP
- `Assets/Reflex/package.json`: `"unity": "2021.1"`, `"version": "14.4.0"`. https://raw.githubusercontent.com/gustavopsantos/Reflex/main/Assets/Reflex/package.json
- README badge: "Unity-2021+".
- Unity 6: package.json'da yok; release notlarında "unity 6.5+" fix (14.3.1) ve "CoreCLR support" (14.4.0).
- IL2CPP: README "**AOT Support:** Basically there's no runtime Emit, so it works fine on IL2CPP builds." Ancak "Scripting Restrictions" bölümü: `IEnumerable<T>` constructor injection + IL2CPP → "`System.ExecutionEngineException: Attempting to call method 'System.Linq.Enumerable::Cast<ANY-TYPE>' for which no ahead of time (AOT) code was generated.`" — `[Preserve]` workaround gerekiyor; "Reflex 4.0.0 had and assembly weaver ... But it was causing conflicts with projects using Burst. So it's being removed temporarly". https://github.com/gustavopsantos/Reflex#-scripting-restrictions
- Platform listesi (README): iOS, Android, Windows/Mac/Linux, PS4/PS5, Xbox One/S/X ve Series X/S, WebGL.

### 4.3 Kurulum (README)
- UPM git URL: `https://github.com/gustavopsantos/reflex.git?path=/Assets/Reflex/#14.4.0`
- OpenUPM: `openupm install com.gustavopsantos.reflex`
- `.unitypackage` (releases)
- Asset Store: bahsedilmiyor.

### 4.4 Özellik envanteri (README)
- Constructor injection ✅; `[Inject]` ile field / property / method injection ✅ ("Attribute injection is the way to go for **MonoBehaviours**")
- Lifetimes ✅ `Lifetime.Singleton / Scoped / Transient`; ayrıca `Resolution.Lazy / Eager` (instantiation zamanı)
- Child scope ✅: RootContainer → her sahne için SceneContainer; `parentContainer.Scope(builder => ...)`; parent override `ContainerScope.OnSceneContainerBuilding`
- Factories ✅ `RegisterFactory` (14.3.0: "factories auto injection")
- Lazy<T> injection ❌ (yalnızca `Resolution.Lazy`)
- Named / keyed ❌ — README açıkça: "Reflex does not support selective resolution natively, there are no `WithId` builder methods or `[Inject(Id = "FooId")]` attributes"; wrapper-type workaround öneriliyor
- MonoBehaviour / prefab injection ✅: `GameObjectInjector.InjectSingle/InjectObject/InjectRecursive/InjectRecursiveMany`, `GameObjectSelfInjector` component
- Scene scopes ✅ `ContainerScope` component ("As of version 8.0.0 Reflex has stopped automatically managing dependency injection for any scene.")
- Entry points (ITickable-style) ❌ — açık issue #116 "Should we consider adding lifecycle methods like Start and Update for pure C# class injection?" (2025-10-29)
- Source generator ✅ `[SourceGeneratorInjectable]` (tip `partial` ve `public` olmalı)
- Validation ❓ (`Container.Single<T>` tek binding doğrulaması var; editor-time graph validation yok)
- Debugger ✅ "Window → Analysis → Reflex Debugger ... CTRL + E"; `REFLEX_DEBUG` define gerekli; "debug mode reduces performance and increases memory pressure"
- Signal bus ❌; Reactive ❌; Async (UniTask/Awaitable) ❌ README'de yok
- Test helpers ❓ (repo'da `Assets/Reflex.EditModeTests` var; kullanıcıya yönelik helper docs yok)
- `ReflexSettings` ScriptableObject `Resources/` altında **zorunlu** ("ReflexSettings asset is obligatory to have")
- "Immutable Container: Performant thread safety free of lock"; "Contract Table: Allows usages of APIs like `container.All<IDisposable>`"

### 4.5 Performans iddiaları (README, aynen)
"**Fast:** up to 414% faster than VContainer, up to 800% faster than Zenject." / "**GC Friendly:** up to 28% less allocations than VContainer, up to 921% less allocations than Zenject."

Metodoloji: "Resolving ten thousand times a transient dependency with four levels of chained dependencies. See NestedBenchmarkReflex.cs" (`Assets/Reflex.Benchmark/`).

| Platform | Reflex | Zenject | VContainer |
|---|---|---|---|
| Android + Mono | 54.7 KB / 4.9ms | 503.9 KB / 34.4ms (921% / 702%) | 70.3 KB / 20.3ms (128% / 414%) |
| Android + IL2CPP | 140.6 KB / 4.0ms | 1000 KB / 15.8ms (711% / 395%) | 140.6 KB / 4.2ms (100% / 105%) |
| Windows + Mono | 140.6 KB / 0.7ms | 1000 KB / 5.6ms (711% / 800%) | 140.6 KB / 1.9ms (100% / 271%) |
| Windows + IL2CPP | 140.6 KB / 1.4ms | 1000 KB / 6.2ms (711% / 442%) | 140.6 KB / 3.0ms (100% / 214%) |

https://github.com/gustavopsantos/Reflex#-performance

**Ölçülmeyenler / belirsizlikler:** Unity sürümü, donanım, rakip kütüphane sürümleri, VContainer'ın source generator'ının açık olup olmadığı README'de **yok**. Yalnızca transient resolve; singleton/scoped, container build, scene injection ölçülmemiş. Android+IL2CPP'de VContainer ile fark yalnızca %5.

### 4.6 API ergonomisi — hello world (README "Getting Started", 23 adım)
```csharp
public class RootInstaller : MonoBehaviour, IInstaller
{
    public void InstallBindings(ContainerBuilder builder)
    {
        builder.RegisterValue("Hello"); // Note that values are always registered as singletons
    }
}

public class Greeter : MonoBehaviour
{
    [Inject] private readonly IEnumerable<string> _strings;
    private void Start() => Debug.Log(string.Join(" ", _strings));
}
```
Ek zorunlu adımlar: `Create → Reflex → RootScope` prefab, `Assets/Resources/ReflexSettings` asset, sahnede `Reflex > SceneScope`, installer'ları bu objelere ekleme. https://github.com/gustavopsantos/Reflex#-getting-started
Açık issue #112 "Greeter example in README is unclear" (2025-10-09) bu onboarding'in kafa karıştırdığını gösteriyor.

### 4.7 Adoption
- Stars 1.634.
- OpenUPM son 30 gün indirme: **150** (VContainer'ın ~1/237'si). OpenUPM'de 84 release build'i.
- README'de isimli oyun/stüdyo **yok**; Discord sunucusu var.

### 4.8 En çok yorumlanan açık issue'lar (toplam yalnızca 6 açık issue)
1. 2 yorum — "Should we consider adding lifecycle methods like Start and Update for pure C# class injection?" (2025-10-29) https://github.com/gustavopsantos/Reflex/issues/116
2. 2 yorum — "Greeter example in README is unclear" (2025-10-09) https://github.com/gustavopsantos/Reflex/issues/112
3. 1 yorum — "Reflex should include Samples in Samples~ folder" (2025-10-22) https://github.com/gustavopsantos/Reflex/issues/114

---

## 5. Diğer aktif Unity DI container'ları (GitHub search, stars sırası)

`search/repositories?q=unity+dependency+injection&sort=stars` ve `topic:dependency-injection+topic:unity` sonuçları (2026-10-15):

| Repo | Stars | Son push | Son release | Lisans | Not |
|---|---:|---|---|---|---|
| hadashiA/VContainer | 3.072 | 2026-10-06 | 1.19.0 (2026-07-01) | MIT | §1 |
| gustavopsantos/Reflex | 1.634 | 2026-10-10 | 14.4.0 (2026-10-10) | MIT | §4 |
| hadashiA/VitalRouter | 369 | 2026-07-14 | 2.8.0 (2026-07-11) | MIT | DI değil; "A fast, zero-allocation, in-memory messaging library. Declarative async pipeline with source generator for Unity and .NET." — VContainer yazarının messaging kütüphanesi; MessagePipe'a alternatif |
| CatLib/CatLib | 294 | 2021-04-12 | — | — | ölü |
| intentor/adic | 200 | 2021-08-09 | — | — | ölü |
| UniDi/UniDi | 122 | 2024-09-26 | yok (404) | NOASSERTION | Zenject fork'u; 1+ yıldır push yok |
| alexanderlarsen/Saneject | 92 | 2026-07-26 | 1.2.4 (2026-07-26) | MIT | "Dependency injection the Unity way" — yeni, küçük |
| Jeffan207/usyrup | 45 | 2026-01-26 | 1.5.1 (2026-01-26) | MIT | küçük |
| tariksavas/tarject | 36 | 2026-01-17 | v1.0.14 (2026-01-17) | MIT | küçük |

Unity MVC/MVCS araması (`q=unity+mvcs+framework`): en yüksek DevsDaddy/UIFramework 95 stars (son push 2024-04-25). StrangeIoC dışında **aktif ve benimsenmiş bir Unity MVCS framework'ü bulunamadı**. Bu, Nexus'un MVCS + DI birleşimi için boş bir alan olduğunu gösteriyor (yorum; veri yukarıdaki arama sonuçları).

Saneject, UniDi, usyrup, tarject için README/özellik envanteri **çıkarılmadı** (düşük adoption; yalnızca API metadata'sı alındı).

---

## 6. MessagePipe (Cysharp) — signal/event rakibi

Repo: https://github.com/Cysharp/MessagePipe

### 6.1 Sürüm / aktivite / lisans
- Son release **1.8.2**, 2026-06-08. master son commit 2026-10-07 (`47f036c`); `pushed_at` 2026-10-14.
- Stars **2.003**, forks 135. Açık issue **3**, açık PR **3**. Lisans MIT.

### 6.2 Unity minimum / IL2CPP
- `src/MessagePipe.Unity/Assets/Plugins/MessagePipe/package.json`: `"unity": "2018.4"`, `"version": "1.8.2"`.
- README Unity notu: "Unity version does not have open generics support(for IL2CPP) and does not support auto registration. Therefore, all required types need to be manually registered."
- Roslyn analyzer: "This analyzer can use after Unity 2020.2".
- Unity 6: README'de açıkça yok.

### 6.3 Kurulum (README "Unity")
- Core: `https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe`
- VContainer adaptörü: `...?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe.VContainer`
- Zenject adaptörü: `...?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe.Zenject`
- "requires UniTask to install, all `ValueTask` declaration in .NET is replaced to `UniTask`."
- OpenUPM `com.cysharp.messagepipe`: son 30 gün **1.406** indirme.
- `.unitypackage` (releases).

### 6.4 Özellikler (README, aynen)
"Dependency-injection first; Filter pipeline; better event; sync/async; keyed/keyless; buffered/bufferless; singleton/scoped; broadcast/response(+many); in-memory/interprocess/distributed". Unity'de `Window -> MessagePipe Diagnostics` penceresi (subscription leak izleme), `MessagePipeOptions.EnableCaptureStackTrace`. Roslyn analyzer "to prevent subscription leak". `AsObservable` ile UniRx köprüsü.
Zenject sınırı: "Zenject version is not supported `InstanceScope.Singleton` for Zenject's limitation. The default is `Scoped`, which cannot be changed."
Not: "If you are using Unity 2022.1 or later and VContainer 1.14.0 or later, you do not need `RegsiterMessageBroker<>`."

### 6.5 Performans iddiası (README, aynen)
"MessagePipe is faster than standard C# event and 78 times faster than Prism's EventAggregator." ve "Of course, memory allocation per publish operation is less(zero)." — grafikler görsel; metin metodolojisi **yok**. Benchmark kodu: `tests/MessagePipe.Benchmark/` (BenchmarkDotNet; `ZenjectCore.dll` dahil). https://github.com/Cysharp/MessagePipe#messagepipe
Ölçülmeyen: Unity/IL2CPP ölçümü README'de yok (benchmark .NET BenchmarkDotNet).

### 6.6 API (README VContainer örneği, kısaltılmış)
```csharp
protected override void Configure(IContainerBuilder builder)
{
    var options = builder.RegisterMessagePipe();
    builder.RegisterBuildCallback(c => GlobalMessagePipe.SetProvider(c.AsServiceProvider()));
    builder.RegisterMessageBroker<int>(options);
    builder.RegisterEntryPoint<MessagePipeDemo>(Lifetime.Singleton);
}
public MessagePipeDemo(IPublisher<int> publisher, ISubscriber<int> subscriber) { ... }
subscriber.Subscribe(x => Debug.Log("S1:" + x)).AddTo(d);
publisher.Publish(10);
```

### 6.7 En çok yorumlanan açık issue'lar
1. 2 yorum — "MessagePipe.Interprocess - Scope issue ... (ValidateScopes)" (2026-02-06) https://github.com/Cysharp/MessagePipe/issues/149
2. 0 yorum — "How to properly start NamedPipe Interprocess server" (2026-07-23) https://github.com/Cysharp/MessagePipe/issues/159
3. 0 yorum — "Inject] not working on (Strip Code Engine - High) with Unity VContainer" (2026-08-03) https://github.com/Cysharp/MessagePipe/issues/160

---

## 7. R3 ve UniRx (Cysharp / neuecc) — reactive model rakipleri

### 7.1 R3
Repo: https://github.com/Cysharp/R3 — "The new future of dotnet/reactive and UniRx."
- Son release **1.3.1**, 2026-05-19. main son commit 2026-10-08. Stars **4.019**, forks 188. Açık issue **25**, PR **9**. MIT.
- `src/R3.Unity/Assets/R3.Unity/package.json`: `"unity": "2021.3"`, `"version": "1.3.1"`. README: "The minimum Unity support for R3 is **Unity 2021.3**."
- Kurulum: iki adım — (1) NuGetForUnity ile `R3` NuGet paketi, (2) UPM git URL `https://github.com/Cysharp/R3.git?path=src/R3.Unity/Assets/R3.Unity` (`#1.0.0` gibi tag ile sabitlenebilir). OpenUPM `com.cysharp.r3`: son 30 gün **3.569** indirme.
- Unity 6 / 7: README: "Unity 6 (confirmed with 6000.6.3f1) is not affected." (Unity 7'deki `Microsoft.Bcl.AsyncInterfaces` sorunu bağlamında, issue #404). Unity 7 için ek adım gerekiyor ("Using R3 on Unity 7").
- Özellikler (README başlıkları): `ReactiveProperty`, `ReactiveCommand`, `ObservableTracker`, `UnhandledExceptionHandler`, `FromEvent`, Unity için PlayerLoop tabanlı `TimeProvider`/`FrameProvider`, `INotifyPropertyChanged to Observable`.
- En çok yorumlanan açık issue'lar: #334 "Null object reference in Godot ObservableTracker" (9), #340 "Add `GroupBy`" (6), #338 "ToReactiveProperty doesn't seem to exist" (6).

### 7.2 UniRx
Repo: https://github.com/neuecc/UniRx — **`archived: true`**.
- Son release 7.1.0, 2019-07-01. Son commit 2024-02-16. Stars **7.322**, forks 876. Açık issue **195**, PR **50**. MIT.
- README en üstte: "> [!IMPORTANT] I have started distributing an evolved version of UniRx in Cysharp/R3, please use it instead of UniRx."
- `Assets/Plugins/UniRx/package.json` yok (404). OpenUPM `com.neuecc.unirx` 7.1.0 (2020-02 build): son 30 gün **2.202** indirme (hâlâ R3'ün ~%60'ı).
- Asset Store: README "UniRx is available on the Unity Asset Store (FREE)".
- Zenject README'de "UniRx Integration" bölümü; VContainer docs'ta `integrations/unirx.mdx` var.
- En çok yorumlanan açık issue'lar: #300 "Add support for .net standard to project" (53), #152 "What game or library is using UniRx?" (19), #327 "Discussion: gitter vs Slack vs Discord" (18).

**Referans olarak UniTask:** OpenUPM `com.cysharp.unitask` son 30 gün **117.715** indirme — Unity ekosisteminde async standardı; VContainer ve MessagePipe buna entegre. (Yalnızca OpenUPM sayısı alındı; repo detayı çıkarılmadı.)

---

## 8. Karşılaştırma matrisi

✅ = birincil kaynakta açıkça var; ❌ = birincil kaynakta açıkça yok / yazar yok diyor; ❓ = doğrulanamadı.
Sütunlar: **VC** = VContainer 1.19.0, **ZJ** = Zenject 9.2.0 / Extenject 9.2.1 (aynı README), **SI** = StrangeIoC v0.6.1/"1.0.0", **RX** = Reflex 14.4.0, **MP** = MessagePipe 1.8.2 (DI değil; yalnızca ilgili satırlar), **R3** = R3 1.3.1 (reactive; yalnızca ilgili satırlar).

| Özellik | VC | ZJ | SI | RX | MP | R3 |
|---|:-:|:-:|:-:|:-:|:-:|:-:|
| Constructor injection | ✅ | ✅ | ✅ | ✅ | — | — |
| Method injection | ✅ | ✅ | ✅ ("Tag a method to fire after construction") | ✅ | — | — |
| Field injection | ✅ | ✅ | ❓ ("setter injection") | ✅ | — | — |
| Property injection | ✅ | ✅ | ✅ (setter) | ✅ | — | — |
| Singleton lifetime | ✅ | ✅ (`AsSingle`) | ✅ | ✅ | ✅ | — |
| Scoped lifetime | ✅ | ✅ (`AsCached`) | ❌ | ✅ | ✅ | — |
| Transient lifetime | ✅ | ✅ (`AsTransient`) | ✅ (factory) | ✅ | ❌ | — |
| Child scope / hiyerarşi | ✅ | ✅ (Sub-Containers) | ✅ (Multiple contexts) | ✅ | — | — |
| Factories | ✅ (`Func<>`) | ✅ | ✅ | ✅ | — | — |
| Lazy<T> injection | ❌ (#519 açık) | ✅ (`LazyInject<>`) | ❌ | ❌ | — | — |
| Named / keyed bindings | ✅ (`.Keyed`, `[Key]`) | ✅ (`WithId`) | ✅ ("Name injections") | ❌ (açıkça yok) | ✅ (keyed) | — |
| MonoBehaviour / prefab injection | ✅ | ✅ | ✅ | ✅ | — | — |
| Scene scopes | ✅ | ✅ (SceneContext, scene parenting) | ✅ (contexts per scene) | ✅ (ContainerScope) | — | — |
| Entry points (ITickable-style) | ✅ (11 interface) | ✅ (ITickable/IInitializable/IDisposable) | ❌ | ❌ (#116 açık) | — | — |
| Source generator / AOT codegen | ✅ (Roslyn) | ❌ (Reflection Baking = IL weaving) | ❌ | ✅ (Roslyn) | ❌ | — |
| Editor-time validation | ❌ | ✅ | ❌ | ❌ | — | — |
| Editor window / debugger | ✅ (Diagnostics Window) | ❓ (README'de dedicated window yok) | ❌ | ✅ (Reflex Debugger) | ✅ (MessagePipe Diagnostics) | ✅ (ObservableTracker) |
| Signal bus / messaging | ❌ (MessagePipe entegrasyonu) | ✅ (SignalBus) | ✅ (EventDispatcher + Signals) | ❌ | ✅ | — |
| Reactive / observable model | ❌ (UniRx entegrasyonu) | ❌ (UniRx entegrasyonu) | ❌ | ❌ | ❌ (`AsObservable` köprüsü) | ✅ |
| Async (UniTask / Awaitable) | ✅ (`IAsyncStartable`: UniTask veya Awaitable) | ❌ | ❌ (Promises var) | ❌ | ✅ (UniTask zorunlu) | ✅ |
| Test helpers | ❓ | ✅ ("unit test, integration tests, and scene tests") | ❓ | ❓ | — | — |
| MVCS / mediator yapısı | ❌ | ❌ | ✅ | ❌ | — | — |
| Memory pool | ❌ | ✅ | ❌ | ❌ | — | — |
| Decorator bindings | ❌ (#157 açık) | ✅ | ❌ | ❌ | — | — |
| IL2CPP açıkça belirtilmiş | ✅ | ✅ (gotchas ile) | ❓ | ✅ (IEnumerable<T> kısıtı ile) | ✅ (open generics yok) | ❓ |
| `package.json` `unity` alanı | 2018.4 | 2019.3 (Extenject Source/) | yok | 2021.1 | 2018.4 | 2021.3 |
| Unity 6 `package.json`'da açıkça | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ (README: "Unity 6 ... not affected") |
| UPM git URL | ✅ | ❌ (branch + 3rd-party ext.) | ❌ | ✅ | ✅ | ✅ (+NuGet) |
| OpenUPM | ✅ (35.556/ay) | ✅ (3. fork, 920/ay) | ❌ | ✅ (150/ay) | ✅ (1.406/ay) | ✅ (3.569/ay) |
| Asset Store (README'de) | ❌ | ✅ | ❓ | ❌ | ❌ | — |
| Sayısal benchmark yayınlıyor | ✅ | ❌ | ❌ | ✅ | ✅ (grafik) | ❓ |

---

## 9. Özet gözlemler (yalnızca yukarıdaki olgulara dayanan yorumlar)

1. **Canlı DI rakipleri sadece iki tane:** VContainer (2026-10-06 commit, 1.19.0) ve Reflex (2026-10-10 commit, 14.4.0). Zenject/Extenject/StrangeIoC release olarak 2021 öncesinde durmuş.
2. **Adoption uçurumu:** OpenUPM aylık indirme VContainer 35.556 vs Reflex 150 vs Extenject 920. Stars: VContainer 3.072, Zenject 2.940, Reflex 1.634, StrangeIoC 1.610.
3. **Performans iddiaları birbirini çürütmüyor ama tutarlı da değil:** VContainer "5-10x faster than Zenject" (Unity 2019.x / IL2CPP macOS, sürüm/tarih yok); Reflex "up to 414% faster than VContainer" (platform var, Unity sürümü / rakip sürümü yok). Android+IL2CPP'de Reflex-VContainer farkı yalnızca %5. Hiçbiri Unity 6'da ölçüm yayınlamıyor. Nexus için fırsat: tarihli, sürümlü, Unity 6 + IL2CPP, tekrar üretilebilir benchmark.
4. **Hiçbir aktif DI container MVCS/mediator/signal katmanı sunmuyor;** VContainer ve Reflex messaging'i MessagePipe/VitalRouter'a, reactive'i R3'e bırakıyor. Signal bus'ı olan tek DI'lar ölü olanlar (Zenject, StrangeIoC).
5. **Rakiplerin kendi issue tracker'larındaki talepler:** VContainer'da "simple registration", Decorator, Lazy inject; Reflex'te pure-C# lifecycle (ITickable benzeri), README netliği, Samples~ klasörü; Zenject'te UPM. Bunlar Nexus'un "clarity / ease of use" hedefi için doğrudan kontrol listesi.
6. **Onboarding yükü:** Reflex hello-world 23 adım + zorunlu `Resources/ReflexSettings`; Zenject SceneContext + MonoInstaller + inspector listesi; VContainer tek `LifetimeScope` subclass'ı. Nexus'un hello-world adım sayısı bununla kıyaslanmalı.
7. **Unity 6 desteğini hiçbir rakip `package.json`'da deklare etmiyor;** yalnızca Unity 6.x deprecation fix'leri var. Nexus `"unity": "6000.0"` gibi açık bir alanla ayrışabilir.
