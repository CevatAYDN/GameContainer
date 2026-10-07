using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nexus.Core.Tests
{
    public class OptionalDiFeatureRegressionTests
    {
        public sealed class Dependency { }
        public sealed class NamedConsumer
        {
            public NamedConsumer([Inject(Name = "late")] Dependency value) { }
        }
        // Value equality must not collapse two independently owned services.
        public sealed class EqualService : INexusService
        {
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() { }
            public override bool Equals(object obj) => obj is EqualService;
            public override int GetHashCode() => 1;
        }

        [Test]
        public void NamedRegistrationAfterMiss_PreservesDefaultAndParentOwnership()
        {
            using var parent = new NexusDI();
            using var child = parent.CreateChildScope();
            var normal = new Dependency(); var named = new Dependency();
            parent.BindInstance(normal);
            Assert.IsNull(child.TryResolve<Dependency>("late"));
            Assert.Throws<InvalidOperationException>(() => child.Resolve<Dependency>("late"));
            parent.BindInstance("late", named);
            Assert.AreSame(named, child.Resolve<Dependency>("late"));
            Assert.AreSame(normal, child.Resolve<Dependency>());
            parent.Dispose();
            Assert.IsFalse(child.IsRegistered(typeof(Dependency), "late"));
            Assert.IsNull(child.TryResolve<Dependency>("late"));
            Assert.Throws<InvalidOperationException>(() => child.Resolve<Dependency>("late"));
        }

        [Test]
        public void ConcurrentFirstNamedRegistrations_AllRemainResolvable()
        {
            using var di = new NexusDI();
            var instances = new Dependency[64];
            Parallel.For(0, instances.Length, index =>
            {
                instances[index] = new Dependency();
                di.BindInstance(index.ToString(), instances[index]);
            });
            for (int index = 0; index < instances.Length; index++)
                Assert.AreSame(instances[index], di.Resolve<Dependency>(index.ToString()));
        }

        [Test]
        public void CrossBoundaryRegistrationAfterMiss_UsesOwningParent()
        {
            using var parent = new NexusDI();
            using var child = parent.CreateChildScope();
            Assert.Throws<InvalidOperationException>(() => child.ResolveCrossBoundary(typeof(Dependency)));
            parent.BindCrossBoundary<Dependency>();
            Assert.AreSame(parent.Resolve<Dependency>(), child.ResolveCrossBoundary(typeof(Dependency)));
        }

        [Test]
        public void ValidationAfterLateNamedRegistration_RechecksMissingDependency()
        {
            using var di = new NexusDI();
            di.Bind<NamedConsumer>(Lifetime.Transient);
            Assert.IsTrue(di.ValidateBindings().Any(issue => issue.IssueType == DiValidationIssueType.MissingConstructorDependency));
            di.BindInstance("late", new Dependency());
            Assert.IsEmpty(di.ValidateBindings());
        }

        [Test]
        public void TypeResolveAfterLateLazyMark_QueuesExistingServiceOnce()
        {
            using var di = new NexusDI();
            var service = new EqualService();
            di.BindInstance(service);
            Assert.AreSame(service, di.Resolve<EqualService>());
            Assert.IsTrue(di._lazyServicesPendingInit.IsEmpty);
            di.MarkLazyService(typeof(EqualService));
            di.Resolve(typeof(EqualService)); di.Resolve(typeof(EqualService));
            Assert.IsTrue(di._lazyServicesPendingInit.TryDequeue(out var queued));
            Assert.AreSame(service, queued);
            Assert.IsTrue(di._lazyServicesPendingInit.IsEmpty);
        }

        [Test]
        public void ConcurrentFirstLazyNotifications_QueueEachReferenceOnce()
        {
            using var di = new NexusDI();
            var first = new EqualService(); var second = new EqualService();
            int notifications = 0;
            di.LazyServiceResolvedCallback = _ => Interlocked.Increment(ref notifications);
            Parallel.For(0, 64, index => di.NotifyLazyServiceResolved(typeof(EqualService), index % 2 == 0 ? first : second));
            Assert.AreEqual(2, notifications);
            var queued = di._lazyServicesPendingInit.ToArray();
            Assert.AreEqual(2, queued.Length);
            Assert.AreEqual(1, queued.Count(value => ReferenceEquals(value, first)));
            Assert.AreEqual(1, queued.Count(value => ReferenceEquals(value, second)));
        }
    }
}
