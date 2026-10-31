#if NEXUS_STACK_PROBE
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace NexusVerify.Device
{
    /// <summary>
    /// Phone check of the UI Toolkit screen stack (issue #76, ADR-0003 verification plan item 4), compiled only into
    /// the player NexusVerifyBuild.BuildAndroidStackProbe builds with NEXUS_STACK_PROBE; tools/unity-verify/stack-probe.ps1
    /// drives it over adb, once for each UI input setup of ADR-0003 decision 7, named by the launch intent's string extra
    /// "nexusInputMode": "eventsystem" (the starter scene's EventSystem with InputSystemUIInputModule, as it is),
    /// "eventsystem-once" (the same EventSystem with the core's ToolkitPanelInput, which turns uGUI's per-frame panel
    /// objects off and makes the panel's event objects once) and "inputforui" (no EventSystem).
    /// A context of its own binds InputService and ScreenStack beside the starter scene's Root, and three screens: Home
    /// (root, with a full-screen element that counts pointer events), Modal (UILayer.Modal) and Sheet (UILayer.Popup,
    /// opened last, so on top). Every back press logs the screen that heard it; each round is sheet, modal, root. After
    /// the rounds it measures the managed allocation of each frame for 60 s while the script drags over the screen, in
    /// three groups: the frames before the first touch, the touched frames and the rest. Then it waits for the double
    /// press that quits.
    /// </summary>
    internal sealed class StackProbe : MonoBehaviour
    {
        private const int Rounds = 10;
        private const float CaptureSeconds = 60f;

        internal abstract class ProbeScreen : ToolkitScreen
        {
            protected override bool OnBack()
            {
                Debug.Log($"[StackProbe] back on {GetType().Name}");
                return false;
            }
        }

        internal sealed class Home : ProbeScreen { }

        internal sealed class Modal : ProbeScreen
        {
            public override UILayer Layer => UILayer.Modal;
        }

        internal sealed class Sheet : ProbeScreen
        {
            public override UILayer Layer => UILayer.Popup;
        }

        private sealed class Lifecycle : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder)
            {
                builder.BindService<ITickService, TickService>();
                builder.BindService<IInputService, InputService>();
                builder.BindServiceInterfacesAndSelfTo<ScreenStack>();
            }

            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        /// <summary>Frames, frames that allocated, bytes, the largest frame.</summary>
        private sealed class Group
        {
            private long _frames, _allocating, _bytes, _largest;

            public void Add(long bytes)
            {
                _frames++;
                _bytes += bytes;
                if (bytes > 0) _allocating++;
                if (bytes > _largest) _largest = bytes;
            }

            public override string ToString() => $"{_frames}/{_allocating}/{_bytes}/{_largest}";
        }

        private enum Phase { Rounds, Capturing, Done }

        private Root _root;
        private ScreenStack _stack;
        private string _mode;
        private int _round;
        private Phase _phase;
        private ProfilerRecorder _allocated;
        private int _countFrom;
        private float _captureUntil;
        private bool _touchedOnce;
        private readonly Group _idle = new Group(), _touched = new Group(), _rest = new Group();
        private int _pointerDowns, _pointerMoves;
        private VisualElement _surface;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject("[NexusVerify.StackProbe]");
            DontDestroyOnLoad(go);
            go.AddComponent<StackProbe>();
        }

        private IEnumerator Start()
        {
            _mode = InputMode();
            if (!ApplyInputMode()) yield break;
            yield return null; // a destroyed EventSystem is gone at the end of the frame
            Debug.Log($"[StackProbe] mode={_mode} eventSystem={(EventSystem.current != null ? "yes" : "no")}");

            var go = new GameObject("StackProbeRoot");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            _root = go.AddComponent<Root>();
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = "StackProbe";
            data.AssemblyScopes = Array.Empty<string>();
            _root.SetUp(data);
            _root.RegisterLifecycle(new Lifecycle());
            go.SetActive(true);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!_root.IsInitialized && Time.realtimeSinceStartup < deadline) yield return null;
            if (!_root.IsInitialized) { Debug.Log("[StackProbe] FAILED: the context did not start in 10 s"); yield break; }

            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = ScriptableObject.CreateInstance<ThemeStyleSheet>(); // empty: the screens draw nothing
            var home = AddScreen<Home>(panel);
            AddScreen<Modal>(panel);
            AddScreen<Sheet>(panel);
            yield return null;
            _surface = new VisualElement { pickingMode = PickingMode.Position };
            _surface.style.position = Position.Absolute;
            _surface.style.left = 0;
            _surface.style.top = 0;
            _surface.style.right = 0;
            _surface.style.bottom = 0;
            _surface.RegisterCallback<PointerDownEvent>(_ => _pointerDowns++);
            _surface.RegisterCallback<PointerMoveEvent>(_ => _pointerMoves++);
            home.rootVisualElement.style.flexGrow = 1; // a document's root has no height without content
            home.rootVisualElement.Add(_surface);

            _stack = (ScreenStack)_root.Context.Container.Resolve<IScreenStack>();
            _stack.AtRoot = RootBack.PressAgainToQuit;
            _stack.PressAgainSeconds = 2f;
            _stack.PressAgainHintRequested += OnHint;
            _stack.ReplaceRoot<Home>();
            yield return OpenRound();
        }

        private static string InputMode()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            return intent.Call<string>("getStringExtra", "nexusInputMode") ?? "eventsystem";
        }

        private bool ApplyInputMode()
        {
            var eventSystem = FindAnyObjectByType<EventSystem>();
            if (eventSystem == null) { Debug.Log("[StackProbe] FAILED: the starter scene has no EventSystem"); return false; }
            switch (_mode)
            {
                case "eventsystem":
                    return true;
                case "inputforui":
                    eventSystem.gameObject.SetActive(false);
                    Destroy(eventSystem.gameObject);
                    return true;
                case "eventsystem-once":
                    eventSystem.gameObject.AddComponent<ToolkitPanelInput>(); // the screens' panels reach it as they bind
                    return true;
                default:
                    Debug.Log($"[StackProbe] FAILED: unknown nexusInputMode {_mode}");
                    return false;
            }
        }

        private UIDocument AddScreen<T>(PanelSettings panel) where T : ToolkitScreen
        {
            var go = new GameObject(typeof(T).Name);
            go.SetActive(false);
            go.transform.SetParent(_root.transform);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = panel;
            go.AddComponent<T>();
            go.SetActive(true);
            return document;
        }

        private IEnumerator OpenRound()
        {
            _round++;
            yield return new WaitForSecondsRealtime(0.5f);
            _stack.Push<Modal>();
            _stack.Push<Sheet>();
            Debug.Log($"[StackProbe] ready round={_round} top=Sheet depth={_stack.Depth}");
        }

        private void OnHint()
        {
            Debug.Log($"[StackProbe] at root: press-again hint (round {_round})");
            if (_round < Rounds) StartCoroutine(OpenRound());
            else if (_phase == Phase.Rounds) StartCapture();
        }

        private void StartCapture()
        {
            _allocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            if (!_allocated.Valid) { Debug.Log("[StackProbe] FAILED: the GC Allocated In Frame recorder is not available"); return; }
            // LastValue is the frame before; this frame logs, so the first frame counted is the next one.
            _countFrom = Time.frameCount + 2;
            _captureUntil = Time.realtimeSinceStartup + CaptureSeconds;
            _phase = Phase.Capturing;
            Debug.Log($"[StackProbe] rounds done; capture start {CaptureSeconds:F0} s");
        }

        private void Update()
        {
            if (_phase != Phase.Capturing || Time.frameCount < _countFrom) return;
            if (Time.realtimeSinceStartup >= _captureUntil) { FinishCapture(); return; }
            // The touch read now belongs to this frame and LastValue to the one before, so the frame where a touch
            // starts or ends can land in the neighbouring group.
            long bytes = _allocated.LastValue;
            var touch = Touchscreen.current;
            bool touched = touch != null && touch.primaryTouch.press.isPressed;
            _touchedOnce |= touched;
            (touched ? _touched : _touchedOnce ? _rest : _idle).Add(bytes);
        }

        private void FinishCapture()
        {
            _phase = Phase.Done;
            _allocated.Dispose();
            Debug.Log($"[StackProbe] capture mode={_mode} idle={_idle} touched={_touched} rest={_rest} " +
                $"(frames/allocating/bytes/largest) pointerDowns={_pointerDowns} pointerMoves={_pointerMoves} " +
                $"surface={_surface.worldBound} panel={_surface.panel.visualTree.worldBound}");
            Debug.Log("[StackProbe] capture done; a double back press quits");
        }

        private void OnApplicationQuit() => Debug.Log("[StackProbe] quit");
    }
}
#endif
