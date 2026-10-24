> **Kimin için:** `com.nexus.ads` ile oyun yayınlayan herkes. Bir sağlayıcıyı etkinleştirmeden önce okuyun.

# Politika uyum kaydı

Paketin bağlanabildiği her platformun yayıncının uyması gereken politikaları var. Bu kayıt platform
başına resmi politika sayfasını, okunduğu günü, ödüllü ve geçiş reklamı olan bir mobil oyuna uygulanan
kuralları ve her kuralın pakette nerede karşılandığını listeler. Platformun güncel resmi metni her zaman
bu dosyanın önündedir; buradaki bir kural ile sayfa çelişirse bu depoda issue açın.

Aşağıdaki her sayfanın okunma tarihi: **2026-10-24**. İngilizce aslı:
[POLICY_COMPLIANCE.md](POLICY_COMPLIANCE.md).

## Bir kural nasıl karşılanır

| İşaret | Anlamı |
|---|---|
| **Kod** | `PolicyGuard` (`Nexus.Ads.Core` içinde) çağrıyı çalışma anında engeller. Engel kapatılamaz; yer ve uzaktan gelen değerler korumanın alt sınırlarına kırpılır. Her engel bir `PolicyViolation` olayı yayar. |
| **Doğrulayıcı** | `AdsValidator` (editör, `RunCli`, pre-build kancası) projenin ayarlarından ve bir proje sondasından kontrol başına `pass`, `warn` ya da `fail` raporlar; parantezdeki kontrol kimlikleri paket README'sindekilerdir. |
| **Geliştirici** | Kodla zorlanamaz; kurulum sihirbazının kontrol listesinde ve burada yer alır. |
| **SDK** | Reklamı mediation ya da ağ SDK'sı çizer ve davranışına (kapatma düğmesi, sayaçlar) sahiptir; paket reklam görünümüne hiç dokunmaz. |

**Paket** sütunundaki kural adları, Aşama 2 kodunun ve testlerinin kullandığı `PolicyGuard` kural
kimlikleridir (issue #10).

## Google AdMob

Okunan sayfalar: [AdMob ve AdSense program politikaları](https://support.google.com/admob/answer/6128543),
[Davranış politikaları](https://support.google.com/admob/answer/2753860),
[Geçersiz trafik](https://support.google.com/admob/answer/3342054),
[İzin verilmeyen geçiş reklamı uygulamaları](https://support.google.com/admob/answer/6201362),
[Önerilen geçiş reklamı uygulamaları](https://support.google.com/admob/answer/6201350),
[Ödül sunan reklam politikaları](https://support.google.com/admob/answer/7313578),
[AdMob ile Google Play Aileler politikasına uyum](https://support.google.com/admob/answer/6223431),
[Reklam kimliği izni](https://support.google.com/admob/answer/11402075),
[Test reklamları (Unity)](https://developers.google.com/admob/unity/test-ads).

| Kural (resmi ifade, kısaltılmış) | Paket |
|---|---|
| "Publishers may not click their own ads"; "Testing your own ads by clicking on them is not allowed." (Yayıncı kendi reklamına tıklayamaz; tıklayarak test yasak.) | **Geliştirici**: canlı reklama asla dokunma. **Kod** `TestModeRequired`: development build yalnızca Google'ın örnek birimlerini yükler; **Doğrulayıcı** Google'ın örnek kimliklerinden farklı bir Test sekmesini (`keys.test.official`) ve onları taşıyan bir Release sekmesini (`keys.release.testdata`) durdurur. |
| "If a publisher opens additional accounts, the accounts will be flagged as duplicates." (Ek hesap açılırsa kopya sayılır.) | **Geliştirici**: tek AdMob hesabı. |
| Geçersiz trafik: yayıncının kendi canlı reklamına tıklaması, tıklamaya teşvik; "it is your responsibility as the publisher to ensure that the traffic on your ads is valid" (trafiğin geçerliliği yayıncının sorumluluğudur). | **Geliştirici**: kimseden tıklama isteme; canlı birimli build'i test edenlere verme. **Kod** `UnregisteredDevice` (LevelPlay/MAX satırlarına bakın: development build listede olmayan cihazda canlı reklam istemez). |
| "Do not place interstitial ads on app load and when exiting apps" (açılışta ve çıkışta geçiş reklamı yok); geçiş reklamı yalnızca içerik sayfaları arasında; arka planda reklam çalışamaz. | **Kod** `InterstitialNaturalBreakOnly` (`NaturalBreak` işaretsiz yerde geçiş reklamı gösterilemez), `NoAdInBackground`. |
| "You should place no more than one interstitial ad after every two user actions" (her iki kullanıcı eyleminde en fazla bir geçiş reklamı); bir öncekinin kapanmasının hemen ardından yok; her tıklamada yok. | **Kod** `InterstitialFrequency` (geçiş reklamları arası en az süre, ilk gösterim gecikmesi, oturum ve gün başına üst sınır; oyun kendi geçiş reklamını eklemez). |
| Reklam, kullanıcı bir işe dalmışken (oyun oynarken) aniden çıkamaz. Önerilen: "Sonraki seviye" düğmesinden önce, önceden yüklenmiş. | **Kod** `InterstitialNaturalBreakOnly`; başlatma sonrası ve her gösterimden sonra önceden yükleme denetleyicinin varsayılanıdır. |
| Ödüllü reklam "must only be served after a user affirmatively and unambiguously opts in" (yalnızca açık kullanıcı onayıyla); gereken eylem ve ödül açıkça gösterilir; ödül tamamlanınca verilmek zorundadır. | **Kod** `RewardedOnRequestOnly` (kullanıcı isteği olmadan ödüllü gösterim yok; ödül bir kez ve yalnızca SDK'nın ödül callback'inde). **Geliştirici**: düğme ödülü ve miktarı yazar. |
| Yenileme aralığı SDK'nın izin verdiği aralığın dışına çıkamaz. | **Kod** `NoAutomaticRequests` (istekler yalnızca önceden yükleme ve yeniden deneme politikasından gelir; yenileme döngüsü yok). |
| Aileler: çocukları içeren uygulamalar "must use a Google Play Families self-certified ads SDK or mediation platform"; mediation isteklerinde yaş işlemi CHILD ve içerik derecesi G. | **Kod** `ChildSettingsForwarded` (`AdsSettings`'teki tek çocuk bayrağı ve içerik derecesi her sağlayıcıya iletilir). **Doğrulayıcı** çocuklara yönelik uygulamada Google'ın öz-sertifikalı listesinde olmayan ya da çocuk uygulamasını yasaklayan bir ağın adapter'ı varsa uyarır (`child.networks`, adapter paket ve klasör adlarından algılanır; aşağıdaki tablo). |
| AD_ID: GMA SDK 20.4.0+ izni otomatik bildirir; Android 13 hedefleyen uygulamalar için Play bildirimi zorunlu kılar. | **Doğrulayıcı** Android 13+ hedefte özel `AndroidManifest.xml` `com.google.android.gms.permission.AD_ID` taşımıyorsa uyarır (`android.adid`); birleşik manifest yalnızca build'den sonra var olduğundan SDK'ların kendi bildirimleri ona görünmez. |
| Test reklamları: cihaz "RequestConfiguration.TestDeviceIds" ile kaydedilir; yayından önce kaldırılır; test modunda olmadan çok reklama tıklamak hesabın işaretlenmesine yol açar. | **Kod** `TestModeRequired`: AdMob sağlayıcısı `RequestConfiguration.TestDeviceIds` değerini yalnızca Test sekmesinden alır; **Doğrulayıcı** örnek birimli Release sekmesini durdurur (`keys.release.testdata`); test cihazları yalnızca Test sekmesinde yaşar ve `ProviderGate` cihaz taşıyan bir release yapılandırmasını reddeder (`TestModeRequired`). |

AdMob için yayımlanmış tek test verisi Google'ın örnek kimlikleridir; geldikleri sayfayla birlikte
`Editor/PlatformCatalog.json` içinde durur. Uygulama kimlikleri: Android
`ca-app-pub-3940256099942544~3347511713`, iOS `ca-app-pub-3940256099942544~1458002511`
([Android hızlı başlangıç](https://developers.google.com/admob/android/quick-start),
[iOS hızlı başlangıç](https://developers.google.com/admob/ios/quick-start)).

## Google Play

Okunan sayfalar: [Reklam politikası](https://support.google.com/googleplay/android-developer/answer/9857753),
[Aileler politikası](https://support.google.com/googleplay/android-developer/answer/9893335),
[Aileler öz-sertifikalı reklam SDK'ları](https://support.google.com/googleplay/android-developer/answer/9283445),
[Reklam kimliği](https://support.google.com/googleplay/android-developer/answer/6048248),
[Uygulamanızı incelemeye hazırlayın (Uygulama içeriği)](https://support.google.com/googleplay/android-developer/answer/9859455).

| Kural (resmi ifade, kısaltılmış) | Paket |
|---|---|
| Beklenmedik tam ekran geçiş reklamları yasak; seviye ya da içerik başında reklam yasak; açılış ekranından önce tam ekran reklam yasak. | **Kod** `InterstitialNaturalBreakOnly`, `InterstitialFrequency` (ilk gösterim gecikmesi). |
| 15 saniye sonra kapatılamayan tam ekran geçiş reklamı yasak; "This policy does not apply to rewarded ads which are explicitly opted-in by users." (kullanıcının açıkça seçtiği ödüllü reklamlar hariç). | **SDK** (kapatma düğmesi mediation'ındır); **Kod** `NoAdViewInterference` paketin reklamın üstüne çizmesini ya da boyutunu değiştirmesini engeller. |
| Geçiş reklamını tekrar tekrar gösteren uygulamalar yasak; dokunma ve kaydırmaların ardından art arda geçiş reklamı ihlaldir. | **Kod** `InterstitialFrequency`. |
| Kullanıcı reklama tıklamaya zorlanamaz; reklamlar cezasız kapatılabilmeli; hangi uygulamanın reklam gösterdiği belli olmalı. | **Geliştirici** (arayüz tasarımı); **Kod** `NoAdViewInterference`. |
| "Ad fraud is strictly prohibited." (Reklam sahtekârlığı kesinlikle yasaktır.) | **Geliştirici**; **Kod** `UnregisteredDevice`, `TestModeRequired`. |
| Reklam kimliği yalnızca reklam ve analitik için; Android 13 hedefleyen uygulamalar manifest'te `com.google.android.gms.permission.AD_ID` iznini bildirmek zorunda. | **Doğrulayıcı** (`android.adid`, yalnızca özel manifest); **Geliştirici**: Play Console reklam kimliği beyanı. |
| Uygulama içeriği: reklam içerip içermediği, hedef yaş grubu, gizlilik ve güvenlik uygulamaları (Data safety) beyan edilir. | **Geliştirici**: Play Console beyanları; sihirbazın kontrol listesi üçünü de adlandırır. |
| Aileler: yalnızca çocuklara yönelik uygulamalar yalnızca öz-sertifikalı SDK sürümlerini kullanır; karma kitle "nötr yaş ekranı" ister; çocuklara kişiselleştirilmiş reklam yok; oyunu kesen ödüllü/opt-in reklam 5 saniyede kapatılabilmeli; açılışta geçiş reklamı yok; sayfada birden çok reklam yeri yok. | **Kod** `ChildSettingsForwarded`, `InterstitialNaturalBreakOnly`, `NoStackedAds`; **Doğrulayıcı** `child.mediation`, `child.networks`; **Geliştirici** yaş ekranı ve içerik. |

Okunma tarihinde Google'ın Aileler öz-sertifikalı reklam SDK listesi (LevelPlay ve Liftoff eski
adlarıyla geçiyor):

| Ağ | Listede | Google'ın satırı |
|---|---|---|
| Google AdMob | evet | `play-services-ads` 19.0.0 ve üstü |
| Unity LevelPlay (ironSource) | evet | `mediationsdk` 7.2.1 ve üstü |
| Unity Ads | evet | `unity-ads` 4.0.1 ve üstü |
| Liftoff Monetize (Vungle) | evet | `publisher-sdk-android` 6.10.4 ve üstü |
| DT Exchange | evet | `marketplace-sdk` 8.2.1 ve üstü |
| InMobi | evet | `inmobi-ads` 10.5.5 ve üstü |
| Chartboost | evet | `chartboost-sdk` 9.1.1 ve üstü |
| AppLovin MAX | hayır | "AppLovin has left the Families Self-Certified Ads SDK Program." (AppLovin programdan ayrıldı.) |
| Mintegral | hayır | listede yok |
| Pangle | hayır | listede yok |
| Meta Audience Network | hayır | listede yok |

Google ayrıca programın "is not currently accepting new applicants" (şu an yeni başvuru almıyor)
olduğunu belirtiyor. Çocukları hedefleyen bir oyun bu yüzden MAX'i mediation olarak kullanamaz ve
Mintegral, Pangle ve Meta'yı mediation gruplarının dışında tutmak zorundadır; doğrulayıcı bu
bileşimde uyarır.

## AppLovin MAX

Okunan sayfalar: [Yayıncı Politikaları](https://legal.applovin.com/policies-publishers/) (sayfa tarihi
2026-09-22), [Yayıncı içerik politikalarına giriş](https://legal.applovin.com/introduction-to-applovins-publisher-content-policies/),
[MAX panel en iyi uygulamalar](https://support.applovin.com/en/max-dashboard/best-practices),
[Unity entegrasyonu](https://support.applovin.com/en/max/unity/overview/integration),
[Test modu (Unity)](https://support.applovin.com/en/max/unity/testing-networks/test-mode).

| Kural (resmi ifade, kısaltılmış) | Paket |
|---|---|
| "Do not mediate MAX in an internal or third-party mediation." "The AppLovin Publisher Policies do not authorize this use of MAX." "It will lead to account suspension." (MAX'i dahili ya da üçüncü taraf mediation içinde çalıştırma; politikalar izin vermez; hesap askısına yol açar.) Politikalar: AppLovin'e "only through a single mediation integration" (tek mediation entegrasyonuyla) erişilir. | **Kod** `SingleMediation` (oturum başına tek sağlayıcı; ikinci mediation SDK'sı başlatılamaz). **Doğrulayıcı** projede iki mediation SDK'sı varsa durdurur (`mediation.single`; durum rozeti Invalid der). Pakette tasarım gereği mediation'lar arası şelale yok. |
| Test ederken kendi reklamına tıklama, gösterim ve tıklamayı yapay şişirme; gösterim ve tıklamalar gerçek insan etkinliğinden gelmeli. | **Geliştirici**; **Kod** `UnregisteredDevice`, `TestModeRequired`. |
| "Minimize the number of active AppLovin accounts that you use." (Etkin AppLovin hesaplarını en aza indir.) | **Geliştirici**: tek hesap. |
| Reklamlar doğal olmayan dikkat çekmemeli, yanıltmamalı, geçersiz tıklamaya yol açacak yerlerde olmamalı; "not hidden or invisible, out of page, stacked, or stuffed" (gizli, görünmez, sayfa dışı, üst üste, tıkıştırılmış olmamalı). | **Kod** `NoStackedAds`, `NoAdViewInterference`; **Geliştirici** yerleşim. |
| AppLovin'in standart reklam şablonlarına ve reklamın çizimine müdahale yasak. | **Kod** `NoAdViewInterference`. |
| "Çocuk" sayılan kullanıcı için AppLovin SDK'sı başlatılamaz; yalnızca çocuklara yönelik uygulamalar kısıtlıdır. | **Doğrulayıcı** çocuklara yönelik `AdsSettings` ile MAX seçiliyse durdurur (`child.mediation`; MAX Google'ın Aileler listesinde de yok). |
| Test modu: `MaxSdk.SetTestDeviceAdvertisingIdentifiers(...)` SDK başlatılmadan önce; log satırı "Test Mode On: true"; test reklamlarında gösterim, tıklama ve gelir izlenmez. Örnek SDK anahtarı ya da test birimi yayımlanmaz; test reklamları hesabın kendi anahtarı ve birimleriyle verilir. | **Kod** `TestModeRequired`, `UnregisteredDevice`: development build Test sekmesinin cihaz listesini başlatmadan önce verir; listede olmayan cihazda SDK yerine simülatör sağlayıcı çalışır. **Doğrulayıcı** cihaz listesi boşsa uyarır (`devices.empty`: her development build simülatör çalıştırır) ve Release sekmesini yansıtmayan Test sekmesini durdurur (`keys.test.mirror`); release yapılandırması hiçbir zaman cihaz taşımaz (`ProviderGate`, `TestModeRequired`). |
| Yaptırım: reklamların engellenmesi ya da kısıtlanması, hesap askısı, ödemelerin tutulması. | Yukarıdaki kuralların seçenek değil alt sınır olmasının nedeni. |

## Unity LevelPlay ve Unity Advertising

Okunan sayfalar: [Unity Advertising Politika Merkezi](https://unity.com/pages/unity-operate-policy-center),
[Yerleşim Politikası](https://unity.com/legal/placement-policy) (son güncelleme 2021-06-23),
[Geçersiz Etkinlik Politikası](https://unity.com/legal/invalid-activity-policy) (2021-06-23),
[Ödüllü Envanter Politikası](https://unity.com/legal/rewarded-inventory-policy) (2024-12-04),
[İçerik Politikası](https://unity.com/legal/content-policy) (2026-06-30),
[Unity Advertising Ek Koşulları](https://unity.com/legal/monetization-services-terms-of-service) (2026-06-30; "LevelPlay Mediation Platform" kapsamda),
[Unity paket entegrasyonu](https://docs.unity.com/grow/levelplay/sdk/unity/package-integration/),
[Entegrasyon testi](https://docs.unity.com/grow/levelplay/sdk/unity/integration-testing),
[Entegrasyon test paketi](https://docs.unity.com/grow/levelplay/sdk/unity/integration-test-suite),
[Unity Editor'de sahte reklam önizleme](https://docs.unity.com/grow/levelplay/sdk/unity/preview-mock-ads-in-editor).

| Kural (resmi ifade, kısaltılmış) | Paket |
|---|---|
| Yerleşimler düğmelerin yakınına ya da altına konamaz; kullanıcının rastgele dokunduğu alanlara konamaz; arka planda çalışamaz; uygulama açılmadan önce ya da kapandıktan sonra başlatılamaz; kullanıcı reklama tıklamadan ekrandan çıkabilmeli. | **Kod** `InterstitialNaturalBreakOnly`, `NoAdInBackground`, `NoAdViewInterference`; **Geliştirici** yerleşim. |
| Geçersiz etkinlik: yayıncı çalışanlarının sınırlı, olağan uygulama testi dışında reklama elle tıklaması; görüntüleme, tıklama, indirme ve yüklemeyi teşvik ya da ödüllendirme; botlar; cihaz kimliklerini değiştirme. | **Geliştirici**; **Kod** `UnregisteredDevice`, `TestModeRequired`. |
| Ödüllü: yalnızca kullanıcı her teklif için açıkça onay verdikten sonra; ödül ve gereken eylem açıkça belirtilir; kullanıcı reklamın kendisiyle etkileşime teşvik edilemez. | **Kod** `RewardedOnRequestOnly`; ödül yalnızca SDK'nın ödül callback'inde, tıklama için asla. |
| İçerik politikası: reklam ve uygulama hedef kullanıcının olgunluk düzeyine uygun olmalı; çocuklara yönelik bayrağı kimlik kullanımını belirler. | **Kod** `ChildSettingsForwarded`. |
| Ek Koşullar: her ihlal Unity'nin fesih ya da askıya alma hakkını doğurur; geçersiz etkinlik tutarları düşülür. | Yukarıdaki kuralların alt sınır olmasının nedeni. |
| Test: test cihazları panelde reklam kimliğiyle eklenir ("Settings > Test devices"); test paketi başlatmadan önce `LevelPlay.SetMetaData("is_test_suite", "enable")`, başlattıktan sonra `LevelPlay.LaunchTestSuite()`, SDK 7.3.0+, yalnızca dikey. Demo ya da test App Key yayımlanmıyor; editör sahte modu `"editor"` anahtarı ve `"editor_banner"` gibi sahte birimlerle başlar ve "don't simulate network latency, failures, or reward logic" (ağ gecikmesini, hataları ve ödül mantığını taklit etmez). | **Kod** `TestModeRequired`, `UnregisteredDevice` (Test sekmesi Release anahtarını kopyalar; development build yalnızca listedeki cihazlarda canlı SDK reklamı ister, diğerlerinde simülatör sağlayıcı). Paketin kendi simülatörü editör sahtesinin kapsamadığı hata ve ödül senaryolarını kapsar. Debug paneli düğmesi test paketini açar. |

## Mediation içindeki ağlar

Bu SDK'lar mediation adapter'ı olarak kurulur, paket onları hiç çağırmaz. Politikaları yayıncıyı yine
de bağlar; satırlar her birinin yukarıdaki kuralların ötesinde ne eklediğini listeler.

| Ağ | Sayfa (2026-10-24'te okundu) | Ek kurallar | Paket |
|---|---|---|---|
| Meta Audience Network | [Audience Network Politikası](https://developers.facebook.com/docs/audience-network/optimization/best-practices/an-policy) | ads.txt ya da app-ads.txt tutan yayıncılar Audience Network'ü doğru listelemek zorunda; insanların kazara tıklayacağı yerlere reklam konamaz; reklam içeriği değiştirilemez, filtrelenemez, kesilemez; COPPA uyumu; ihlaller askı ya da fesih doğurur. | app-ads.txt üretici (**Doğrulayıcı** algılanan bir ağın app-ads.txt satırı yoksa uyarır, `appads.lines`); **Kod** `NoAdViewInterference`, `ChildSettingsForwarded`. |
| Pangle | [Tedarik Politikası](https://pangleglobal.com/knowledge/pangle-supply-policy) (Aralık 2025) | "Publishers should not click on their own ads"; geçiş reklamı kullanıcı etkileşimi sırasında aniden çıkamaz (yükleme ekranı, çıkış, oyun sırasında adlandırılmış); "must provide users with an option to skip the ad after 5 seconds" (5 saniye sonra atlama seçeneği); reklamlar alındığı gibi değiştirilmeden çizilir; ödüllü video otomatik gösterilemez ve tıklamayı ödüllendiremez; "primarily directed or marked at minors" (ağırlıkla reşit olmayanlara yönelik) uygulamalar desteklenmez; testler için ayrılmış test envanteri kullanılır. | **Kod** `InterstitialNaturalBreakOnly`, `NoAdViewInterference`, `RewardedOnRequestOnly`; **Doğrulayıcı** çocuklara yönelik uygulamada Pangle için uyarır (`child.networks`); **SDK** atlama sayacı. |
| Chartboost (LoopMe) | [Koşullar](https://docs.chartboost.com/en/legal/terms-conditions/) (2025-11-19) | Sahte etkinlik "repeated manual clicks, click spam, robots" içerir; reklamın görünümü küçültülemez, engellenemez, içeriği değiştirilemez; 13 yaş altına yönelik oyun ve uygulama yasak; yeni hesaplar LoopMe onayına bağlı; askı, ödeme tutma, fesih. | **Kod** `NoAdViewInterference`; **Doğrulayıcı** çocuklara yönelik uygulamada Chartboost için uyarır (`child.networks`); **Geliştirici** tek hesap. |
| Mintegral | [Sahtekârlıkla mücadele](https://helpcenter.mintegral.com/en/docs/mtg-antifraud-measures) (2026 Ç1 güncellemesi); [Reklam kreatif politikaları](https://helpcenter.mintegral.com/en/docs/ad-policies) (2025-09-17, talep tarafı) | Bot trafiği, sahte sinyal üreten scriptler, uyuşmayan uygulama kimlikleri ve cihaz çiftlikleri engellenir; bunun ötesinde yayıncı yerleşim kuralı yayımlanmamış. Google'ın Aileler listesinde yok. | **Doğrulayıcı** çocuklara yönelik uygulamada Mintegral için uyarır (`child.networks`). |
| Liftoff Monetize (Vungle) | [Yayıncı yardım merkezi](https://support.vungle.com/hc/en-us/categories/360001994512-Publisher) | Hizmet Koşulları makalesi okunma tarihinde araçla açılamadı (HTTP 403); yardım merkezi "Accessing the Terms of Service (ToS)" makalesini listeliyor. Google'ın Aileler listesinde Vungle 6.10.4+ olarak var. | **Geliştirici**: kayıt sırasında koşulları oku ve tarihini buraya yaz. |
| DT Exchange (Digital Turbine) | [Yayıncı Hizmet Siparişi](https://www.digitalturbine.com/legal/service-order-for-publishers-fyber), [Tedarik Ortakları MSA](https://www.digitalturbine.com/legal/msa-supply-fyber) | DT'nin sahtekârlıktan kaynaklandığını belirlediği gelir düşülür; Hizmet Siparişi'nde yerleşim, çocuk ya da test modu kuralı yok. Google'ın Aileler listesinde DT Exchange SDK 8.2.1+ olarak var. | **Geliştirici**: kayıt sırasında MSA'yı oku. |
| InMobi | [Başlangıç](https://support.inmobi.com/monetize/getting-started), [COPPA](https://support.inmobi.com/monetize/privacy/coppa), [Yayıncı Politikası](https://advertising.inmobi.com/publisher-policy/) | İşaretlenen hesaplar "A temporary, reversible suspension pending investigation" (inceleme süresince geçici askı), doğrulanmış sahtekârlık "An irreversible block" (geri alınamaz engel); çocuklara yönelik uygulamalar panelde ve SDK'da `setIsAgeRestricted(true)` ile bildirilir; sandbox test cihazı reklam kimliğiyle kaydedilir. Yayıncı Politikası sayfasının gövdesi okunma tarihinde araçta kesildi; kayıt sırasında okuyun. | **Kod** `ChildSettingsForwarded` (mediation bayrağı iletir); **Geliştirici** politikayı oku. |

## PolicyGuard kural haritası

Brief'in istediği kurallar ve tanımsız yer kuralı, her biri yukarıdaki politikalara bağlı. Sayısal alt sınırlar bu belgede
değil koddadır; `Nexus.Ads.Core` içinde (issue #10) belirlenir ve test edilir, bir oyun tarafından
sıkılaştırılabilir, asla gevşetilemez.

| Kural kimliği | Korumanın engellediği | Politikalar |
|---|---|---|
| `RewardedOnRequestOnly` | Yapısal: tek gösterim yolu `AdController.Show(placement)` ve otomatik gösterim yok; başka formatla tanımlanmış yer reddedilir; ödül gösterim başına bir kez ve yalnızca SDK'nın ödül callback'inde verilir. | AdMob ödül politikası; Unity Ödüllü Envanter Politikası; Pangle. |
| `InterstitialNaturalBreakOnly` | `NaturalBreak` işaretsiz yerde geçiş reklamı; uygulama açılırken, dönerken, çıkarken ya da oyun sırasında her geçiş reklamı. | AdMob izin verilmeyen uygulamalar; Play reklam politikası; Unity Yerleşim Politikası; Pangle. |
| `InterstitialFrequency` | En az aralığın içinde, oturumun ilk saniyelerinde, oturum ya da gün üst sınırının üstünde ya da ödüllü reklamın hemen ardından geçiş reklamı. | AdMob "her iki kullanıcı eyleminde bir"; Play "tekrar tekrar". |
| `NoStackedAds` | Biri açıkken ikinci tam ekran reklam; reklam gösterilirken istek. | AppLovin görünürlük kuralı; Play Aileler "birden çok reklam yeri". |
| `NoAdInBackground` | Uygulama görünür değilken gösterim (yükleme serbest kalır). | AdMob; Unity Yerleşim Politikası. |
| `NoRequestBeforeConsent` | Onay gereken yerde onay akışı bitmeden istek; gizlilik seçenekleri menüden erişilebilir kalır. | `com.nexus.ads.admob` ile gelen UMP onay akışı (AdMob); LevelPlay ve MAX onay sayfaları sağlayıcılarıyla birlikte #13 ve #14'te okunur. |
| `ChildSettingsForwarded` | `AdsSettings`'teki çocuklara yönelik bayrağı, yaş altı bayrağı ve içerik derecesi olmadan başlatılan sağlayıcı. | AdMob Aileler; Play Aileler; AppLovin çocuk kuralı; Unity İçerik Politikası; InMobi COPPA. |
| `SingleMediation` | Aynı oturumda ikinci mediation sağlayıcısı. | AppLovin "tek mediation entegrasyonu". |
| `NoAutomaticRequests` | Önceden yükleme ya da yeniden deneme politikasından gelmeyen istek. | AdMob yenileme kuralı; AppLovin geçersiz istekler. |
| `NoAdViewInterference` | Paket bir SDK reklam görünümünü ya da kapatma düğmesini asla gizlemez, üstüne çizmez, boyutlandırmaz, değiştirmez. | Play 15 saniye kapatma kuralı; AppLovin, Pangle, Chartboost, Meta "alındığı gibi çizilir". |
| `TestModeRequired` | Sağlayıcının test modu ya da Google'ın örnek birimleri olmayan development build; bunlarla alınan release build. | AdMob test reklamları; MAX test modu; LevelPlay test cihazları. |
| `UnregisteredDevice` | Test sekmesinin cihaz listesinde olmayan bir cihazda development build'den canlı SDK isteği (yerine simülatör sağlayıcı çalışır). | Her platformun geçersiz trafik kuralları. |
| `UnknownPlacement` | Ayarlarda tanımlı olmayan ya da başka formatla tanımlanmış bir yer adı, her formatta. | Issue #8 karar 4; üretilen sabitleri bir yeri adlandırmanın tek yolu olarak tutar. |

## Paketin zorlayamadığı kurallar

Kurulum sihirbazı bunları kontrol listesi olarak gösterir; yayıncı her birini onaylar.

- Canlı reklama asla dokunma, kimseden de isteme. Yalnızca sağlayıcının test modu ya da örnek birimleriyle test et.
- Release build'i ya da canlı birimli herhangi bir build'i test edenlere, arkadaşlara ya da ekibe verme.
- Platform başına tek hesap; aynı şirket bilgileri ve Play Console e-postası.
- Mağaza kaydındaki geliştirici web sitesinde, etkin her ağı listeleyen `app-ads.txt` (dosyayı paket üretir; yayımlamak elle yapılır).
- Play Console: reklam beyanı, hedef kitle, Data safety, reklam kimliği beyanı; karma kitle için Aileler yaş ekranı.
- Reklam SDK'larını adlandıran bir gizlilik politikası URL'si (Türkiye için KVKK aydınlatma metni); ayar alanı boşsa doğrulayıcı uyarır.
- Trafik kaynakları: ağların geçersiz saydığı kaynaklardan satın alınmış yükleme yok.

## Test verisi ve SDK dağıtımı

Her sağlayıcının test için yayımladıkları ve Unity eklentisinin nasıl kurulduğu. Paketin
`versionDefines` girişleri bir sağlayıcıyı yalnızca SDK'nın UPM paketi varken derler; `.unitypackage`
içe aktarması paket adı taşımaz, o yüzden bu yolda doğrulayıcı uyarır ve elle konulacak scripting define'ı
adlandırır (`sdk.unitypackage`); UPM yolu tercih edilir.

| Sağlayıcı | Yayımlanmış test verisi | Test cihazları | Unity eklentisi |
|---|---|---|---|
| Google AdMob | Her format için örnek uygulama kimlikleri ve örnek reklam birimleri, Android ve iOS (`ca-app-pub-3940256099942544/...`), `PlatformCatalog.json` içinde. | `RequestConfiguration.TestDeviceIds` ya da AdMob arayüzü; emülatörler otomatik test cihazıdır. | OpenUPM `com.google.ads.mobile` (hızlı başlangıçta 11.6.0) ya da GitHub sürümlerinden `.unitypackage`. Ayar varlığı `Assets/GoogleMobileAds/Resources/GoogleMobileAdsSettings.asset`. |
| Unity LevelPlay | Yok. Editör sahte modu `"editor"` anahtarını kullanır; yalnızca yerleşimi kapsar. | Panel, Settings > Test devices, reklam kimliğiyle; ağ ataması "bir saat içinde sıfırlanır". | Unity kayıt defteri `com.unity.services.levelplay` (sayfada 9.5.1; GitHub deposunun son etiketi ve sürümü 2026-01-20 tarihli 9.3.0, yani kayıt defteri GitHub'ın önünde) ya da `UnityLevelPlay_v9.5.1.unitypackage`. Ad alanı `Unity.Services.LevelPlay`, `LevelPlay.Init(appKey)`. |
| AppLovin MAX | Yok. Test reklamları test modu açıkken hesabın SDK anahtarı ve birimleriyle gelir. | `MaxSdk.InitializeSdk()` öncesinde `MaxSdk.SetTestDeviceAdvertisingIdentifiers(string[])` ya da Mediation Debugger. | `https://unity.packages.applovin.com/` kapsamlı kayıt defteri, kapsamlar `com.applovin.mediation.ads`, `com.applovin.mediation.adapters`, `com.applovin.mediation.dsp` (eklentinin `AppLovinPluginMigrationHelper.cs` dosyasından, 2026-09-15 tarihli 8.6.6 sürümü), AppLovin'in yükleyici paketiyle kurulur; ya da `.unitypackage`. SDK anahtarı AppLovin > Integration Manager'da. |

## Kayıt linkleri

Hepsi okunma tarihinde yanıt verdi (aksi yazılmadıkça HTTP 200).

| Platform | Rol | Link | Not |
|---|---|---|---|
| Unity LevelPlay | mediation | https://cloud.unity.com | |
| AppLovin MAX | mediation | https://dash.applovin.com | onay süreci |
| Mintegral | ağ | https://dev.mintegral.com | |
| Liftoff Monetize | ağ | https://publisher.vungle.com | |
| Pangle | ağ | https://www.pangleglobal.com | |
| DT Exchange | ağ | https://console.fyber.com | |
| Meta Audience Network | ağ | https://business.facebook.com | |
| InMobi | ağ | https://publisher.inmobi.com | oturum olmadan 401 döner; giriş sayfası |
| Chartboost | ağ | https://dashboard.chartboost.com | |
| Google AdMob | ağ | https://admob.google.com | |
