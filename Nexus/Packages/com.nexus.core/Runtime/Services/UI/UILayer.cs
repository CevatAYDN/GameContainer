namespace Nexus.Core.Services
{
    /// <summary>
    /// Canvas stacking order for Nexus in-game UI (UGUI). Higher values render on top.
    /// Used by <see cref="UIManager"/> (screen layering) and <see cref="UICanvasSystem"/>
    /// (per-layer roots + modal interactivity policy).
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
