#if UNITY_ANDROID && NEXUS_DEVICE_PROBE && !UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Notifications.Unity;
using UnityEngine;
using UnityEngine.UIElements;

namespace NexusVerify.Device
{
    /// <summary>
    /// Phone checks for issues #134, #160 and #270, in the development Android players that hold DeviceProbe, run only when
    /// adb starts the player with <c>--es nexusProbe services</c>, so a plain launch (gate 9's probe) is unchanged. Through a
    /// context bound as a game binds it, it logs "[Probe]" lines: the share sheet for a text, then for a file (the activity
    /// pauses while the sheet is up; adb's Back key returns), the notification permission, a notification scheduled 20 s
    /// ahead, what TakeOpenedWith returns after each resume, and each time the debug menu opens or closes.
    /// </summary>
    internal sealed class ServicesProbe : MonoBehaviour
    {
        private Context _context;
        private INotificationService _notifications;
        private UIDocument _menuDocument;
        private bool _menuOpen;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            if (LaunchExtra() != "services") return;
            var go = new GameObject("[NexusVerify.ServicesProbe]");
            DontDestroyOnLoad(go);
            go.AddComponent<ServicesProbe>();
        }

        private static string LaunchExtra()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            return intent.Call<string>("getStringExtra", "nexusProbe");
        }

        private IEnumerator Start()
        {
            Task<Context> start = ContextFactory.StartAsync("ServicesProbe", builder =>
            {
                builder.BindService<IShareService, ShareService>();
                builder.BindService<IDebugMenu, DebugMenu>();
                UnityNotificationBindings.Bind(builder);
            });
            yield return new WaitUntil(() => start.IsCompleted);
            if (start.IsFaulted)
            {
                Debug.Log($"[Probe] services context FAILED: {start.Exception?.GetBaseException()}");
                yield break;
            }
            _context = start.Result;
            _notifications = _context.Resolve<INotificationService>();
            var menu = _context.Resolve<IDebugMenu>();
            menu.AddButton("Probe", "Probe button", () => Debug.Log("[Probe] menu button ran"));
            Debug.Log($"[Probe] services bound notifications={_notifications.GetType().Name} openedWith={_notifications.TakeOpenedWith() ?? "none"}");

            var share = _context.Resolve<IShareService>();
            Debug.Log($"[Probe] share text opened={share.ShareText("Nexus probe text", "Nexus probe text")}");
            yield return new WaitForSeconds(2f); // runs on once the sheet is gone

            string path = Path.Combine(Application.temporaryCachePath, "nexus-probe.txt");
            File.WriteAllText(path, "Nexus probe file");
            Debug.Log($"[Probe] share file opened={share.ShareFile(path, "text/plain", "Nexus probe file")}");
            yield return new WaitForSeconds(2f);

            Debug.Log($"[Probe] notification permission={_notifications.Permission}");
            _notifications.SetChannel("nexus_probe", "Nexus probe", "Notifications of the Nexus device probe");
            DateTime fireTime = DateTime.Now.AddSeconds(20);
            int id = _notifications.Schedule("Nexus probe", "Scheduled 20 s ahead", fireTime, "probe-data");
            Debug.Log($"[Probe] notification scheduled id={id} fireTime={fireTime:HH:mm:ss}");
        }

        private void Update()
        {
            if (_menuDocument == null)
            {
                var host = GameObject.Find("NexusDebugMenu");
                _menuDocument = host != null ? host.GetComponent<UIDocument>() : null;
                if (_menuDocument == null) return;
            }
            if (_menuDocument.enabled == _menuOpen) return;
            _menuOpen = _menuDocument.enabled;
            Debug.Log($"[Probe] menu open={_menuOpen} theme={_menuDocument.panelSettings.themeStyleSheet != null} " +
                $"buttons={(_menuOpen ? _menuDocument.rootVisualElement.Query<Button>().ToList().Count : 0)}");
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused || _notifications == null) return;
            Debug.Log($"[Probe] resumed openedWith={_notifications.TakeOpenedWith() ?? "none"} permission={_notifications.Permission}");
        }

        private void OnDestroy() => _context?.Dispose();
    }
}
#endif
