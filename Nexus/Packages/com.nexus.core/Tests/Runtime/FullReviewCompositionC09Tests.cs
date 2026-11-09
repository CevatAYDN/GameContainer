using System;
using System.Collections;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    public sealed class FullReviewCompositionC09Tests
    {
        [UnityTest]
        public IEnumerator RegistrationObserverDisposal_DestroysNativeSettingsByNextFrame()
        {
            const string scope = "C09-native-registration-disposal";
            Context observed = null;
            ContextData settings = null;
            Action<IContext> observer = owner =>
            {
                if (owner.ScopeTag != scope) return;
                observed = (Context)owner;
                settings = observed.ContextData;
                owner.Dispose();
            };
            NexusRuntime.OnContextRegistered += observer;
            try
            {
                var startup = NexusRuntime.CreatePureContextAsync(scope);
                Assert.IsTrue(startup.IsCompleted, "Disposed registration must not continue asynchronous startup.");
                Assert.Throws<ObjectDisposedException>(() => startup.GetAwaiter().GetResult());
                Assert.IsFalse(ReferenceEquals(settings, null), "Capture the actual runtime-created ScriptableObject.");
                Assert.IsTrue(observed.IsDisposed);
                yield return null;
                Assert.IsFalse(settings, "The factory's native settings survived the destruction frame.");
                foreach (var active in NexusRuntime.ActiveContexts)
                    Assert.AreNotSame(observed, active, "The failed context remained registered.");
            }
            finally
            {
                NexusRuntime.OnContextRegistered -= observer;
                observed?.Dispose();
                if (settings) UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
