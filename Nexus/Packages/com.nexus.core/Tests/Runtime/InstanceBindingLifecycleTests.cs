using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// Issue #69: a model or service the game builds itself (its constructor takes game values) was bound with
    /// BindInstance / BindModelInstance, which only stored it: no [Inject] members, no OnBind, no InitializeAsync.
    /// Wickling moved its services' start-up into constructors; ChipCrisis's RatingModel could not fire its own signal.
    /// </summary>
    public sealed class InstanceBindingLifecycleTests
    {
        private static readonly List<string> Log = new List<string>();

        public interface IFirst { }
        public interface ISecond { }

        public sealed class BuiltService : NexusService<IFirst>, IFirst
        {
            public override ValueTask InitializeAsync(CancellationToken ct) { Log.Add("init built"); return default; }
            public override void OnDispose() => Log.Add("dispose built");
        }

        public sealed class PrebuiltService : NexusService<ISecond>, ISecond
        {
            private readonly int _value;
            public PrebuiltService(int value) { _value = value; }
            [Inject] public IFirst First { get; set; }

            public override ValueTask InitializeAsync(CancellationToken ct)
            {
                Log.Add($"init prebuilt {_value} first={First != null} bus={SignalBus != null}");
                return default;
            }

            public override void OnDispose() => Log.Add("dispose prebuilt");
        }

        public interface IScore { }

        public sealed class ScoreModel : IScore, IReactiveModel
        {
            [Inject] public ISignalPublisher Signals { get; set; }
            public bool BoundWithSignals;
            public int Binds;

            public ValueTask OnBind(CancellationToken ct)
            {
                Binds++;
                BoundWithSignals = Signals != null;
                return default;
            }
        }

        private sealed class Configure : IContextLifecycle
        {
            private readonly Action<IContextBuilder> _configure;
            public Configure(Action<IContextBuilder> configure) { _configure = configure; }
            public void OnConfigure(IContextBuilder builder) => _configure(builder);
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private static async Task Run(Action<IContextBuilder> configure)
        {
            var context = ContextFactory.Create();
            context.Container.BindInstance<IContextLifecycle>(new Configure(configure));
            context.Configure();
            await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);
            context.Dispose();
        }

        [Test]
        public async Task AServiceTheGameBuiltIsInjectedInitializedAndDisposedInBindOrder()
        {
            Log.Clear();

            await Run(b =>
            {
                b.BindService<IFirst, BuiltService>();
                b.BindServiceInstance<ISecond>(new PrebuiltService(7));
            });

            Assert.That(Log, Is.EqualTo(new[] { "init built", "init prebuilt 7 first=True bus=True", "dispose prebuilt", "dispose built" }));
        }

        [Test]
        public void BindServiceInstanceRefusesAnInstanceThatIsNotAService()
        {
            using var container = new NexusDI();
            var builder = new ContextBuilder(container, null);
            Assert.Throws<ArgumentException>(() => builder.BindServiceInstance<IScore>(new ScoreModel()));
        }

        [Test]
        public async Task AReactiveModelTheGameBuiltIsInjectedBeforeItsOnBind()
        {
            var model = new ScoreModel();

            await Run(b => b.BindModelInstance<IScore>(model));

            Assert.That(model.Binds, Is.EqualTo(1));
            Assert.That(model.BoundWithSignals, Is.True);
        }
    }
}
