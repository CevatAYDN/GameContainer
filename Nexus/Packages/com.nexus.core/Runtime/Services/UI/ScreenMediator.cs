using System;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Base mediator for <see cref="ScreenView"/> screens.
    ///
    /// Adds screen lifecycle hooks (<see cref="OnScreenOpened"/>, <see cref="OnScreenClosed"/>)
    /// on top of the standard <see cref="Mediator{TView}"/> so derived mediators can bind models
    /// while the screen is open and release them when it closes — without leaking subscriptions.
    /// The mediator is attached automatically by the ViewBinder pipeline; it only needs the
    /// <c>[Mediator(typeof(TMediator))]</c> attribute on the ScreenView.
    /// </summary>
    [Preserve]
    public abstract class ScreenMediator<TView> : Mediator<TView> where TView : ScreenView
    {
        protected override void OnBind()
        {
            base.OnBind();
            if (View != null)
            {
                View.ScreenOpened += OnScreenOpenedEvent;
                View.ScreenClosed += OnScreenClosedEvent;
            }
        }

        protected override void OnUnbind()
        {
            if (View != null)
            {
                View.ScreenOpened -= OnScreenOpenedEvent;
                View.ScreenClosed -= OnScreenClosedEvent;
            }
            base.OnUnbind();
        }

        private void OnScreenOpenedEvent(object args) => OnScreenOpened(args);
        private void OnScreenClosedEvent() => OnScreenClosed();

        /// <summary>Called when the screen fully opens. Subscribe to models here.</summary>
        protected virtual void OnScreenOpened(object args) { }

        /// <summary>Called after the screen closes. Release model subscriptions here.</summary>
        protected virtual void OnScreenClosed() { }
    }
}
