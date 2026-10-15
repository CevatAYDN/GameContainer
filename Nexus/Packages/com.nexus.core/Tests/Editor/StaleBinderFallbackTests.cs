using Nexus.Core;
using NUnit.Framework;

namespace Nexus.Editor.Tests
{
    public interface IStaleProbeDependency { }
    public sealed class StaleProbeDependency : IStaleProbeDependency { }
    public interface IStaleProbeUnbound { }

    public sealed class StaleProbeSubject
    {
        [Inject] public IStaleProbeDependency First { get; set; }
        [Inject] public IStaleProbeDependency Second { get; set; }
    }

    public sealed class StaleProbeWithUnboundMember
    {
        [Inject] public IStaleProbeDependency First { get; set; }
        [Inject] public IStaleProbeUnbound Unbound { get; set; }
    }

    /// <summary>
    /// A generated binder written before a type gained an [Inject] member leaves that member null on every instance,
    /// because no reflection pass runs while a generated injector exists. The container checks each generated injector
    /// once per type against the members it could have filled and falls back to reflection for that type.
    /// </summary>
    public sealed class StaleBinderFallbackTests
    {
        [SetUp] public void Reset() => NexusDI.ClearGeneratedRegistrations();
        [TearDown] public void Cleanup() => NexusDI.ClearGeneratedRegistrations();

        [Test]
        public void StaleGeneratedInjectorFallsBackToReflection()
        {
            NexusDI.RegisterInjector<StaleProbeSubject>((subject, di) => subject.First = di.Resolve<IStaleProbeDependency>());
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            var first = new StaleProbeSubject();
            container.Inject(first);
            var later = new StaleProbeSubject();
            container.Inject(later);

            Assert.That(first.First, Is.Not.Null);
            Assert.That(first.Second, Is.Not.Null, "the member the stale binder does not know is injected by reflection");
            Assert.That(later.Second, Is.Not.Null, "later instances of the type no longer use the stale injector");
        }

        [Test]
        public void CompleteGeneratedInjectorIsKept()
        {
            int calls = 0;
            NexusDI.RegisterInjector<StaleProbeSubject>((subject, di) =>
            {
                calls++;
                subject.First = di.Resolve<IStaleProbeDependency>();
                subject.Second = di.Resolve<IStaleProbeDependency>();
            });
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            container.Inject(new StaleProbeSubject());
            container.Inject(new StaleProbeSubject());

            Assert.That(calls, Is.EqualTo(2), "a binder that sets every member keeps being used");
        }

        [Test]
        public void MemberWithoutABindingIsNotMistakenForAStaleBinder()
        {
            int calls = 0;
            NexusDI.RegisterInjector<StaleProbeWithUnboundMember>((subject, di) =>
            {
                calls++;
                subject.First = di.Resolve<IStaleProbeDependency>();
            });
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            var first = new StaleProbeWithUnboundMember();
            container.Inject(first);
            container.Inject(new StaleProbeWithUnboundMember());

            Assert.That(first.First, Is.Not.Null);
            Assert.That(first.Unbound, Is.Null, "nothing is bound for this member, so reflection could not fill it either");
            Assert.That(calls, Is.EqualTo(2), "the generated injector is kept; a missing registration is not a stale binder");
        }
    }
}
