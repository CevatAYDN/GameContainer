using System;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC02Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void GenericAliasesAddedByChild_KeepTheExistingRootSingleton()
        {
            VerifyAliases(scope => scope.BindMultiple<IFirst, ISecond, Shared>(Lifetime.Singleton));
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReflectionAliasesAddedByChild_KeepTheExistingRootSingleton()
        {
            VerifyAliases(scope => scope.BindMultiple(
                new[] { typeof(IFirst), typeof(ISecond) }, typeof(Shared), Lifetime.Singleton));
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FluentAliasesAddedByChild_KeepTheExistingRootSingleton()
        {
            VerifyAliases(scope => scope.BindFluent<IFirst>().To<Shared>().AsSingleton().AsImplementedInterfaces(),
                includesConcrete: false);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FluentAliasesMovedToChildScope_RemoveOnlyTheNewRootAlias()
        {
            using var root = new NexusDI();
            root.Bind<IFirst, Shared>(Lifetime.Singleton);
            var original = (Shared)root.Resolve<IFirst>();
            Shared scoped;
            using (var child = root.CreateChildScope())
            {
                child.BindFluent<IFirst>().To<Shared>()
                    .AsSingleton().AsImplementedInterfaces().AsScoped();

                scoped = (Shared)child.Resolve<IFirst>();
                Require(ReferenceEquals(scoped, child.Resolve<ISecond>()),
                    "The child interfaces must share one scoped instance.");
                Require(!ReferenceEquals(original, scoped),
                    "The child scoped instance must be distinct from the original root singleton.");
                Require(root.IsRegistered(typeof(IFirst)) && ReferenceEquals(original, root.Resolve<IFirst>()),
                    "Moving the fluent chain must preserve the original root registration and singleton.");
                Require(!root.IsRegistered(typeof(ISecond)),
                    "The alias introduced by the fluent chain must leave the root when the chain becomes scoped.");
                Require(root.TryResolve<ISecond>() == null,
                    "The removed root alias must not resolve the retained singleton.");
            }
            Require(scoped.Disposals == 1, "The child must dispose its scoped instance exactly once.");
            Require(original.Disposals == 0, "The child must not dispose the retained root singleton.");
            root.Dispose();
            Require(original.Disposals == 1, "The root must still own and dispose its original singleton exactly once.");
            Require(scoped.Disposals == 1, "Root disposal must not dispose the child instance again.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FluentAliasesMovedToChildScope_PreservePreExistingRootAliases()
        {
            using var root = new NexusDI();
            root.BindMultiple<IFirst, ISecond, Shared>(Lifetime.Singleton);
            var original = (Shared)root.Resolve<IFirst>();
            using (var child = root.CreateChildScope())
            {
                child.BindFluent<IFirst>().To<Shared>()
                    .AsSingleton().AsImplementedInterfaces().AsScoped();

                Require(ReferenceEquals(original, root.Resolve<IFirst>()),
                    "The pre-existing first root interface must retain its singleton.");
                Require(ReferenceEquals(original, root.Resolve<ISecond>()),
                    "The pre-existing second root interface must retain its singleton.");
                Require(ReferenceEquals(original, root.Resolve<Shared>()),
                    "The pre-existing concrete registration must remain intact.");
                var scoped = child.Resolve<IFirst>();
                Require(!ReferenceEquals(original, scoped) && ReferenceEquals(scoped, child.Resolve<ISecond>()),
                    "The child interfaces must share a separate scoped instance.");
            }
            Require(original.Disposals == 0, "Child disposal must leave the pre-existing root alias group alive.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FluentAliasesMovedToRoot_StopShadowingTheRetainedSingleton()
        {
            using var root = new NexusDI();
            root.Bind<IFirst, Shared>(Lifetime.Singleton);
            var original = (Shared)root.Resolve<IFirst>();
            Shared scoped;
            using (var child = root.CreateChildScope())
            {
                var chain = child.BindFluent<IFirst>().To<Shared>().AsScoped().AsImplementedInterfaces();
                scoped = (Shared)child.Resolve<IFirst>();
                Require(!ReferenceEquals(original, scoped), "The initial child binding must own a separate instance.");

                chain.AsSingleton();

                Require(ReferenceEquals(original, child.Resolve<IFirst>()),
                    "The old child binding must not shadow the retained root singleton.");
                Require(ReferenceEquals(original, child.Resolve<ISecond>()),
                    "The old child alias must not shadow the new root alias.");
                Require(ReferenceEquals(original, root.Resolve<ISecond>()),
                    "The new root alias must share the original singleton.");
            }
            Require(scoped.Disposals == 1, "The child must still dispose the scoped instance it previously created.");
            Require(original.Disposals == 0, "Moving to the root must not transfer its singleton's disposal to the child.");
        }

        private static void VerifyAliases(Action<NexusDI> addAliases, bool includesConcrete = true)
        {
            using var root = new NexusDI();
            root.Bind<IFirst, Shared>(Lifetime.Singleton);
            var original = (Shared)root.Resolve<IFirst>();
            using (var child = root.CreateChildScope())
            {
                addAliases(child);
                Require(ReferenceEquals(original, child.Resolve<IFirst>()), "The existing alias changed instance.");
                Require(ReferenceEquals(original, child.Resolve<ISecond>()), "The added alias constructed a second singleton.");
                if (includesConcrete)
                    Require(ReferenceEquals(original, child.Resolve<Shared>()), "The concrete key split from its aliases.");
            }
            Require(original.Disposals == 0, "A child must not dispose the root singleton.");
            root.Dispose();
            Require(original.Disposals == 1, "The root must dispose the shared singleton exactly once.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private interface IFirst { }
        private interface ISecond { }
        private sealed class Shared : IFirst, ISecond, IDisposable
        {
            public int Disposals;
            public Shared() { }
            public void Dispose() => Disposals++;
        }
    }
}
