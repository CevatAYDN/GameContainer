# iOS port rehberi

Bir Nexus oyununu App Store'a tasimak icin, Wickling'in ilk gonderiminden (2026-11-15 ile 2026-11-20, issue #436) yazildi; siradaki oyun bir hafta degil birkac gun alsin diye. Nexus oyunlarinin ortak yigini varsayilir: Unity 6000.6, Unity Ads'li LevelPlay, Unity IAP, External Dependency Manager, bazi oyunlarda Firebase.

> **Durum.** iOS App Store kit'i (`com.nexus.core` icinde `Editor/Ios`, `Nexus.Editor.Ios`) Unity ve `dotnet` olmadan yazildi: icindeki hicbir sey derlenmedi ya da calistirilmadi. Saf kurallarinin host harness testleri (`FullReviewIosKitTests`) var ve onlar da calismadi. Kit ile hicbir Xcode projesi disa aktarilmadi, hicbir arsiv kontrol edilmedi. Bir oyunun ilk disa aktarimini kanit sayin ve asagidaki kontrollerle bir kez elle karsilastirin.

Bekleme suresi olan adimlar (Apple, LevelPlay) ilk gun, kodla yan yana baslar.

## 1. gun: hesaplar (hesap sahibi, kodla paralel)

1. **App Store Connect uygulama kaydi**: bundle id Play paket id ile ayni, bir SKU, bir ana dil. Oyun basina bir kayit.
2. **Paid Apps Agreement** (Business): tuzel kisilik, banka, vergi. Banka ve vergi bilgisini yalnizca hesap sahibi girer. Anlasma Active olana kadar hicbir yerde IAP fiyati yuklenmez; sandbox'a yaklasik bir gun sonra ulasti.
3. **AB DSA ticaret erbabi beyani** (Business > Compliance): Ingilizce adres belgesi; "In Review" gonderimi engellemez.
4. **Sandbox test kullanicisi** (Users and Access > Sandbox): Apple Account olmayan bir adres (`+sandbox1` takma adi isler). Test kullanicisi telefonda yalnizca satin alma ekrani isteyince oturum acar.
5. **LevelPlay iOS uygulamasi**: iOS uygulamasini ekleyin, sonra Setup > Instances > Unity Ads: Game ID = Unity Ads **Apple** Game ID (Google olan degil) ve her format icin Unity placement id'li bir instance. Bos bir Unity Ads instance'i, Rewarded altinda onay isareti olmayan ag olarak gorunur; yeni hesapta tek dolum Unity Ads'tir.
6. **App Review iletisim bilgisi**: surum sayfasinda gercek ad, ulke kodlu telefon ve e-posta.

## Kit: projede ne acilir

Tek bir ayar varligi olusturun: **Assets > Create > Nexus > iOS App Store Kit** (`IosKitSettings`). Varlik yoksa post-process hicbir sey yapmaz.

| Ayar | Sonuc |
|---|---|
| `declaresTracking` | Oyunun App Store etiketindeki beyani. **False** (varsayilan; Nexus ads paketinin varsayimi, takip izni hic istemez) `NSUserTrackingUsageDescription` anahtarini kaldirir, `NSPrivacyTracking`, takip alan adlarini ve takip veri turlerini reddeder, gemi kontrolunun takip metnini reddetmesini saglar. True, Player Settings'te metni zorunlu kilar. |
| `languages` | `CFBundleLocalizations` ve gorunen adi olan her kayit icin `<kod>.lproj/InfoPlist.strings` (Xcode projesine eklenir). |
| `developmentRegion` | Plist'te yoksa `CFBundleDevelopmentRegion`. |
| `dataTypes` | `PrivacyInfo.xcprivacy`'nin `NSPrivacyCollectedDataTypes` listesi: App Store gizlilik etiketindeki liste, her biri amaclariyla. |
| `accessedApis` | `NSPrivacyAccessedAPITypes`: neden kodlariyla gerekli-neden API'leri. |
| `podsMinimumIos` | Her pod'u o iOS surumune derleyen Podfile `post_install` (bir kez; oyunun kendi `post_install`'i varsa dongu onun icine girer). |

Post-process (`IosPostProcess`, sira 950: paketlerinkinden sonra, oyunun 999'undan once) ayrica oyun belirlemediyse `ITSAppUsesNonExemptEncryption` false'u, urun adindan `CFBundleDisplayName`'i ve her disa aktarimda `PrivacyInfo.xcprivacy`'yi yazar (eklemeli bir Xcode projesi eskisini tutardi). Gecersiz bir gizlilik bildirimi ya da metni olmayan takip-beyanli oyun **build'i bozar**.

### Gizlilik manifesti ve ITMS-91054

`NSPrivacyAccessedAPIType` bir **kategori** alir (`NSPrivacyAccessedAPICategoryUserDefaults`, `...FileTimestamp`, `...SystemBootTime`, `...DiskSpace`, `...ActiveKeyboards`), asla bir tur adi almaz; Apple yanlis degeri yalnizca yuklemeden sonra hesap sahibine e-postayla bildirir (ITMS-91054, App Store Connect'te "Invalid Binary"). Her kategori `CA92.1` bicimli en az bir neden kodu ister. Kit kategoriyi bes sabite, nedeni bicime karsi kontrol eder; Apple'in neden kodu listesine karsi degil: kit yazilirken o liste okunamadi. Kodlari Apple'in "Describing use of required reason API" sayfasindan alin ve manifesti oyunun ve SDK'larinin kullandigiyla esit tutun. Veri turu ve amac sabitleri yalnizca `NSPrivacyCollectedDataType` / `NSPrivacyCollectedDataTypePurpose` oneklerine gore kontrol edilir. Manifest, App Store gizlilik etiketi ve gizlilik politikasi sayfasi birlikte degisir.

Firebase'in acilista "toplama kapali" anahtarlarini kit degil `com.nexus.firebase` yazar (issue #434).

### Simulator build (magaza ekran goruntuleri)

`Nexus.Editor.Ios.IosSimulatorBuild.Build(yol)` (ya da `-executeMethod Nexus.Editor.Ios.IosSimulatorBuild.BuildCli -iosSimulatorOutput <klasor>`) etkin sahneleri `iOSSdkVersion.SimulatorSDK` ile, her zaman Development build olarak derler (Unity 6000.6 yalnizca development Simulator calisma zamani ile gelir) ve sonra SDK ile development ayarlarini geri koyar. Simulator mimarisini ayarlamaz (Apple silicon varsayilan olarak arm64 derler). Yakalama: `xcrun simctl io <udid> screenshot --mask ignored`.

### Gemi kontrolleri (arsivden sonra)

```
Unity -batchmode -nographics -quit -projectPath <proje> -executeMethod Nexus.Editor.Ios.IosShipCheckCli.Run \
  -iosArchive <Oyun.xcarchive> -iosProject <disa aktarilan Xcode klasoru> -iosLastBuild <son yuklenen build> -iosRequireClass ISUnityAdsAdapter
```

Cikis 0 tum kontroller gecti, 1 bir kontrol basarisiz (disa aktarmayin), 2 calistirilamadi. Yalnizca macOS (`plutil`, `xcodebuild`). Uretmesi gereken ayarlari degil, arsivin kendisini okur:

- `ITSAppUsesNonExemptEncryption` false;
- `NSUserTrackingUsageDescription` yalnizca takip beyan edildiyse var;
- projede Firebase SDK'si varsa `FIREBASE_ANALYTICS_COLLECTION_ENABLED` ve `FirebaseCrashlyticsCollectionEnabled` false;
- `PrivacyInfo.xcprivacy` uygulama paketinde ve gecerli;
- UnityFramework `OTHER_LDFLAGS` icinde `-ObjC` var (`com.nexus.ads.levelplay` ekler, issue #435);
- her `-iosRequireClass` adi `UnityFramework` ikilisinde var (LevelPlay adaptorlerini sinif adiyla olusturur; her `Assets/LevelPlay/Editor/IS*AdapterDependencies.xml` icin bir `IS*Adapter` verin);
- `CFBundleVersion`, `-iosLastBuild` degerinden buyuk: bir kez yuklenen build numarasi harcanmistir.

## Kod kontrol listesi

| Adim | Nerede |
|---|---|
| iOS build hedefi, bundle id, team id ortamdan (asla git'te degil), otomatik imzalama | oyunun build betigi |
| Manifest'te `com.unity.external-dependency-manager` (olmazsa Podfile bos kalir) | `Packages/manifest.json` |
| UnityFramework'te `-ObjC` | `com.nexus.ads.levelplay` (#435) |
| Info.plist, gizlilik manifesti, yerellestirme, Pods minimumu | kit, yukarida |
| Acilista Firebase toplama kapali | `com.nexus.firebase` (#434) |
| AdMob/UMP olmadan onay | ads paketinin oyun cizimli onay akisi (#430) |
| iOS build'inde uygulama ikonu seti, yoksa Unity logosu gider | oyun |
| Tabletler: UI Toolkit panel olcegi telefon referansindan **Expand**; Shrink telefonlari bozar | oyunun PanelSettings'i |
| Her yukleme icin iPhone `buildNumber` artirilir | `ProjectSettings` |

## Yukleme oncesi sert kapi: telefonda

Gercek cihazda release build'in temiz kurulumu:

- log (`idevicesyslog -p <App>`) analitigin onay cevabina kadar bekletildigini, sonra gonderildigini gosterir;
- gercek metinli onay ekrani, Kabul;
- **gercek bir odullu reklam oynar** ("reklam yok" bir kurulum eksigidir: 1. gun 5. adim ya da eksik adaptor, asla "uygulama henuz yayinda degil" degil);
- IAP fiyati yuklenir, sandbox satin alma calisir ve kaldirip yeniden kurduktan sonra **Satin alimlari geri yukle** onu geri getirir.

Unity 6000.6'da release build Simulator'u hedefleyemez; release davranisini telefonda kontrol edin. Kitin kontrolleri bu kapinin yerini tutmaz ve bunlarin hicbiri henuz kit uzerinden calistirilmadi.

## App Store Connect (liste)

- **Ekran goruntuleri**: gerekli iPhone yuvasi **6.3"** (1206 x 2622), uygulama iPad'de calisiyorsa ayrica iPad 13" (2064 x 2752).
- **Ad** 30 karakter, alt baslik 30, anahtar kelimeler 100 **bayt** (ASCII disi harfler 2 sayilir). Her yerellestirmenin kendi metni ve ekran goruntusu gerekir.
- **Yas derecelendirmesi**, fiyatlandirma, test edilmedikce Mac ve Vision Pro kapali.
- **IAP (tuketilmeyen)**: dil basina gorunen ad ve aciklama sinirlari; satin alma ekraninin **fiyat gorunurken** inceleme ekran goruntusu; dugme ve Restore'un yerini soyleyen inceleme notlari.
- **Ilk IAP bir surumle gonderilir**: surum sayfasinda IAP bolumu yoktur. IAP sayfasinda "Add for Review", sonra surum sayfasinda ayni taslak gonderimi secip gonderin.
- **Manuel yayin**, boylece onaydan sonraki adimlar once calisir (Unity Ads Apple Store ID, LevelPlay uygulamasi Live).

## Atlanacak zaman kayiplari

- ITMS-91054 (yukarida): manifesti her disa aktarimda yazin, gemi kontrolu arsivden okusun.
- Unity bir hafta istek almayan placement'lari arsivler; yeni bir iOS reklam birimi ve placement ekleyip id'sini LevelPlay instance'inda kullanin.
- LevelPlay test modu reklami release icin kanit degildir.
- Unity panosundaki "missing SKAdNetwork IDs" uyarisi kendi tum listesini gosterir; build ile karsilastirin, onay degil gelir meselesidir.
