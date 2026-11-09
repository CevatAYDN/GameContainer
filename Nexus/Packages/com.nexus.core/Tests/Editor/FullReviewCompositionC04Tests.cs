using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC04Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void FailedConfiguration_DisposesItsLifecyclesAndExternalSubscriptions()
        {
            VerifyFailedConfiguration(retry: false);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void RetriedConfiguration_KeepsFailedAttemptOwners_AndDisposesSharedLifecycleOnce()
        {
            VerifyFailedConfiguration(retry: true);
        }

        private static void VerifyFailedConfiguration(bool retry)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            data.AssemblyScopes = new[] { typeof(object).Assembly.GetName().Name };
            var source = new EventSource();
            var disposed = new List<string>();
            var first = new LifecycleProbe("first", source, disposed);
            var second = new LifecycleProbe("second", source, disposed) { FailConfigure = true };
            var replacement = new LifecycleProbe("replacement", source, disposed);
            using var context = new Context(contextData: data);
            try
            {
                bool failed = false;
                try { context.Configure(new IContextLifecycle[] { first, second }); }
                catch (InvalidOperationException ex) when (ex.Message == "configuration probe") { failed = true; }
                Require(failed && !context.IsInjectionReady, "Failed configuration must not publish readiness.");
                if (retry)
                {
                    context.Configure(new IContextLifecycle[] { first, replacement });
                    Require(context.IsInjectionReady, "The documented configuration retry must still succeed.");
                }

                context.Dispose();
                context.Dispose();
                Require(first.Disposals == 1 && second.Disposals == 1,
                    $"Failed-attempt owners must end once; observed first={first.Disposals}, second={second.Disposals}.");
                Require(string.Join(",", disposed) == (retry ? "replacement,second,first" : "second,first"),
                    "Lifecycle cleanup must run in reverse ownership order without duplicate disposal.");
                source.Raise();
                Require(first.Notifications == 0 && second.Notifications == 0 && replacement.Notifications == 0,
                    "Context disposal left a lifecycle's external subscription alive.");
            }
            finally
            {
                context.Dispose();
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class EventSource
        {
            public event Action Changed;
            public void Raise() => Changed?.Invoke();
        }

        private sealed class LifecycleProbe : IContextLifecycle
        {
            private readonly string _name;
            private readonly EventSource _source;
            private readonly List<string> _disposed;
            private bool _subscribed;
            public int Disposals, Notifications;
            public bool FailConfigure;
            public LifecycleProbe(string name, EventSource source, List<string> disposed)
            {
                _name = name;
                _source = source;
                _disposed = disposed;
            }
            public void OnConfigure(IContextBuilder builder)
            {
                if (!_subscribed)
                {
                    _source.Changed += OnChanged;
                    _subscribed = true;
                }
                if (FailConfigure) throw new InvalidOperationException("configuration probe");
            }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose()
            {
                Disposals++;
                _disposed.Add(_name);
                _source.Changed -= OnChanged;
                _subscribed = false;
            }
            private void OnChanged() => Notifications++;
        }
    }
}
