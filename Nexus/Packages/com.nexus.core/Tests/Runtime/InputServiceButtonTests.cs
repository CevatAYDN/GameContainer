using System.Threading;
using NUnit.Framework;
using Nexus.Core.Services;
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
    }
}
