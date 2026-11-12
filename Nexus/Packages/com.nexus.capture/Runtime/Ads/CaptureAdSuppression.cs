#if NEXUS_ADS
using Nexus.Ads;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Capture
{
    /// <summary>
    /// Tracks and suppresses ad requests via com.nexus.ads while Capture Mode is open.
    /// </summary>
    internal static class CaptureAdSuppression
    {
        private static AdController s_suppressedController;
        private static bool s_weSuppressed;

        static CaptureAdSuppression()
        {
            CaptureModeService.OpenChanged += OnOpenChanged;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            CaptureModeService.OpenChanged -= OnOpenChanged;
            CaptureModeService.OpenChanged += OnOpenChanged;
        }

        internal static void Tick()
        {
            if (!CaptureModeService.IsOpen)
            {
                if (s_suppressedController != null)
                {
                    LiftSuppression();
                }
                return;
            }

            var currentController = AdsBootstrap.Instance?.Controller;
            if (currentController == s_suppressedController)
            {
                return;
            }

            // The bootstrap that owned the old controller is gone (Instance changes only when it is destroyed), and a disposed
            // controller must not be lifted: lifting reloads every format through a dead provider. Forget it.
            s_suppressedController = null;
            s_weSuppressed = false;

            if (currentController != null)
            {
                s_suppressedController = currentController;
                // Preserve external suppression so Capture never lifts what it did not set.
                if (!currentController.Suppressed)
                {
                    s_weSuppressed = true;
                    currentController.SetSuppressed(true);
                }
                else
                {
                    s_weSuppressed = false;
                }
            }
        }

        private static void OnOpenChanged(bool isOpen)
        {
            if (isOpen)
            {
                Tick();
            }
            else
            {
                LiftSuppression();
            }
        }

        private static void LiftSuppression()
        {
            // Lift only a controller that is still the live one; a replaced one was disposed with its bootstrap.
            if (s_weSuppressed && s_suppressedController != null && AdsBootstrap.Instance?.Controller == s_suppressedController)
            {
                s_suppressedController.SetSuppressed(false);
            }
            s_suppressedController = null;
            s_weSuppressed = false;
        }
    }
}
#endif
