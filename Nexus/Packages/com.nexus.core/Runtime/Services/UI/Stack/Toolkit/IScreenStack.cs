using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The UI Toolkit screen stack of one context (ADR-0003). Every method changes the history at once; the
    /// <c>...Async</c> variants also return a task that completes when the transitions that call started end (already
    /// complete without one). A call made from a screen's handler during another call gets a completed task; the task of
    /// the call that is running covers its transitions. Bind it with <c>builder.BindServiceInterfacesAndSelfTo&lt;ScreenStack&gt;()</c>, which also
    /// binds the context's <see cref="IBackRouter"/>.
    /// </summary>
    public interface IScreenStack : INexusService
    {
        /// <summary>The route on top of the history. Only the stack sets it, so it cannot drift from the history.</summary>
        IReadOnlyObservableProperty<ScreenRoute> Current { get; }

        int Depth { get; }

        /// <summary>
        /// Pushes the screen. The route already on top does nothing; the same screen on top with other arguments
        /// takes them. The screen below on the same layer is hidden; screens on lower layers stay shown. Push does not
        /// look at <see cref="ToolkitScreen.BlocksBack"/>: a game that refuses routes while a blocking screen is shown
        /// checks <see cref="IsShown{TScreen}"/> in its own command.
        /// </summary>
        void Push<TScreen>(object args = null) where TScreen : ToolkitScreen;

        /// <summary>Clears the history and pushes the screen.</summary>
        void ReplaceRoot<TScreen>(object args = null) where TScreen : ToolkitScreen;

        /// <summary>Takes the screen out of the history wherever it is.</summary>
        void Close<TScreen>() where TScreen : ToolkitScreen;

        /// <summary>The top screen's <see cref="ToolkitScreen.OnBack"/>, then its block, then a pop.</summary>
        BackResult Back();

        ValueTask PushAsync<TScreen>(object args = null) where TScreen : ToolkitScreen;
        ValueTask ReplaceRootAsync<TScreen>(object args = null) where TScreen : ToolkitScreen;
        ValueTask CloseAsync<TScreen>() where TScreen : ToolkitScreen;
        ValueTask<BackResult> BackAsync();

        bool IsShown<TScreen>() where TScreen : ToolkitScreen;

        /// <summary>Transitions are skipped and every task completes at once (Gigawatt's setting).</summary>
        bool ReducedMotion { get; set; }
    }
}
