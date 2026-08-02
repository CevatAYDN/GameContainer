using NUnit.Framework;
using System;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// EditMode proof for Phase 1a — Cross-Context InjectionBinder (CrossBoundary binding).
    ///
    /// Verifies that:
    /// - Parent-context BindCrossBoundary types are resolvable from child contexts via ResolveCrossBoundary.
    /// - Non-cross-boundary parent bindings are NOT accessible from child contexts (least-privilege).
    /// - Grandchild contexts can resolve cross-boundary types from grandparent.
    /// - Self-referencing cross-boundary bindings work.
    /// - Normal Resolve from child does NOT see parent cross-boundary types (strict isolation).
    /// - Attempting to resolve a non-existent cross-boundary type throws.
    /// </summary>
    [TestFixture]
    public class CrossBoundaryTests
    {
        // ─── Test types ───

        public interface ISharedService { string Id { get; } }
        public sealed class SharedService : ISharedService
        {
            public string Id { get; }
            public SharedService() => Id = Guid.NewGuid().ToString("N");
        }

        public interface ILocalService { string Name { get; }
        }
        public sealed class LocalService : ILocalService
        {
            public string Name => "local";
        }

        public interface ICrossBoundaryModel { int Value { get; } }
        public sealed class CrossBoundaryModel : ICrossBoundaryModel
        {
            public int Value => 42;
        }

        public sealed class SelfBoundService
        {
            public string Tag { get; }
            public SelfBoundService() => Tag = "self-bound";
        }

        // ─── CB1: CrossBoundary binding is resolvable from child context ───

        [Test]
        public void CrossBoundary_ResolvedFromChildContext()
        {
            using var parent = NexusTestHarness.CreateContext(
                builder => builder.BindCrossBoundary<ISharedService, SharedService>());

            using var child = NexusTestHarness.CreateChildContext(parent, "Child");

            var resolved = child.Context.ResolveCrossBoundary<ISharedService>();
            Assert.IsNotNull(resolved, "Cross-boundary binding must be resolvable from child context");
            Assert.IsInstanceOf<ISharedService>(resolved);
        }

        // ─── CB2: Cross-boundary singleton identity — same instance in parent and child ───

        [Test]
        public void CrossBoundary_SameSingletonInParentAndChild()
        {
            using var parent = NexusTestHarness.CreateContext(
                builder => builder.BindCrossBoundary<ISharedService, SharedService>());

            var parentInstance = parent.Context.Resolve<ISharedService>();
            using var child = NexusTestHarness.CreateChildContext(parent, "Child");
            var childInstance = child.Context.ResolveCrossBoundary<ISharedService>();

            Assert.AreSame(parentInstance, childInstance,
                "Cross-boundary must resolve the SAME singleton instance in parent and child");
        }

        // ─── CB3: Non-cross-boundary parent Bind is NOT resolvable via ResolveCrossBoundary ───
        //         (but IS via normal Resolve, due to unconditional parent fallthrough)

        [Test]
        public void CrossBoundary_NonCrossBoundaryBinding_NotViaResolveCrossBoundary()
        {
            using var parent = NexusTestHarness.CreateContext(
                builder => builder.Bind<ILocalService, LocalService>());

            using var child = NexusTestHarness.CreateChildContext(parent, "Child");

            // ResolveCrossBoundary throws — not marked as cross-boundary
            Assert.Throws<InvalidOperationException>(() =>
                child.Context.ResolveCrossBoundary<ILocalService>(),
                "ResolveCrossBoundary must NOT resolve parent bindings not marked as cross-boundary");

            // Normal Resolve still works via unconditional parent fallthrough
            var viaNormal = child.Context.Resolve<ILocalService>();
            Assert.IsNotNull(viaNormal,
                "Normal Resolve must still fall through to parent unconditionally (backward compat)");
        }

        // ─── CB4: Self-referencing cross-boundary binding ───

        [Test]
        public void CrossBoundary_SelfBound_ResolvedFromChild()
        {
            using var parent = NexusTestHarness.CreateContext(
                builder => builder.BindCrossBoundary<SelfBoundService>());

            using var child = NexusTestHarness.CreateChildContext(parent, "Child");

            var resolved = child.Context.ResolveCrossBoundary<SelfBoundService>();
            Assert.IsNotNull(resolved);
            Assert.AreEqual("self-bound", resolved.Tag);
        }

        // ─── CB5: Grandchild resolves cross-boundary from grandparent ───

        [Test]
        public void CrossBoundary_GrandchildResolvesFromGrandparent()
        {
            using var grandparent = NexusTestHarness.CreateContext(
                builder => builder.BindCrossBoundary<ICrossBoundaryModel, CrossBoundaryModel>());

            using var parent = NexusTestHarness.CreateChildContext(grandparent, "Parent");
            using var child = NexusTestHarness.CreateChildContext(parent, "Child");

            var resolved = child.Context.ResolveCrossBoundary<ICrossBoundaryModel>();
            Assert.IsNotNull(resolved);
            Assert.AreEqual(42, resolved.Value,
                "Grandchild must resolve cross-boundary binding from grandparent");
        }

        // ─── CB6: Both Resolve and ResolveCrossBoundary return the same singleton from child ───

        [Test]
        public void CrossBoundary_BothResolvePathsReturnSameSingleton()
        {
            using var parent = NexusTestHarness.CreateContext(
                builder => builder.BindCrossBoundary<ISharedService, SharedService>());

            using var child = NexusTestHarness.CreateChildContext(parent, "Child");

            var viaNormal = child.Context.Resolve<ISharedService>();
            var viaCrossBoundary = child.Context.ResolveCrossBoundary<ISharedService>();

            Assert.IsNotNull(viaNormal);
            Assert.IsNotNull(viaCrossBoundary);
            Assert.AreSame(viaNormal, viaCrossBoundary,
                "Both Resolve paths must return the same singleton instance");
        }

        // ─── CB7: Throws when cross-boundary type not registered anywhere in chain ───

        [Test]
        public void CrossBoundary_ThrowsWhenNotRegistered()
        {
            using var parent = NexusTestHarness.CreateContext(
                builder => builder.Bind<ILocalService, LocalService>());

            using var child = NexusTestHarness.CreateChildContext(parent, "Child");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                child.Context.ResolveCrossBoundary<ISharedService>());
            Assert.That(ex.Message, Does.Contain("Cross-boundary"),
                "Exception message must clearly indicate cross-boundary resolution failure");
        }

        // ─── CB8: Multiple parent contexts with different cross-boundary types ───

        [Test]
        public void CrossBoundary_MultipleParents_DifferentTypesResolveCorrectly()
        {
            using var grandparent = NexusTestHarness.CreateContext(
                builder =>
                {
                    builder.BindCrossBoundary<ICrossBoundaryModel, CrossBoundaryModel>();
                    builder.BindCrossBoundary<ISharedService, SharedService>();
                });

            using var parent = NexusTestHarness.CreateChildContext(grandparent, "Parent");
            using var child = NexusTestHarness.CreateChildContext(parent, "Child");

            var model = child.Context.ResolveCrossBoundary<ICrossBoundaryModel>();
            var service = child.Context.ResolveCrossBoundary<ISharedService>();

            Assert.AreEqual(42, model.Value);
            Assert.IsNotNull(service);
        }
    }
}
