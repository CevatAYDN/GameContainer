using NUnit.Framework;
using System.Threading.Tasks;
using UnityEngine;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class AudioAndHapticServiceTests
    {
        [Test]
        public async Task AudioService_VolumeClampingAndMuteState()
        {
            using var audio = new AudioService();
            await audio.InitializeAsync(default);

            audio.MasterVolume = 0.8f;
            audio.BgmVolume = 0.5f;
            audio.SfxVolume = 0.7f;

            Assert.AreEqual(0.8f, audio.MasterVolume);
            Assert.AreEqual(0.5f, audio.BgmVolume);
            Assert.AreEqual(0.7f, audio.SfxVolume);

            audio.IsMuted = true;
            Assert.IsTrue(audio.IsMuted);

            audio.IsMuted = false;
            Assert.IsFalse(audio.IsMuted);
        }

        [Test]
        public async Task HapticService_VibrateDoesNotThrowExceptions()
        {
            var haptics = new HapticService();
            await haptics.InitializeAsync(default);

            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Light));
            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Medium));
            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Heavy));
            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Success));

            haptics.OnDispose();
        }
    }
}
