namespace Nexus.Core
{
    /// <summary>
    /// Fired on a context's signal bus once its whole start-up has run: services initialised, every
    /// <see cref="IContextLifecycle.OnStartAsync"/>, the startables and the lazy services resolved during start (issue #70).
    /// Views that registered before services were ready redraw on it. It fires just before
    /// <see cref="Context.IsInitialized"/> turns true; a handler that throws fails the start as a throwing OnStartAsync does.
    /// </summary>
    public readonly struct ContextStartedSignal
    {
        public readonly string ScopeTag;
        public ContextStartedSignal(string scopeTag) { ScopeTag = scopeTag; }
    }

    /// <summary>
    /// Fired by <see cref="Root"/> on its context's signal bus when the application pauses (<c>IsPaused</c> true) or
    /// resumes (false). Unity calls OnApplicationPause before a context has started on Android; those calls are not
    /// forwarded, so a handler never runs against services that have not initialised (issue #70). Subscribe with
    /// <c>Subscribe</c>, not <c>SubscribeAsync</c>: the signal is fired synchronously because Android may freeze the
    /// process as soon as OnApplicationPause returns.
    /// </summary>
    public readonly struct ApplicationPauseSignal
    {
        public readonly bool IsPaused;
        public ApplicationPauseSignal(bool isPaused) { IsPaused = isPaused; }
    }

    /// <summary>
    /// Fired by <see cref="Root"/> on its context's signal bus from OnApplicationQuit, once the context has started (issue #199).
    /// Android and iOS normally end a backgrounded app without calling OnApplicationQuit, so a save that must survive belongs
    /// on <see cref="ApplicationPauseSignal"/>; this signal covers <c>Application.Quit</c>, a desktop window closed and the
    /// editor leaving play mode. Fired synchronously: subscribe with <c>Subscribe</c>.
    /// </summary>
    public readonly struct ApplicationQuitSignal
    {
    }

    /// <summary>
    /// Fired by <see cref="Root"/> on its context's signal bus when the device reports low memory (<c>Application.lowMemory</c>,
    /// which Unity raises only while the app is in the foreground), once the context has started (issue #199). Release what can
    /// be rebuilt. Fired synchronously: subscribe with <c>Subscribe</c>.
    /// </summary>
    public readonly struct LowMemorySignal
    {
    }
}
