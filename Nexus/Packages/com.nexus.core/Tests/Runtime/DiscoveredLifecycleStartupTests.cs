using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Core.Tests
{
    /// <summary>Found by convention: ScopeTag "DiscoveredStartup" + "Lifecycle".</summary>
    public sealed class DiscoveredStartupLifecycle : IContextLifecycle
    {
        public static int Configured, Initialized, Started;
        public static void ResetCounts() => Configured = Initialized = Started = 0;

        public void OnConfigure(IContextBuilder builder) => Configured++;
        public ValueTask OnInitializeAsync(CancellationToken ct) { Initialized++; return default; }
        public ValueTask OnStartAsync(CancellationToken ct) { Started++; return default; }
        public void OnDispose() { }
    }

    public sealed class ComponentStartupLifecycle : MonoBehaviour, IContextLifecycle
    {
        public int Configured, Initialized, Started;

        public void OnConfigure(IContextBuilder builder) => Configured++;
        public ValueTask OnInitializeAsync(CancellationToken ct) { Initialized++; return default; }
        public ValueTask OnStartAsync(CancellationToken ct) { Started++; return default; }
        public void OnDispose() { }
    }

    public sealed class DiscoveredLifecycleStartupTests
    {
        private const string ScopeTag = "DiscoveredStartup";
        private GameObject _go;
        private ContextData _data;

        [SetUp]
        public void SetUp()
        {
            NexusRuntime.Reset();
            DiscoveredStartupLifecycle.ResetCounts();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_data != null) Object.DestroyImmediate(_data);
            NexusRuntime.Reset();
        }

        private Root CreateRoot(bool withComponentLifecycle)
        {
            _go = new GameObject("Discovery root");
            _go.SetActive(false);
            if (withComponentLifecycle) _go.AddComponent<ComponentStartupLifecycle>();
            _data = ScriptableObject.CreateInstance<ContextData>();
            _data.ScopeTag = ScopeTag;
            _data.EnableAutoDiscovery = true;
            // The default scan skips assemblies named *.tests; scope the scan to this one so the convention type is found.
            _data.AssemblyScopes = new[] { typeof(DiscoveredStartupLifecycle).Assembly.GetName().Name };
            var root = _go.AddComponent<Root>();
            root.SetUp(_data);
            return root;
        }

        private static IEnumerator WaitInitialized(Root root)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!root.IsInitialized && Time.realtimeSinceStartup < deadline) yield return null;
        }

        [UnityTest]
        public IEnumerator RootRunsALifecycleFoundByConvention_ThroughInitializeAndStart()
        {
            var root = CreateRoot(withComponentLifecycle: false);
            _go.SetActive(true);
            yield return WaitInitialized(root);

            Assert.IsTrue(root.IsInitialized);
            Assert.AreEqual(1, DiscoveredStartupLifecycle.Configured, "OnConfigure");
            Assert.AreEqual(1, DiscoveredStartupLifecycle.Initialized, "OnInitializeAsync");
            Assert.AreEqual(1, DiscoveredStartupLifecycle.Started, "OnStartAsync");
        }

        [UnityTest]
        public IEnumerator ComponentLifecycle_RunsEachPhaseOnce_AndSuppressesDiscovery()
        {
            var root = CreateRoot(withComponentLifecycle: true);
            var component = _go.GetComponent<ComponentStartupLifecycle>();
            _go.SetActive(true);
            yield return WaitInitialized(root);

            Assert.IsTrue(root.IsInitialized);
            Assert.AreEqual((1, 1, 1), (component.Configured, component.Initialized, component.Started));
            Assert.AreEqual(0, DiscoveredStartupLifecycle.Configured, "a component lifecycle must keep discovery out");
        }

        [UnityTest]
        public IEnumerator ALifecycleComponentAddedAfterAwake_IsReportedAsAnError()
        {
            var root = CreateRoot(withComponentLifecycle: false);
            _go.SetActive(true); // Awake reads the lifecycle components here
            var late = _go.AddComponent<ComponentStartupLifecycle>();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"^\[Nexus\] Root 'Discovery root': the lifecycle component ComponentStartupLifecycle was added after Awake and never runs"));
            yield return WaitInitialized(root);

            Assert.IsTrue(root.IsInitialized);
            Assert.AreEqual((0, 0, 0), (late.Configured, late.Initialized, late.Started), "the error reports the component; it does not run it");
        }
    }
}
