using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #148: <see cref="IResolver"/> resolved only by a generic argument, so a game resolved a runtime Type through
    /// reflection, which finds nothing once <c>TryResolve</c> moved to the base interface. Issue #149: the builder could not
    /// bind a service to a delegate, so a game reflected into <c>ContextBuilder._container</c>.
    /// </summary>
    public sealed class ResolverAndFactoryBindingTests
    {
        public interface IUnbound { }
        public sealed class Bound { }
        public sealed class BoundInTheParent { }

        public class Owned : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }
        public sealed class AppWide : Owned { }
        public sealed class PerContext : Owned { }
        public sealed class PerResolve : Owned { }

        public interface ISettingsBacked { }

        public sealed class NeedsSettings : INexusService
        {
            public NeedsSettings(ISettingsBacked settings) { }
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        [Test]
        public void AResolverResolvesARuntimeType_AsTheGenericFormsDo_ByName_AndFromAParentContext()
        {
            using var parent = ContextFactory.Create();
            using var child = ContextFactory.Create(parent);
            parent.Container.Bind<BoundInTheParent>();
            child.Container.Bind<Bound>();
            var named = new Bound();
            child.Container.BindInstance("special", named);
            IResolver resolver = child;

            Assert.AreSame(resolver.Resolve<Bound>(), resolver.Resolve(typeof(Bound)));
            Assert.AreSame(resolver.Resolve<Bound>(), resolver.TryResolve(typeof(Bound)));
            Assert.IsNull(resolver.TryResolve(typeof(IUnbound)));
            Assert.Throws<InvalidOperationException>(() => resolver.Resolve(typeof(IUnbound)));
            Assert.AreSame(named, resolver.TryResolve(typeof(Bound), "special"));
            Assert.IsNull(resolver.TryResolve(typeof(Bound), "other"));
            Assert.AreSame(parent.Resolve<BoundInTheParent>(), resolver.TryResolve(typeof(BoundInTheParent)));
        }

        [Test]
        public async Task ABuilderFactory_RunsItsDelegateOncePerLifetime_AndItsContainerDisposesWhatItOwns()
        {
            int appWideRuns = 0, perContextRuns = 0, perResolveRuns = 0;
            var parent = ContextFactory.Create();
            try
            {
                var child = await ContextFactory.StartAsync("factory-lifetimes", builder =>
                {
                    builder.BindFactory(() => { appWideRuns++; return new AppWide(); }, Lifetime.Singleton);
                    builder.BindFactory(() => { perContextRuns++; return new PerContext(); }, Lifetime.Scoped);
                    builder.BindFactory(() => { perResolveRuns++; return new PerResolve(); }, Lifetime.Transient);
                    builder.BindFactory("second", () => new PerContext(), Lifetime.Scoped);
                }, parent);

                var appWide = child.Resolve<AppWide>();
                Assert.AreSame(appWide, parent.Resolve<AppWide>(), "a Singleton factory is bound on the root container");
                var perContext = child.Resolve<PerContext>();
                Assert.AreSame(perContext, child.Resolve<PerContext>());
                Assert.IsNull(parent.TryResolve<PerContext>());
                var named = child.TryResolve<PerContext>("second");
                Assert.IsNotNull(named);
                Assert.AreNotSame(perContext, named);
                var firstPerResolve = child.Resolve<PerResolve>();
                Assert.AreNotSame(firstPerResolve, child.Resolve<PerResolve>());
                Assert.AreEqual((1, 1, 2), (appWideRuns, perContextRuns, perResolveRuns));

                await child.DisposeAsync();
                Assert.IsTrue(perContext.Disposed, "the context disposes its Scoped factory's instance");
                Assert.IsFalse(firstPerResolve.Disposed, "the container never owns a Transient factory's result");
                Assert.IsFalse(appWide.Disposed, "the Singleton lives on the root until the root is disposed");
            }
            finally { parent.Dispose(); }
        }

        [Test]
        public async Task ABuilderFactoryThatThrows_FailsTheStartThatResolvesIt_AndTheErrorLogNamesTheBoundType()
        {
            int before = NexusRuntime.ActiveContexts.Count;
            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape($"'{typeof(ISettingsBacked).FullName}' threw InvalidOperationException: no settings asset")));
            try
            {
                await ContextFactory.StartAsync("factory-throws", builder =>
                {
                    builder.BindFactory<ISettingsBacked>(() => throw new InvalidOperationException("no settings asset"), Lifetime.Scoped);
                    builder.BindService<NeedsSettings>();
                });
                Assert.Fail("The start must fail when a service needs the factory's instance.");
            }
            catch (InvalidOperationException ex) { Assert.AreEqual("no settings asset", ex.Message); }
            Assert.AreEqual(before, NexusRuntime.ActiveContexts.Count);
        }
    }
}
