using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    /// <summary>The Architecture tab's map (#193 phase 3), built from this assembly's types.</summary>
    public class ArchitectureMapTests
    {
        public struct ArchCodeBoundSignal { public int Amount; }

        // bound with BindCommand in code, no attribute: the Explorer, Graph and Game Manager scans read attributes only
        public sealed class ArchCodeBoundCommand : ICommand<ArchCodeBoundSignal>
        {
            public void Execute(ArchCodeBoundSignal signal) { }
        }

        public struct ArchUnhandledSignal { public int Amount; }

        public interface IArchTarget { }

        public sealed class ArchTargetService : IArchTarget, INexusService
        {
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        public abstract class ArchBaseCommand : ICommand<ArchCodeBoundSignal>
        {
            [Inject] private IArchTarget _target = null;
            protected IArchTarget Target => _target;
            public abstract void Execute(ArchCodeBoundSignal signal);
        }

        public sealed class ArchDerivedCommand : ArchBaseCommand
        {
            public override void Execute(ArchCodeBoundSignal signal) { }
        }

        private static ArchitectureMap BuildMap() => ArchitectureMap.Build(new[] { typeof(ArchitectureMapTests).Assembly });

        [Test]
        public void ACommandBoundOnlyInCode_IsListedWithTheSignalItsInterfaceNames()
        {
            var map = BuildMap();

            Assert.IsTrue(map.TryGet(typeof(ArchCodeBoundSignal), out var signal), "the signal is listed");
            Assert.AreEqual(ArchitectureMap.Kind.Signal, signal.Kind);
            var handler = signal.Handlers.Single(h => h.Command == typeof(ArchCodeBoundCommand));
            Assert.AreEqual(ArchitectureMap.Source.Implements, handler.Source);
            Assert.IsTrue(map.TryGet(typeof(ArchCodeBoundCommand), out var command));
            Assert.AreEqual(typeof(ArchCodeBoundSignal), command.Handlers.Single().Signal);
        }

        [Test]
        public void AStructNamedLikeASignal_ThatNoCommandHandles_IsNotListed()
        {
            Assert.IsFalse(BuildMap().TryGet(typeof(ArchUnhandledSignal), out _));
        }

        [Test]
        public void APrivateInjectFieldOfABaseClass_IsADependency_AndListsItsOwnerAsADependentOfTheService()
        {
            var map = BuildMap();

            Assert.IsTrue(map.TryGet(typeof(ArchDerivedCommand), out var derived));
            var dependency = derived.Dependencies.Single(d => d.Target == typeof(IArchTarget));
            StringAssert.Contains("_target", dependency.Member);
            Assert.IsTrue(map.TryGet(typeof(ArchTargetService), out var service));
            Assert.IsTrue(service.Dependents.Any(d => d.Owner == typeof(ArchDerivedCommand)),
                "a member typed as the interface makes its owner a dependent of the service that implements it");
        }
    }
}
