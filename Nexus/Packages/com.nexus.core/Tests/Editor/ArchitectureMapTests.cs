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

        public struct ArchCompositeFirst { }
        public struct ArchCompositeSecond { }

        [CompositeSignalHandler(typeof(ArchCompositeFirst), typeof(ArchCompositeSecond), Priority = 4)]
        public sealed class ArchAttributedComposite : ICompositeCommand
        {
            public void Execute(CompositeContext signals) { }
        }

        public struct ArchTypedFirst { }
        public struct ArchTypedSecond { }

        // bound with BindCompositeCommand in code
        public sealed class ArchTypedComposite : ICompositeCommand<ArchTypedFirst, ArchTypedSecond>
        {
            public void Execute(ArchTypedFirst first, ArchTypedSecond second) { }
        }

        public struct ArchRegisteredSignal { }

        [RegisterCommand(typeof(ArchRegisteredSignal), Mode = ExecutionMode.Concurrent, Priority = 7, OneShot = true, IsAsync = true)]
        public sealed class ArchRegisteredCommand : IAsyncCommand<ArchRegisteredSignal>
        {
            public ValueTask ExecuteAsync(ArchRegisteredSignal signal, CancellationToken ct) => default;
        }

        public struct ArchRegisteredFirst { }
        public struct ArchRegisteredSecond { }

        [RegisterCompositeCommand(typeof(ArchRegisteredFirst), typeof(ArchRegisteredSecond), Priority = 3)]
        public sealed class ArchRegisteredComposite : ICompositeCommand
        {
            public void Execute(CompositeContext signals) { }
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
        public void CompositeCommands_AreListed_WithTheSignalsTheyWaitFor()
        {
            // a composite was classified as nothing, so it and the signals only it names had no node (#253)
            var map = BuildMap();

            Assert.IsTrue(map.TryGet(typeof(ArchAttributedComposite), out var attributed), "an ICompositeCommand with [CompositeSignalHandler] is listed");
            Assert.AreEqual(ArchitectureMap.Kind.Command, attributed.Kind);
            CollectionAssert.AreEquivalent(new[] { typeof(ArchCompositeFirst), typeof(ArchCompositeSecond) }, attributed.Handlers.Select(h => h.Signal).ToArray());
            Assert.IsTrue(attributed.Handlers.All(h => h.Source == ArchitectureMap.Source.Composite && h.Priority == 4));

            Assert.IsTrue(map.TryGet(typeof(ArchTypedComposite), out var typed), "an ICompositeCommand<TFirst, TSecond> bound in code is listed");
            CollectionAssert.AreEquivalent(new[] { typeof(ArchTypedFirst), typeof(ArchTypedSecond) }, typed.Handlers.Select(h => h.Signal).ToArray());
            Assert.IsTrue(typed.Handlers.All(h => h.Source == ArchitectureMap.Source.Implements && h.Mode == ExecutionMode.Composite));
        }

        [Test]
        public void RegisterAttributes_GiveTheEdgeTheirModePriorityAndFlags()
        {
            // [RegisterCommand] and [RegisterCompositeCommand] were not read: no edge, or an Implements edge, Sequential, priority 0 (#253)
            var map = BuildMap();

            Assert.IsTrue(map.TryGet(typeof(ArchRegisteredSignal), out var signal), "the signal a [RegisterCommand] names is listed");
            var handler = signal.Handlers.Single(h => h.Command == typeof(ArchRegisteredCommand));
            Assert.AreEqual(ArchitectureMap.Source.Registered, handler.Source);
            Assert.AreEqual(ExecutionMode.Concurrent, handler.Mode);
            Assert.AreEqual(7, handler.Priority);
            Assert.IsTrue(handler.OneShot, "one-shot");
            Assert.IsTrue(handler.IsAsync, "async");

            Assert.IsTrue(map.TryGet(typeof(ArchRegisteredComposite), out var composite), "a [RegisterCompositeCommand] composite is listed");
            Assert.AreEqual(2, composite.Handlers.Count);
            Assert.IsTrue(composite.Handlers.All(h => h.Source == ArchitectureMap.Source.RegisteredComposite && h.Mode == ExecutionMode.Composite && h.Priority == 3));
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
