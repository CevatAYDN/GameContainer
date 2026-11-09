using System;
using System.Threading.Tasks;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC01Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void ReentrantContainerDispose_EndsAsyncSingletonAndKeepsDependenciesAvailable()
        {
            using var container = new NexusDI();
            VerifyReentrantDisposal(container, container.Dispose);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReentrantContainerDisposeDuringContextTeardown_EndsAsyncSingleton()
        {
            using var context = new Context();
            VerifyReentrantDisposal(context.Container, context.Dispose);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task DisposalWhileAsyncCleanupIsSuspended_PreservesTheOwningCleanup()
        {
            using var container = new NexusDI();
            var dependency = new AsyncResource();
            var consumer = new AsyncConsumer(container, dependency);
            container.BindInstance(dependency);
            container.BindInstance(consumer);
            var teardown = container.DisposeAsync().AsTask();
            try
            {
                Require(consumer.Disposals == 1 && !teardown.IsCompleted, "The regression must suspend real async cleanup.");
                container.Dispose();
                Require(ReferenceEquals(container.TryResolve<AsyncResource>(), dependency),
                    "A second disposal call closed the suspended cleanup's dependencies.");
            }
            finally
            {
                consumer.Release.TrySetResult(true);
                await teardown;
            }
            Require(consumer.DependencyAvailableAfterReentry, "Async reentry closed the owning callback's dependencies.");
            Require(consumer.Disposals == 1 && dependency.Disposals == 1, "Async cleanup must dispose each singleton once.");
        }

        private static void VerifyReentrantDisposal(NexusDI container, Action dispose)
        {
            var dependency = new AsyncResource();
            bool dependencyAvailableAfterReentry = false;
            var consumer = new ReentrantConsumer(() =>
            {
                container.Dispose();
                dependencyAvailableAfterReentry = ReferenceEquals(container.TryResolve<AsyncResource>(), dependency);
            });
            container.BindInstance(dependency);
            container.BindInstance(consumer);

            dispose();
            dispose();

            Require(dependency.Disposals == 1,
                $"The async-only dependency must be disposed once, observed {dependency.Disposals}.");
            Require(consumer.Disposals == 1, "The reentrant consumer must be disposed once.");
            Require(dependencyAvailableAfterReentry,
                "A nested Dispose must not close dependency resolution during the owning cleanup callback.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class AsyncResource : IAsyncDisposable
        {
            public int Disposals;
            public ValueTask DisposeAsync()
            {
                Disposals++;
                return default;
            }
        }

        private sealed class ReentrantConsumer : IDisposable
        {
            private readonly Action _onDispose;
            public int Disposals;
            public ReentrantConsumer(Action onDispose) => _onDispose = onDispose;
            public void Dispose()
            {
                Disposals++;
                _onDispose();
            }
        }

        private sealed class AsyncConsumer : IAsyncDisposable
        {
            private readonly NexusDI _container;
            private readonly AsyncResource _dependency;
            public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Disposals;
            public bool DependencyAvailableAfterReentry;
            public AsyncConsumer(NexusDI container, AsyncResource dependency)
            {
                _container = container;
                _dependency = dependency;
            }
            public async ValueTask DisposeAsync()
            {
                Disposals++;
                await Release.Task;
                await _container.DisposeAsync();
                DependencyAvailableAfterReentry = ReferenceEquals(_container.TryResolve<AsyncResource>(), _dependency);
            }
        }
    }
}
