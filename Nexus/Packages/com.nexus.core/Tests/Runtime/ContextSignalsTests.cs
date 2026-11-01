using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// Issue #70: nothing fired once a context had started and no runtime type forwarded OnApplicationPause, so Wickling
    /// fired its own AppStartedSignal from OnStartAsync and turned OnApplicationPause into its own signals, guarded
    /// because Android reports pause before the context has started.
    /// </summary>
    public sealed class ContextSignalsTests
    {
        private static readonly List<string> Log = new List<string>();

        public interface IStartLogger { }

        public sealed class StartLogger : NexusService<IStartLogger>, IStartLogger
        {
            public override ValueTask InitializeAsync(CancellationToken ct) { Log.Add("service init"); return default; }
        }

        private sealed class LoggingLifecycle : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) => builder.BindService<IStartLogger, StartLogger>();
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) { Log.Add("on start"); return default; }
            public void OnDispose() { }
        }

        private sealed class GatedStart : IContextLifecycle
        {
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => new ValueTask(Release.Task);
            public void OnDispose() { }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var root in UnityEngine.Object.FindObjectsByType<Root>(FindObjectsInactive.Include))
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            Root.ClearRegistry();
        }

        [Test]
        public async Task ContextStartedFiresOnceAfterServicesAndOnStartAsync()
        {
            Log.Clear();
            var context = ContextFactory.Create();
            context.Container.BindInstance<IContextLifecycle>(new LoggingLifecycle());
            context.SignalBus.Subscribe<ContextStartedSignal>(s => Log.Add($"started initialized={context.IsInitialized}"));
            context.Configure();

            await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);
            context.Dispose();

            Assert.That(Log, Is.EqualTo(new[] { "service init", "on start", "started initialized=False" }));
        }

        [UnityTest]
        public IEnumerator PauseAndResumeReachTheContextOnlyAfterItHasStarted()
        {
            Root.ClearRegistry();
            var gate = new GatedStart();
            var go = new GameObject("PauseRoot");
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = "PauseRoot";
            data.AssemblyScopes = Array.Empty<string>();
            root.SetUp(data);
            root.RegisterLifecycle(gate);
            go.SetActive(true);

            var seen = new List<bool>();
            root.Context.SignalBus.Subscribe<ApplicationPauseSignal>(s => seen.Add(s.IsPaused));
            go.SendMessage("OnApplicationPause", true);

            gate.Release.SetResult(true);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!root.IsInitialized && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(root.IsInitialized, Is.True, "the root did not start in 5 s");
            go.SendMessage("OnApplicationPause", false);

            Assert.That(seen, Is.EqualTo(new[] { false }));
        }

        // Issue #199: Root forwarded only the pause, so Gigawatt and ChipCrisis hooked OnApplicationQuit and Application.lowMemory
        // themselves, each with its own guard against a context that has not started
        [UnityTest]
        public IEnumerator QuitAndLowMemoryReachTheContextOnlyAfterItHasStarted()
        {
            Root.ClearRegistry();
            var gate = new GatedStart();
            var go = new GameObject("QuitRoot");
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = "QuitRoot";
            data.AssemblyScopes = Array.Empty<string>();
            root.SetUp(data);
            root.RegisterLifecycle(gate);
            go.SetActive(true);

            var seen = new List<string>();
            root.Context.SignalBus.Subscribe<ApplicationQuitSignal>(_ => seen.Add("quit"));
            root.Context.SignalBus.Subscribe<LowMemorySignal>(_ => seen.Add("low memory"));
            go.SendMessage("OnLowMemory");
            go.SendMessage("OnApplicationQuit");

            gate.Release.SetResult(true);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!root.IsInitialized && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(root.IsInitialized, Is.True, "the root did not start in 5 s");
            go.SendMessage("OnLowMemory");
            go.SendMessage("OnApplicationQuit");

            Assert.That(seen, Is.EqualTo(new[] { "low memory", "quit" }));
        }
    }
}
