using System.Collections;
using System.Reflection;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    /// <summary>
    /// Issue #222: NexusDebugHUD read UnityEngine.Input, which throws on every call when the project's input handling is the
    /// Input System package only (this development project's setting), so the HUD wrote an exception every frame and its key
    /// never worked.
    /// </summary>
    public sealed class NexusDebugHUDInputTests : InputTestFixture
    {
        private GameObject _host;

        [TearDown]
        public void DestroyHost()
        {
            if (_host != null) Object.Destroy(_host);
        }

        [UnityTest]
        public IEnumerator TheHud_RunsFramesWithoutAnErrorInAnInputSystemOnlyProject()
        {
            _host = new GameObject("NexusDebugHUD");
            _host.AddComponent<NexusDebugHUD>();

            for (int i = 0; i < 3; i++) yield return null;

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void F12OnAnInputSystemKeyboard_TogglesTheHud()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            _host = new GameObject("NexusDebugHUD");
            var hud = _host.AddComponent<NexusDebugHUD>();
            var update = typeof(NexusDebugHUD).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
            var visible = typeof(NexusDebugHUD).GetField("_isVisible", BindingFlags.NonPublic | BindingFlags.Instance);

            Press(keyboard.f12Key);
            update.Invoke(hud, null);

            Assert.IsTrue((bool)visible.GetValue(hud));
        }
    }
}
