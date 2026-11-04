using System.Threading;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Nexus.Tests
{
    /// <summary>
    /// Issue #92: on Android the keyboard appears with the first key event, and the back key's press and release can land in one
    /// Input System update. InputService read the key's wasPressedThisFrame, whose first call only compares this frame with the last,
    /// so it saw the key already released and the first back press after a cold start was lost.
    /// </summary>
    public sealed class InputServiceButtonTests : InputTestFixture
    {
        [Test]
        public void A_press_and_release_in_one_update_on_a_keyboard_added_after_the_service_started_is_reported()
        {
            var input = new InputService();
            input.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            try
            {
                var keyboard = InputSystem.AddDevice<Keyboard>();
                Press(keyboard.escapeKey, queueEventOnly: true);
                Release(keyboard.escapeKey, queueEventOnly: true);
                InputSystem.Update();

                Assert.That((input.GetButtonDown("Cancel"), input.GetButtonUp("Cancel")), Is.EqualTo((true, true)));
            }
            finally
            {
                input.OnDispose();
            }
        }

        // Issue #156: a provider answered every button read, so a joystick that has no buttons took Android's back key from the service
        [Test]
        public void A_partial_provider_leaves_the_back_key_to_the_service_and_a_plain_provider_still_answers_it()
        {
            var input = new InputService();
            input.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            try
            {
                var keyboard = InputSystem.AddDevice<Keyboard>();
                Press(keyboard.escapeKey, queueEventOnly: true);
                Release(keyboard.escapeKey, queueEventOnly: true);
                InputSystem.Update();

                input.SetInputProvider(new StickWithoutButtons());
                bool partialProvider = input.GetButtonDown("Cancel");
                input.SetInputProvider(new Stick());
                bool plainProvider = input.GetButtonDown("Cancel");
                Assert.That((partialProvider, plainProvider), Is.EqualTo((true, false)));
            }
            finally
            {
                input.OnDispose();
            }
        }

        private class Stick : IInputProvider
        {
            public Vector2 GetMoveInput() => Vector2.right;
            public bool GetButton(string actionName) => false;
            public bool GetButtonDown(string actionName) => false;
            public bool GetButtonUp(string actionName) => false;
        }

        private sealed class StickWithoutButtons : Stick, IPartialInputProvider
        {
            public bool ProvidesButton(string actionName) => false;
        }
    }
}
