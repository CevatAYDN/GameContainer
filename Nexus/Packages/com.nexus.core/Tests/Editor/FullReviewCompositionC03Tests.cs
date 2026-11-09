using System;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC03Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void UnregisterDuringViewBind_DoesNotAttachAMediator()
        {
            using var context = new Context();
            var view = new MediatedView();
            view.OnBind = owner => owner.UnregisterView(view);
            context.RegisterView(view);
            Require(!view.Bound && view.MediatorBinds == 0, "A cancelled view acquired a mediator.");
            Require(context.Resolve<ViewBinder>().ActiveMediatorCount == 0, "The cancelled registration stayed active.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void DisposeDuringViewBind_DoesNotPublishARegistration()
        {
            using var context = new Context();
            var binder = context.Resolve<ViewBinder>();
            var view = new MediatedView { OnBind = owner => owner.Dispose() };
            context.RegisterView(view);
            Require(context.IsDisposed && !view.Bound, "A view remained bound after its owner ended during Bind.");
            Require(view.MediatorBinds == 0 && binder.ActiveMediatorCount == 0,
                "Registration resumed after context disposal.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void RegisterAgainDuringBind_DoesNotCreateASecondMediator()
        {
            using var context = new Context();
            var view = new MediatedView();
            view.OnBind = owner =>
            {
                if (view.BindCalls == 1) owner.RegisterView(view);
            };
            context.RegisterView(view);
            Require(view.BindCalls == 1 && view.MediatorBinds == 1, "A registration recursively acquired another mediator.");
            Require(context.Resolve<ViewBinder>().ActiveMediatorCount == 1, "The successful registration was lost.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void CancelledViewWithoutMediator_CanRegisterAgain()
        {
            using var context = new Context();
            var view = new ViewProbe();
            view.OnBind = owner =>
            {
                if (view.BindCalls == 1) owner.UnregisterView(view);
            };
            context.RegisterView(view);
            context.RegisterView(view);
            Require(view.Bound && view.BindCalls == 2, "A cancelled binding-only view prevented its next registration.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void UnregisterDuringMediatorBind_CleansSubscriptionsCreatedLaterInThatCallback()
        {
            using var context = new Context();
            var source = new EventSource();
            context.Container.BindFactory(() => new ProbeMediator(source));
            var view = new MediatedView();
            view.OnMediatorBind = () => context.UnregisterView(view);
            context.RegisterView(view);
            source.Raise();
            Require(!view.Bound && view.Notifications == 0, "Cancelled mediator setup left a live external subscription.");
            Require(context.Resolve<ViewBinder>().ActiveMediatorCount == 0, "The cancelled mediator stayed registered.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private class ViewProbe : IView
        {
            public bool Bound;
            public int BindCalls;
            public Action<IContext> OnBind;
            public void Bind(IContext context)
            {
                Bound = true;
                BindCalls++;
                OnBind?.Invoke(context);
            }
            public void Unbind() => Bound = false;
        }

        [Mediator(typeof(ProbeMediator))]
        private sealed class MediatedView : ViewProbe
        {
            public int MediatorBinds, Notifications;
            public Action OnMediatorBind;
        }

        private sealed class EventSource
        {
            public event Action Changed;
            public void Raise() => Changed?.Invoke();
        }

        private sealed class ProbeMediator : IMediator
        {
            private readonly EventSource _source;
            private Action _listener;
            public ProbeMediator() { }
            public ProbeMediator(EventSource source) => _source = source;
            public void Bind(object view, ISignalBus signalBus)
            {
                var target = (MediatedView)view;
                target.MediatorBinds++;
                target.OnMediatorBind?.Invoke();
                if (_source != null)
                {
                    _listener = () => target.Notifications++;
                    _source.Changed += _listener;
                }
            }
            public void Unbind()
            {
                if (_source != null) _source.Changed -= _listener;
                _listener = null;
            }
        }
    }
}
