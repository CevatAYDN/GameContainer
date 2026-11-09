using System;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC08Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void RejectedFactoryUpdate_PreservesThePublishedValue()
        {
            var binder = new NexusBinder<string, object>();
            var original = new object();
            var binding = binder.Bind("item").To(original);
            RejectNullFactory(binding);
            Require(ReferenceEquals(original, binder.Get("item")), "A rejected update changed the published value.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void RejectedFactoryUpdate_PreservesTheChainForALaterNamedBinding()
        {
            var binder = new NexusBinder<string, object>();
            var original = new object();
            var binding = binder.Bind("item").To(original);
            RejectNullFactory(binding);
            binding.ToName("copy");
            Require(ReferenceEquals(original, binder.Get("item", "copy")),
                "A failed update left the fluent chain in a corrupt state.");
            Require(ReferenceEquals(original, binder.Get("item")), "Naming the surviving value changed its default.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void NamedUpdates_KeepPreviouslyPublishedNamesAndTheDefault()
        {
            var binder = new NexusBinder<string, object>();
            var original = new object();
            var first = new object();
            var second = new object();
            binder.Bind("item").To(original).ToName("first").To(first).ToName("second").ToFactory(() => second);
            Require(ReferenceEquals(original, binder.Get("item")), "The default value changed during named updates.");
            Require(ReferenceEquals(first, binder.Get("item", "first")), "Updating another name changed the first entry.");
            Require(ReferenceEquals(second, binder.Get("item", "second")), "The new named factory was not published.");
        }

        private static void RejectNullFactory(IBinderBindingBuilder<string, object> binding)
        {
            bool rejected = false;
            try { binding.ToFactory(null); }
            catch (ArgumentNullException) { rejected = true; }
            Require(rejected, "A null factory must be rejected.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
