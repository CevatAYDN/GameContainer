using System;
using System.Runtime.CompilerServices;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class SaveThrottlerRetentionTests
    {
        private sealed class Clock : ITimeProvider { public float Now { get; set; } }
        private sealed class Owner
        {
            public int Saves;
            public void Save() => Saves++;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference UnrootedControl() => new WeakReference(new object());

#if UNITY_EDITOR
        [Test]
#endif
        public void TickThenDispose_ReleasesCapturedOwner_WithoutAnotherTick()
        {
            var control = UnrootedControl();
            var retained = MakeDisposedOwner(false);
            Collect();
            Require(!control.IsAlive, "the collector control must be collectible before judging retention");
            try { Require(!retained.IsAlive, "the last tick buffer still retains the disposed save owner"); }
            finally { new SaveThrottler().Tick(0); }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ThrowingSaveThenDispose_AlsoReleasesCapturedOwner()
        {
#if UNITY_EDITOR
            LogAssert.Expect(LogType.Warning, new Regex("Save execution failed.*save retention probe"));
#endif
            var retained = MakeDisposedOwner(true);
            Collect();
            try { Require(!retained.IsAlive, "a failed save left its owner in the tick buffer after disposal"); }
            finally { new SaveThrottler().Tick(0); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference MakeDisposedOwner(bool failAtTick)
        {
            var clock = new Clock();
            var throttler = new SaveThrottler { TimeProvider = clock };
            var owner = new Owner();
            var weak = new WeakReference(owner);
            Action save = () =>
            {
                owner.Save();
                if (failAtTick && owner.Saves == 2) throw new InvalidOperationException("save retention probe");
            };
            throttler.TryRequestSave("owner", save);
            throttler.TryRequestSave("owner", save);
            clock.Now = 3;
            throttler.Tick(0);
            Require(owner.Saves == 2, "the pending save must actually execute before the retention check");
            throttler.OnDispose();
            return weak;
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void NestedTick_PreservesOuterWork_AndReleasesBothBuffers()
        {
            var owners = MakeNestedDisposedOwners();
            Collect();
            try
            {
                foreach (var owner in owners)
                    Require(!owner.IsAlive, "a nested/outer tick buffer retained a disposed owner");
            }
            finally { new SaveThrottler().Tick(0); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] MakeNestedDisposedOwners()
        {
            var clock = new Clock();
            var outer = new SaveThrottler { TimeProvider = clock };
            var inner = new SaveThrottler { TimeProvider = clock };
            var first = new Owner();
            var last = new Owner();
            var nested = new Owner();
            Action firstSave = () =>
            {
                first.Save();
                if (first.Saves == 2) inner.Tick(0);
            };
            inner.TryRequestSave("inner", nested.Save);
            inner.TryRequestSave("inner", nested.Save);
            outer.TryRequestSave("first", firstSave);
            outer.TryRequestSave("first", firstSave);
            outer.TryRequestSave("last", last.Save);
            outer.TryRequestSave("last", last.Save);
            clock.Now = 3;
            outer.Tick(0);
            Require(first.Saves == 2 && last.Saves == 2 && nested.Saves == 2,
                "nested Tick must not clear or replace the remaining outer work");
            outer.OnDispose();
            inner.OnDispose();
            return new[] { new WeakReference(first), new WeakReference(last), new WeakReference(nested) };
        }
    }
}
