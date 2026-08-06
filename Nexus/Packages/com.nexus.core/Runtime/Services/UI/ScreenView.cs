using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Base class for Nexus UI screens.
    ///
    /// A screen is a <see cref="View"/> that is opened/closed by the <see cref="UIManager"/>
    /// on a dedicated canvas layer. Because it derives from View, the standard Nexus pipeline
    /// — OnEnable → ViewRegistration → Context.RegisterView → ViewBinder — attaches its
    /// mediator automatically the moment the screen prefab is instantiated. UIManager only
    /// handles layering, pooling and lifecycle ordering; it never touches the mediator itself.
    ///
    /// Implements <see cref="IUIWindowLifecycle"/> so screens integrate with UIManager's
    /// async open/close pipeline (opening → opened → closing → closed).
    /// </summary>
    [Preserve]
    public abstract class ScreenView : View, IUIWindowLifecycle
    {
        /// <summary>
        /// Unique key used by UIManager for registration, pooling and asset lookup
        /// (the default ResourcesUIAssetProvider resolves "UI/Windows/{ScreenName}").
        /// It is the concrete type name — UIManager keys are type-safe and deterministic.
        /// </summary>
        public string ScreenName => GetType().Name;

        /// <summary>Payload passed to the open call. Null while the screen is closed.</summary>
        public object OpenArgs { get; private set; }

        /// <summary>True between <see cref="OnOpenedAsync"/> and <see cref="OnClosedAsync"/>.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>Raised after the screen fully opens. Arg = open payload (may be null).</summary>
        public event Action<object> ScreenOpened;

        /// <summary>Raised after the screen fully closes.</summary>
        public event Action ScreenClosed;

        public ValueTask OnOpeningAsync(object args, CancellationToken ct)
        {
            OpenArgs = args;
            OnScreenOpening(args);
            return default;
        }

        public ValueTask OnOpenedAsync(CancellationToken ct)
        {
            IsOpen = true;
            OnScreenOpened(OpenArgs);
            ScreenOpened?.Invoke(OpenArgs);
            return default;
        }

        public ValueTask OnClosingAsync(CancellationToken ct)
        {
            OnScreenClosing();
            return default;
        }

        public ValueTask OnClosedAsync(CancellationToken ct)
        {
            IsOpen = false;
            OnScreenClosed();
            ScreenClosed?.Invoke();
            OpenArgs = null;
            return default;
        }

        /// <summary>Called before the screen becomes visible. Override for prep/refresh.</summary>
        protected virtual void OnScreenOpening(object args) { }

        /// <summary>Called when the screen is fully open. Override to bind models to UI.</summary>
        protected virtual void OnScreenOpened(object args) { }

        /// <summary>Called when the screen starts closing. Override for teardown prep.</summary>
        protected virtual void OnScreenClosing() { }

        /// <summary>Called after the screen is fully closed. Override to reset state.</summary>
        protected virtual void OnScreenClosed() { }
    }
}
