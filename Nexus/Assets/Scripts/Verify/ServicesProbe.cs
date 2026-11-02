#if UNITY_ANDROID && NEXUS_DEVICE_PROBE && !UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Notifications.Unity;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace NexusVerify.Device
{
    /// <summary>
    /// Phone checks for issues #134, #160 and #270 in the development Android players that hold DeviceProbe, run by
    /// tools/unity-verify/services-probe.ps1 through the intent extra <c>nexusProbe</c>, so a plain launch (gate 9's
    /// probe) is unchanged. Through a context bound as a game binds it, it logs "[Probe]" lines:
    /// <list type="bullet">
    /// <item><c>services</c>: the share sheet for a text, then for a file (whose URI it also grants to adb's shell, so a
    /// second process can read it), the notification permission, a notification scheduled 20 s ahead, and what
    /// TakeOpenedWith returns after each resume;</item>
    /// <item><c>services-cold</c>: schedules the notification and arms the next launch, which the notification's tap starts
    /// after adb killed the process, to log TakeOpenedWith;</item>
    /// <item><c>services-ask</c>: RequestPermissionAsync's answer;</item>
    /// <item><c>services-menu</c>: only the debug menu.</item>
    /// </list>
    /// Every mode logs each time the debug menu opens or closes, with the touches on the screen.
    /// </summary>
    internal sealed class ServicesProbe : MonoBehaviour
    {
        private const string FileText = "Nexus probe file";
        private static string _mode;
        private Context _context;
        private INotificationService _notifications;
        private UIDocument _menuDocument;
        private bool _menuOpen;

        // The notification's tap starts the activity without the extra, so services-cold leaves this file for that launch.
        private static string ColdMarker => Path.Combine(Application.persistentDataPath, "nexus-services-probe-cold");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            _mode = LaunchExtra();
            if (File.Exists(ColdMarker))
            {
                File.Delete(ColdMarker); // one launch only
                _mode = "cold-start";
            }
            if (_mode == null || !_mode.StartsWith("services") && _mode != "cold-start") return;
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
            Debug.Log($"[Probe] services bound mode={_mode} notifications={_notifications.GetType().Name} openedWith={_notifications.TakeOpenedWith() ?? "none"}");

            switch (_mode)
            {
                case "services":
                    yield return Share(_context.Resolve<IShareService>());
                    Schedule();
                    break;
                case "services-cold":
                    Schedule();
                    File.WriteAllText(ColdMarker, "armed");
                    Debug.Log("[Probe] cold armed");
                    break;
                case "services-ask":
                    Debug.Log($"[Probe] ask before permission={_notifications.Permission}");
                    Task<NotificationPermission> ask = _notifications.RequestPermissionAsync(destroyCancellationToken).AsTask();
                    yield return new WaitUntil(() => ask.IsCompleted);
                    Debug.Log($"[Probe] ask answered={(ask.IsFaulted ? ask.Exception?.GetBaseException().Message : ask.Result.ToString())} permission={_notifications.Permission}");
                    break;
            }
        }

        private static IEnumerator Share(IShareService share)
        {
            Debug.Log($"[Probe] share text opened={share.ShareText("Nexus probe text", "Nexus probe text")}");
            yield return new WaitForSeconds(2f); // runs on once the sheet is gone

            string path = Path.Combine(Application.temporaryCachePath, "nexus-probe.txt");
            File.WriteAllText(path, FileText);
            Debug.Log($"[Probe] share file opened={share.ShareFile(path, "text/plain", "Nexus probe file")}");
            Debug.Log($"[Probe] share file uri={GrantToShell(path)}");
            yield return new WaitForSeconds(2f);
        }

        // The URI ShareService puts in the intent, granted to adb's shell (com.android.shell) the way the chooser grants it
        // to the app the player picks: a second process then reads the file through the provider with `content read`.
        private static string GrantToShell(string path)
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            string authority = activity.Call<string>("getPackageName") + ".nexusfileprovider";
            using var file = new AndroidJavaObject("java.io.File", path);
            using var provider = new AndroidJavaClass("androidx.core.content.FileProvider");
            using var uri = provider.CallStatic<AndroidJavaObject>("getUriForFile", activity, authority, file);
            activity.Call("grantUriPermission", "com.android.shell", uri, 1); // FLAG_GRANT_READ_URI_PERMISSION
            return uri.Call<string>("toString");
        }

        private void Schedule()
        {
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
                $"buttons={(_menuOpen ? _menuDocument.rootVisualElement.Query<Button>().ToList().Count : 0)} touches={Touches()}");
        }

        // The fingers on the screen in the frame the menu changed: three when they opened or closed it.
        private static int Touches()
        {
            var screen = Touchscreen.current;
            if (screen == null) return 0;
            int count = 0;
            foreach (var touch in screen.touches)
                if (touch.isInProgress) count++;
            return count;
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
