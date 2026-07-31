using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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
            var throttler = new SaveThrottler(prefs, tickService, TimeSpan.FromMilliseconds(1))
            {
                TimeProvider = timeProvider
            };

            throttler.TryRequestSave(() => prefs.Save());
            timeProvider.Now = 100f; // Simulate time passing
            throttler.Tick(0.016f);

            Assert.GreaterOrEqual(prefs.SaveCount, 1);
            throttler.OnDispose();
        }
    }
}
