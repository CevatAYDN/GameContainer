# Code-first startup (Unity 6)

1. Import **Code-first Startup** from Nexus → Samples in Package Manager.
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

Package Manager'da Nexus → Samples → **Code-first Startup** örneğini içe aktarın.
Boş GameObject'e `ScoreBootstrap` ekleyip Play'e basın. Console'da `Nexus score: 1`
beklenir. Tek dosyadaki model, sinyal ve command'ı oyununuzun tipleriyle değiştirin.
Kurulum, iptal ve kapanış aynı sahibi izler; ek sahne veya Root gerekmez.
