using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    [TestFixture]
    public class SaveThrottlerTests
    {
        private class TestPlayerPrefsService : IPlayerPrefsService
        {
            public int SaveCount;
            public void SetString(string key, string value) { }
            public string GetString(string key, string defaultValue = "") => defaultValue;
            public void SetInt(string key, int value) { }
            public int GetInt(string key, int defaultValue = 0) => defaultValue;
            public void SetFloat(string key, float value) { }
            public float GetFloat(string key, float defaultValue = 0f) => defaultValue;
            public void SetLong(string key, long value) { }
            public long GetLong(string key, long defaultValue = 0L) => defaultValue;
            public bool GetBool(string key, bool defaultValue = false) => defaultValue;
            public void SetBool(string key, bool value) { }
            public bool HasKey(string key) => false;
            public void DeleteKey(string key) { }
            public void DeleteAll() { }
            public void Save() => SaveCount++;
        }

        private class TestTickService : ITickService
        {
            public float TimeScale { get; set; } = 1f;
            public bool IsPaused { get; set; }
            public System.IDisposable HoldPause() => throw new System.NotSupportedException();
            public void RegisterTickable(ITickable tickable) { }
            public void UnregisterTickable(ITickable tickable) { }
            public void RegisterFixedTickable(IFixedTickable tickable) { }
            public void UnregisterFixedTickable(IFixedTickable tickable) { }
            public void RegisterLateTickable(ILateTickable tickable) { }
            public void UnregisterLateTickable(ILateTickable tickable) { }
        }

        private class TestTimeProvider : ITimeProvider
        {
            public float Now { get; set; } = 0f;
        }

        [SetUp]
        public void Setup()
        {
            UnityEngine.Debug.Log($"[DIAG] START {NUnit.Framework.TestContext.CurrentContext.Test.FullName}");
        }

        [Test]
        public void SaveThrottler_FlushesPendingSaveOnTick()
        {
            var prefs = new TestPlayerPrefsService();
            var tickService = new TestTickService();
            var timeProvider = new TestTimeProvider();
            var throttler = new SaveThrottler(tickService, TimeSpan.FromMilliseconds(1))
            {
                TimeProvider = timeProvider
            };

            throttler.TryRequestSave(() => prefs.Save());
            timeProvider.Now = 100f; // Simulate time passing
            throttler.Tick(0.016f);

            Assert.GreaterOrEqual(prefs.SaveCount, 1);
            throttler.OnDispose();
        }

        private sealed class ThrottledSaves : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder)
            {
                builder.BindInstance<ITickService>(new TestTickService { IsPaused = true }); // an ad holds the pause: Tick does not run
                builder.BindInstance<ITimeProvider>(new TestTimeProvider());
                builder.BindService<ISaveThrottler, SaveThrottler>();
            }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        // Issue #167: a save requested inside the throttle window waited for Tick, which a paused TickService skips and a
        // process killed in the background never reaches, so a level-end Earn followed by Home could be lost with no line
        [UnityTest]
        public IEnumerator APendingSave_IsWrittenWhenTheAppPauses_WhateverTheTickState()
        {
            Root.ClearRegistry();
            var go = new GameObject("ThrottleRoot");
            try
            {
                go.SetActive(false);
                var root = go.AddComponent<Root>();
                var data = ScriptableObject.CreateInstance<ContextData>();
                data.ScopeTag = "ThrottleRoot";
                data.AssemblyScopes = Array.Empty<string>();
                root.SetUp(data);
                root.RegisterLifecycle(new ThrottledSaves());
                go.SetActive(true);
                float deadline = Time.realtimeSinceStartup + 5f;
                while (!root.IsInitialized && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(root.IsInitialized, Is.True, "the root did not start in 5 s");

                var throttler = root.Context.Resolve<ISaveThrottler>();
                int economy = 0, progression = 0;
                throttler.TryRequestSave("economy", () => economy++);
                throttler.TryRequestSave("economy", () => economy++); // inside the window: pending
                throttler.TryRequestSave("progression", () => progression++);
                Assert.AreEqual(1, economy);

                go.SendMessage("OnApplicationPause", false);
                Assert.AreEqual(1, economy, "a resume writes nothing");
                go.SendMessage("OnApplicationPause", true);
                Assert.AreEqual(2, economy, "the pending save is written on pause");
                Assert.AreEqual(1, progression, "an owner with nothing pending is not written again");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                Root.ClearRegistry();
            }
        }

        // Issue #167: focus loss comes first on Android and also covers a context without a Root. Unity's focusChanged
        // event cannot be raised from a test, so the handler is called directly.
        [Test]
        public void APendingSave_IsWrittenWhenTheAppLosesFocus()
        {
            var throttler = new SaveThrottler(new TestTickService(), TimeSpan.FromSeconds(2)) { TimeProvider = new TestTimeProvider() };
            throttler.InitializeAsync(CancellationToken.None);
            try
            {
                int saves = 0;
                throttler.TryRequestSave(() => saves++);
                throttler.TryRequestSave(() => saves++); // inside the window: pending
                var onFocus = typeof(SaveThrottler).GetMethod("OnFocusChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.IsNotNull(onFocus, "the throttler has a focus handler");
                onFocus.Invoke(throttler, new object[] { true });
                Assert.AreEqual(1, saves, "gaining focus writes nothing");
                onFocus.Invoke(throttler, new object[] { false });
                Assert.AreEqual(2, saves, "the pending save is written on focus loss");
            }
            finally { throttler.OnDispose(); } // unhooks Application.focusChanged even when an assert fails
        }
    }
}
