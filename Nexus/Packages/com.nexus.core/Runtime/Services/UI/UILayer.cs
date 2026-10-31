namespace Nexus.Core.Services
{
    /// <summary>
    /// The layers of Nexus in-game UI. For the uGUI <see cref="UIManager"/> and <see cref="UICanvasSystem"/> (per-layer
    /// roots and the modal interactivity policy) they are the canvas stacking order: higher values render on top. For the
    /// UI Toolkit <c>ScreenStack</c> a layer decides only which screens are shown: inside one layer the screen
    /// opened last is shown, and lower layers stay shown; the draw order is the game's <c>UIDocument</c> sort order.
    /// </summary>
    public enum UILayer
    {
        Background = 0,
        HUD = 10,
        Screen = 20,
        Popup = 30,
        Modal = 40,
        Overlay = 50,
        System = 60
    }
}
