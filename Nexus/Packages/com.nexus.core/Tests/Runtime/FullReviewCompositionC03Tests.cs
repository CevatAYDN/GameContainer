using Nexus.Core;
using NUnit.Framework;
using UnityEngine;

namespace Nexus.Tests.Runtime
{
    public sealed class FullReviewCompositionC03Tests
    {
        [Test]
        public void NativeOnDisableDuringViewBind_CancelsMediatorAcquisition()
        {
            using var context = new Context();
            var gameObject = new GameObject("composition C03 disabling view");
            try
            {
                var mediator = new ProbeMediator();
                context.Container.BindFactory(() => mediator);
                var view = gameObject.AddComponent<DisablingView>();
                context.RegisterView(view);
                Assert.IsFalse(gameObject.activeInHierarchy);
                Assert.AreEqual(1, view.Unbinds, "Real OnDisable must cancel the in-progress View binding.");
                Assert.AreEqual(0, mediator.Binds, "A disabled view must not acquire its mediator.");
                Assert.AreEqual(0, context.Resolve<ViewBinder>().ActiveMediatorCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(gameObject); }
        }

        [Mediator(typeof(ProbeMediator))]
        private sealed class DisablingView : View
        {
            public int Unbinds;
            protected override void OnEnable() { }
            protected override void OnBind(IContext context) => gameObject.SetActive(false);
            protected override void OnUnbind() => Unbinds++;
        }

        private sealed class ProbeMediator : IMediator
        {
            public int Binds;
            public void Bind(object view, ISignalBus signalBus) => Binds++;
            public void Unbind() { }
        }
    }
}
