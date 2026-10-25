using System.Collections;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    /// <summary>Issue #71: the audio root the service creates itself goes with the service.</summary>
    public class AudioServiceRootTests
    {
        private sealed class NullRootProvider : IAudioRootProvider
        {
            public GameObject GetOrCreateRoot() => null;
        }

        [UnityTest]
        public IEnumerator Dispose_DestroysTheRootTheServiceCreated()
        {
            var audio = new AudioService { AudioRootProvider = new NullRootProvider() };
            audio.InitializeAsync(default).GetAwaiter().GetResult();
            var own = (GameObject)typeof(AudioService).GetField("_audioRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(audio);
            Assert.IsNotNull(own, "With no provider root the service creates its own.");

            audio.Dispose();
            yield return null; // Destroy completes at the end of the frame in play mode.
            Assert.IsTrue(own == null, "The root the service created itself leaked after Dispose.");
        }
    }
}
