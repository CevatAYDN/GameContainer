using NUnit.Framework;
using Nexus.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

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
            public bool HasKey(string key) => false;
            public void DeleteKey(string key) { }
            public void DeleteAll() { }
            public void Save() => SaveCount++;
        }

        private class TestTickService : ITickService
        {
            public event Action<float> OnTick;
            public event Action<float> OnFixedTick;
            public event Action<float> OnLateTick;
            public void RegisterTickable(ITickable tickable) { }
            public void UnregisterTickable(ITickable tickable) { }
            public void RegisterFixedTickable(IFixedTickable tickable) { }
            public void UnregisterFixedTickable(IFixedTickable tickable) { }
            public void RegisterLateTickable(ILateTickable tickable) { }
            public void UnregisterLateTickable(ILateTickable tickable) { }
            public void Tick(float deltaTime) => OnTick?.Invoke(deltaTime);
            public void FixedTick(float deltaTime) => OnFixedTick?.Invoke(deltaTime);
            public void LateTick(float deltaTime) => OnLateTick?.Invoke(deltaTime);
            public bool IsPaused => false;
        }

        [Test]
        public async Task SaveThrottler_FlushesPendingSaveOnTick()
        {
            var prefs = new TestPlayerPrefsService();
            var tickService = new TestTickService();
            var throttler = new SaveThrottler(prefs, tickService, TimeSpan.FromMilliseconds(1));

            throttler.RequestSave();
            await Task.Delay(10);
            tickService.Tick(0.016f);

            Assert.GreaterOrEqual(prefs.SaveCount, 1);
            throttler.Dispose();
        }
    }
}
