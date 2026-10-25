# Code-first startup (Unity 6)

1. Import **Code-first Startup** from Nexus Observable Architecture → Samples in Package Manager.
2. Add `ScoreBootstrap` to an empty GameObject in your scene.
3. Enter Play Mode. The Console should show `Nexus score: 1`.

The entire example is in `ScoreBootstrap.cs`. Bindings are configured before validation
and initialization; the owner cancels startup and disposes its context on destruction.
No demo scene, generated asset or assembly scan is required. Replace the score types
with your own game's model, signal and command. Keep startup on the Unity main thread.

For async shutdown that your game can await before destroying its objects, call
`await context.DisposeAsync()`. `OnDestroy` uses synchronous `Dispose` because Unity
does not await lifecycle messages. Async-only cleanup may continue after this callback.

## Türkçe

1. Package Manager'da Nexus Observable Architecture → Samples altından **Code-first Startup**
   örneğini içe aktarın.
2. Sahnenizdeki boş bir GameObject'e `ScoreBootstrap` ekleyin.
3. Play Mode'a girin. Console'da `Nexus score: 1` görünmelidir.

Örneğin tamamı `ScoreBootstrap.cs` dosyasındadır. Binding'ler doğrulama ve başlatmadan önce
yapılandırılır; sahibi yok edildiğinde başlatmayı iptal eder ve context'ini dispose eder.
Demo sahnesi, üretilmiş asset veya assembly taraması gerekmez. Skor tiplerini kendi oyununuzun
model, sinyal ve command tipleriyle değiştirin. Başlatmayı Unity ana thread'inde tutun.

Oyununuzun nesnelerini yok etmeden önce bekleyebileceği asenkron kapanış için
`await context.DisposeAsync()` çağırın. Unity yaşam döngüsü mesajlarını beklemediği için
`OnDestroy` senkron `Dispose` kullanır. Yalnızca asenkron olan temizlik bu geri çağrıdan
sonra da sürebilir.
