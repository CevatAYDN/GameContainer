using System.Text.RegularExpressions;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

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

    public sealed class StaleProbeWithOptionalMember
    {
        [Inject] public IStaleProbeDependency First { get; set; }
        [OptionalInject] public IStaleProbeDependency Optional { get; set; }
    }

    public sealed class StaleProbeWithMethod
    {
        public int Constructed;
        [Inject] public IStaleProbeDependency First { get; set; }
        [Inject] public void Construct(IStaleProbeDependency dependency) => Constructed++;
    }

    /// <summary>
    /// A generated binder written before a type gained or lost an [Inject] member left the new member null, or, when the
    /// container noticed after the injector ran, invoked the type's [Inject] methods twice (#191). Each generated injector
    /// carries the member signature it was written for; the container compares it with the type's members before the
    /// injector's first run and injects a type whose binder is stale by reflection, with one warning.
    /// </summary>
    public sealed class StaleBinderFallbackTests
    {
        private static readonly Regex StaleWarning = new Regex(@"^\[Nexus\] The generated binder was written for other \[Inject\] members of ");

        [SetUp] public void Reset() => NexusDI.ClearGeneratedRegistrations();

        [TearDown]
        public void Cleanup()
        {
            NexusDI.ClearGeneratedRegistrations();
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AnInjectorWrittenForOtherMembersIsDroppedBeforeItsFirstRun()
        {
            int calls = 0;
            NexusDI.RegisterInjector<StaleProbeSubject>((subject, di) => { calls++; subject.First = di.Resolve<IStaleProbeDependency>(); },
                "p:StaleProbeSubject.First");
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            LogAssert.Expect(LogType.Warning, StaleWarning);
            var first = new StaleProbeSubject();
            container.Inject(first);
            var later = new StaleProbeSubject();
            container.Inject(later);

            Assert.That(calls, Is.Zero, "the stale injector never runs");
            Assert.That(first.First, Is.Not.Null);
            Assert.That(first.Second, Is.Not.Null, "the member the stale binder does not know is injected by reflection");
            Assert.That(later.Second, Is.Not.Null, "later instances use reflection too, without another warning");
        }

        [Test]
        public void AnOptionalMemberTheBinderDoesNotKnowIsInjectedByReflection()
        {
            // issue #81: LocalizationService and AudioService gained [OptionalInject] members; an older binder left them null with no log
            NexusDI.RegisterInjector<StaleProbeWithOptionalMember>((subject, di) => subject.First = di.Resolve<IStaleProbeDependency>(),
                "p:StaleProbeWithOptionalMember.First");
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            LogAssert.Expect(LogType.Warning, StaleWarning);
            var subject = new StaleProbeWithOptionalMember();
            container.Inject(subject);

            Assert.That(subject.Optional, Is.Not.Null, "the bound optional member the stale binder does not know is injected by reflection");
        }

        [Test]
        public void AStaleInjectorOfATypeWithAnInjectMethodInvokesTheMethodOnce()
        {
            int calls = 0;
            NexusDI.RegisterInjector<StaleProbeWithMethod>((subject, di) => { calls++; subject.First = di.Resolve<IStaleProbeDependency>(); },
                "p:StaleProbeWithMethod.First");
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            LogAssert.Expect(LogType.Warning, StaleWarning);
            var subject = new StaleProbeWithMethod();
            container.Inject(subject);

            Assert.That(calls, Is.Zero);
            Assert.That(subject.First, Is.Not.Null);
            Assert.That(subject.Constructed, Is.EqualTo(1), "the stale injector and the reflection pass do not both run");
        }

        [Test]
        public void AnInjectorWrittenForTheTypesMembersIsKept()
        {
            int calls = 0;
            NexusDI.RegisterInjector<StaleProbeSubject>((subject, di) =>
            {
                calls++;
                subject.First = di.Resolve<IStaleProbeDependency>();
                subject.Second = di.Resolve<IStaleProbeDependency>();
            }, "p:StaleProbeSubject.First;p:StaleProbeSubject.Second");
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            container.Inject(new StaleProbeSubject());
            container.Inject(new StaleProbeSubject());

            Assert.That(calls, Is.EqualTo(2), "a binder written for the type's members keeps being used");
        }

        [Test]
        public void AMemberWithoutABindingKeepsTheInjectorAndGetsTheReflectionPathsAnswer()
        {
            int calls = 0;
            NexusDI.RegisterInjector<StaleProbeWithUnboundMember>((subject, di) =>
            {
                calls++;
                subject.First = di.Resolve<IStaleProbeDependency>();
                var unbound = di.TryResolve<IStaleProbeUnbound>();
                if (unbound != null) subject.Unbound = unbound;
                else di.ReportMissingMember(subject, typeof(StaleProbeWithUnboundMember), "Unbound");
            }, "p:StaleProbeWithUnboundMember.First;p:StaleProbeWithUnboundMember.Unbound");
            var container = new NexusDI();
            container.Bind<IStaleProbeDependency, StaleProbeDependency>();

            var missing = new Regex(@"^\[Nexus\] \[Inject\] dependency 'Nexus\.Editor\.Tests\.IStaleProbeUnbound' for property 'Nexus\.Editor\.Tests\.StaleProbeWithUnboundMember\.Unbound' is not registered");
            LogAssert.Expect(LogType.Error, missing);
            LogAssert.Expect(LogType.Error, missing);
            var first = new StaleProbeWithUnboundMember();
            container.Inject(first);
            container.Inject(new StaleProbeWithUnboundMember());

            Assert.That(first.First, Is.Not.Null);
            Assert.That(first.Unbound, Is.Null, "nothing is bound for this member, so reflection could not fill it either");
            Assert.That(calls, Is.EqualTo(2), "a missing registration is not a stale binder");
        }
    }
}
