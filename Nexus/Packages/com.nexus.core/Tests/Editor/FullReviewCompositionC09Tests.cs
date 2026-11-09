using System;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC09Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public async Task RegistrationObserverDisposal_ReleasesPureContextSettings()
        {
            const string scope = "C09-registration-disposal";
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
                Exception failure = null;
                try { await NexusRuntime.CreatePureContextAsync(scope); }
                catch (Exception ex) { failure = ex; }
                Require(failure is ObjectDisposedException, "Registration-time disposal must fail startup.");
                Require(observed != null && observed.IsDisposed && !ReferenceEquals(settings, null),
                    "The observer must end a context with runtime-created settings.");
                Require(!settings, "Runtime-created settings survived registration-time disposal.");
                foreach (var active in NexusRuntime.ActiveContexts)
                    Require(!ReferenceEquals(active, observed), "The failed context remained registered.");
            }
            finally
            {
                NexusRuntime.OnContextRegistered -= observer;
                observed?.Dispose();
                if (settings) UnityEngine.Object.DestroyImmediate(settings);
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ContextDisposal_LeavesCallerOwnedSettingsAlive()
        {
            var settings = ScriptableObject.CreateInstance<ContextData>();
            try
            {
                using var context = new Context(contextData: settings);
                context.Dispose();
                Require(settings, "Caller-owned settings must not be destroyed with a context.");
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
