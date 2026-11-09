using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC07Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public Task NamedLazyService_InitializesOnceWithItsParentLifetime()
            => VerifyNamedOwnership(bindChildDefault: false);

#if UNITY_EDITOR
        [Test]
#endif
        public Task NamedLazyService_IgnoresChildDefaultBindingsWhenFindingItsOwner()
            => VerifyNamedOwnership(bindChildDefault: true);

        private static async Task VerifyNamedOwnership(bool bindChildDefault)
        {
            Context parent = null, first = null, second = null;
            try
            {
                parent = await ContextFactory.StartAsync("C07-parent",
                    builder => builder.Bind<CountingService>("shared"));
                Action<IContextBuilder> configureChild = builder =>
                {
                    if (bindChildDefault) builder.BindService<CountingService>();
                };
                first = await ContextFactory.StartAsync("C07-first", configureChild, parent);
                second = await ContextFactory.StartAsync("C07-second", configureChild, parent);

                var service = first.Container.CreateLazyInjection<CountingService>("shared").Value;
                var same = second.Container.CreateLazyInjection<CountingService>("shared").Value;
                Require(ReferenceEquals(service, same), "Both children must resolve the parent's named instance.");
                Require(service.Initializations == 1,
                    $"The named service must initialize once, observed {service.Initializations}.");
                Require(service.InitializationToken == parent.LifetimeToken,
                    "Lazy initialization used a requesting child's lifetime instead of the binding owner's.");
                await first.DisposeAsync();
                await second.DisposeAsync();
                Require(service.Disposals == 0 && !service.InitializationToken.IsCancellationRequested,
                    "Ending a child must not end the parent-owned service's lifetime.");
                await parent.DisposeAsync();
                Require(service.Disposals == 1 && service.InitializationToken.IsCancellationRequested,
                    "The parent must cancel and dispose its named service exactly once.");
            }
            finally
            {
                if (second != null) await second.DisposeAsync();
                if (first != null) await first.DisposeAsync();
                if (parent != null) await parent.DisposeAsync();
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class CountingService : INexusService
        {
            public int Initializations, Disposals;
            public CancellationToken InitializationToken;
            public CountingService() { }
            public ValueTask InitializeAsync(CancellationToken ct)
            {
                Initializations++;
                InitializationToken = ct;
                return default;
            }
            public void OnDispose() => Disposals++;
        }
    }
}
