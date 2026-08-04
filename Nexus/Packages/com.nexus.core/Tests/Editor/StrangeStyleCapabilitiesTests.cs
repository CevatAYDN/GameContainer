using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// EditMode proof for the StrangeIoC-style capabilities added to Nexus:
    /// polymorphic binding (BindMultiple), [Deconstruct] cleanup hooks,
    /// [Construct] preferred-constructor alias, and .Once() one-shot commands.
    /// Mirrors the harness BinderSuite (PM/DC/CT/ON ids) against the real runtime.
    /// </summary>
    [TestFixture]
    public class StrangeStyleCapabilitiesTests
    {
        // ------------------------------------------------------------------
        // Test model types
        // ------------------------------------------------------------------

        public interface IETUnit { string Kind(); }
        public interface IETAttackable { int MaxHp { get; } }
        public interface IETUpdatable { int TickCount { get; } }

        public sealed class ETCombatUnit : IETUnit, IETAttackable, IETUpdatable
        {
            public string Kind() => "combat";
            public int MaxHp => 100;
            public int TickCount => 7;
        }

        public sealed class ETStorage
        {
            public string Label;
            public ETStorage() { }
            public ETStorage(string label) => Label = label;
        }

        public sealed class ETDeconstructService
        {
            public readonly List<int> Calls = new();

            [Inject] public ETStorage Storage;

            [Deconstruct(Order = 10)] private void CleanupSecond() => Calls.Add(10);
            [Deconstruct(Order = 0)] private void CleanupFirst() => Calls.Add(0);
            [Deconstruct(Order = 5)] private void CleanupMiddle() => Calls.Add(5);
        }

        public sealed class ETConstructConsumer
        {
            public ETStorage ViaCtor;
            public bool ParameterlessUsed;

            public ETConstructConsumer() { ParameterlessUsed = true; }

            [Inject]
            public ETConstructConsumer(ETStorage storage)
            {
                ViaCtor = storage;
            }
        }

        public readonly struct ETOnceSignal { public readonly int Value; public ETOnceSignal(int v) => Value = v; }

        public sealed class ETOnceCommand : ICommand<ETOnceSignal>
        {
            public static int Executions;
            public void Execute(ETOnceSignal signal) => Executions++;
        }

        public readonly struct ETOnceAsyncSignal { public readonly int Value; public ETOnceAsyncSignal(int v) => Value = v; }

        public sealed class ETOnceAsyncCommand : IAsyncCommand<ETOnceAsyncSignal>
        {
            public static int Executions;
            public ValueTask ExecuteAsync(ETOnceAsyncSignal signal, CancellationToken ct)
            {
                Executions++;
                return default;
            }
        }

        // ------------------------------------------------------------------
        // PM — polymorphic binding shares ONE singleton across interfaces
        // ------------------------------------------------------------------

        [Test]
        public void BindMultiple_AllInterfaces_ShareSameSingleton()
        {
            using var ctx = NexusTestHarness.CreateContext(
                builder => builder.BindMultiple<IETUnit, IETAttackable, IETUpdatable, ETCombatUnit>());

            var asUnit = ctx.Context.Resolve<IETUnit>();
            var asAttackable = ctx.Context.Resolve<IETAttackable>();
            var asUpdatable = ctx.Context.Resolve<IETUpdatable>();

            Assert.IsNotNull(asUnit);
            Assert.AreSame(asUnit, asAttackable, "Each interface must resolve to the SAME instance");
            Assert.AreSame(asAttackable, asUpdatable, "Each interface must resolve to the SAME instance");
            Assert.IsInstanceOf<ETCombatUnit>(asUnit);
        }

        [Test]
        public void BindMultiple_EachInterface_SatisfiesItsContract()
        {
            using var ctx = NexusTestHarness.CreateContext(
                builder => builder.BindMultiple<IETUnit, IETAttackable, IETUpdatable, ETCombatUnit>());

            Assert.AreEqual("combat", ctx.Context.Resolve<IETUnit>().Kind());
            Assert.AreEqual(100, ctx.Context.Resolve<IETAttackable>().MaxHp);
            Assert.AreEqual(7, ctx.Context.Resolve<IETUpdatable>().TickCount);
        }

        // ------------------------------------------------------------------
        // DC — [Deconstruct] runs in ascending Order before container disposal
        // ------------------------------------------------------------------

        [Test]
        public void Deconstruct_RunsInAscendingOrder_OnContainerDispose()
        {
            // using + explicit Dispose is safe (Context.Dispose is idempotent via _disposed
            // guard) and prevents a context leak if Resolve throws before the explicit dispose.
            using var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.Bind<ETStorage>();
                    builder.Bind<ETDeconstructService>();
                });

            var svc = ctx.Context.Resolve<ETDeconstructService>();
            Assert.IsNotNull(svc.Storage, "Dependencies must still be injected when Deconstruct runs");

            // Dispose the context -> container runs [Deconstruct] hooks in Order.
            ctx.Dispose();

            Assert.AreEqual(new List<int> { 0, 5, 10 }, svc.Calls,
                "Deconstruct hooks must run in ascending Order before disposal");
        }

        // ------------------------------------------------------------------
        // CT — [Construct] selects the preferred (injected) constructor
        // ------------------------------------------------------------------

        [Test]
        public void Construct_Attribute_SelectsPreferredCtor()
        {
            using var ctx = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.BindInstance(new ETStorage { Label = "ctor-injected" });
                    builder.Bind<ETConstructConsumer>();
                });

            var consumer = ctx.Context.Resolve<ETConstructConsumer>();

            Assert.IsFalse(consumer.ParameterlessUsed, "Parameterless ctor must NOT be used");
            Assert.IsNotNull(consumer.ViaCtor, "Injected ctor must be used");
            Assert.AreEqual("ctor-injected", consumer.ViaCtor.Label);
        }

        // ------------------------------------------------------------------
        // ON — .Once() one-shot commands fire exactly once then unregister
        // ------------------------------------------------------------------

        [Test]
        public void Once_SyncCommand_FiresExactlyOnce_ThenUnregisters()
        {
            ETOnceCommand.Executions = 0;

            using var ctx = NexusTestHarness.CreateContext(
                builder => builder.BindCommandOnce<ETOnceSignal, ETOnceCommand>());

            var bus = ctx.Context.SignalBus;

            bus.Fire(new ETOnceSignal(1));
            bus.Fire(new ETOnceSignal(2));

            Assert.AreEqual(1, ETOnceCommand.Executions, "One-shot must fire exactly once");
            Assert.IsFalse(bus.HasCommandHandler<ETOnceSignal>(), "Handler must be truly unregistered");
        }

        [Test]
        public void Once_AsyncCommand_FiresExactlyOnce_ThenUnregisters()
        {
            ETOnceAsyncCommand.Executions = 0;

            using var ctx = NexusTestHarness.CreateContext(
                builder => builder.BindAsyncCommandOnce<ETOnceAsyncSignal, ETOnceAsyncCommand>());

            var bus = ctx.Context.SignalBus;

            bus.FireAsync(new ETOnceAsyncSignal(1)).GetAwaiter().GetResult();
            bus.FireAsync(new ETOnceAsyncSignal(2)).GetAwaiter().GetResult();

            Assert.AreEqual(1, ETOnceAsyncCommand.Executions, "Async one-shot must fire exactly once");
            Assert.IsFalse(bus.HasCommandHandler<ETOnceAsyncSignal>(), "Async handler must be truly unregistered");
        }
    }
}
