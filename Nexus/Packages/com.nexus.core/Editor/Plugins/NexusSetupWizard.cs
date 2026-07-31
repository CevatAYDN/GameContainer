using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using Nexus.Core;
using Object = UnityEngine.Object;

namespace Nexus.Editor
{
    /// <summary>
    /// Nexus Setup Wizard — one-click project scaffolding.
    /// Creates folder structure, ContextData, scene, Root, Canvas/UI, and starter code.
    /// </summary>
    public class NexusSetupWizard : NexusEditorPlugin
    {
        public override string Id => "SetupWizard";
        public override string DisplayName => "Setup Wizard";
        public override int Order => -1;

        private VisualElement _root;
        private VisualElement _progressFill;
        private readonly List<SetupStep> _steps = new();
        private const string GameRoot = "Assets/Scripts/Game/Samples";

        private static readonly string[] SubFolders = {
            "Models", "Commands", "Signals", "Services",
            "UI/Views", "UI/Mediators", "Lifecycle"
        };

        private class SetupStep
        {
            public string Title, Description, ButtonText;
            public Action Action;
            public Func<bool> IsComplete;
            public VisualElement StatusElement;
            public VisualElement CardElement;
        }

        public override VisualElement CreateView()
        {
            _root = new VisualElement { style = { paddingLeft = 24, paddingRight = 24, paddingTop = 20, paddingBottom = 20 } };
            NexusEditorStyles.LoadTheme(_root);

            var title = new Label("Nexus Setup Wizard")
            {
                style = { fontSize = 22, unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.AccentBlue), marginBottom = 2 }
            };
            _root.Add(title);

            var subtitle = new Label(
                "This wizard scaffolds a complete Nexus project in one click: folder structure, ContextData asset, " +
                "a starter scene with Root + Canvas/UI, and compilable game code with Lifecycle/Signal/Model/Command.")
            {
                style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.TextSecondary),
                    marginBottom = 16, whiteSpace = WhiteSpace.Normal }
            };
            _root.Add(subtitle);

            // Progress bar
            var progressBar = new VisualElement
            {
                style = { height = 6, backgroundColor = new StyleColor(NexusEditorStyles.DarkPanel),
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                    marginBottom = 20, overflow = Overflow.Hidden }
            };
            _progressFill = new VisualElement
            {
                style = { height = 6, width = Length.Percent(0),
                    backgroundColor = new StyleColor(NexusEditorStyles.AccentGreen),
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3 }
            };
            progressBar.Add(_progressFill);
            _root.Add(progressBar);

            _steps.Clear();

            AddStep("Install Nexus", "Nexus is installed. Verify in Package Manager if needed.",
                "Open Package Manager", () => EditorApplication.ExecuteMenuItem("Window/Package Manager"),
                () => true);

            AddStep("Scaffold Project", "Creates sample folder structure under Assets/Scripts/Game/Samples/, " +
                    "a ContextData asset, a starter scene with GameRoot (Root + ContextData), " +
                    "Canvas with Button/Text, EventSystem, and sample Lifecycle/Signal/Model/Command/Service/View/Mediator code.",
                "Create Project",
                () =>
                {
                    ScaffoldProject();
                },
                () => File.Exists(GameRoot + "/Lifecycle/GameLifecycle.cs") && File.Exists("Assets/Scenes/NexusStarter.unity"));

            AddStep("Open Dashboard", "Launch the Nexus Dashboard to inspect your live architecture.",
                "Open Dashboard", () => EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n"),
                () => true);

            foreach (var step in _steps)
                RenderStep(step);

            var infoBox = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.SurfaceDark),
                    borderLeftWidth = 3, borderLeftColor = new StyleColor(NexusEditorStyles.AccentBlue),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    paddingLeft = 14, paddingRight = 14, paddingTop = 10, paddingBottom = 10,
                    marginTop = 12
                }
            };
            infoBox.Add(new Label("💡 Quick Start Tip:")
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue), marginBottom = 4 }
            });
            infoBox.Add(new Label("After creation, hit Play in Unity to see the sample scene in action. The UI Button increments a counter displayed in the Text via: Signal → Command → Model → Mediator → View flow.")
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), whiteSpace = WhiteSpace.Normal }
            });
            _root.Add(infoBox);

            RefreshStepStatus();
            return _root;
        }

        private void AddStep(string title, string description, string buttonText, Action action, Func<bool> isComplete)
        {
            _steps.Add(new SetupStep
            {
                Title = title, Description = description,
                ButtonText = buttonText, Action = action, IsComplete = isComplete
            });
        }

        private void RenderStep(SetupStep step)
        {
            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    borderTopLeftRadius = 6, borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6, borderBottomRightRadius = 6,
                    paddingLeft = 14, paddingRight = 14, paddingTop = 12, paddingBottom = 12,
                    marginBottom = 8, borderLeftWidth = 3,
                    borderLeftColor = new StyleColor(NexusEditorStyles.DimText)
                }
            };
            step.CardElement = card;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            var dot = NexusEditorStyles.CreateStatusDot(NexusEditorStyles.DimText, 10);
            step.StatusElement = dot;
            header.Add(dot);
            header.Add(new Label(step.Title)
            {
                style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.TextPrimary), marginLeft = 8 }
            });
            card.Add(header);

            if (!string.IsNullOrEmpty(step.Description))
                card.Add(new Label(step.Description)
                {
                    style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary),
                        whiteSpace = WhiteSpace.Normal, marginBottom = 8, marginLeft = 18 }
                });

            var btn = new UnityEngine.UIElements.Button(() => { step.Action?.Invoke(); RefreshStepStatus(); })
            {
                text = step.ButtonText,
                style =
                {
                    marginLeft = 18, paddingLeft = 16, paddingRight = 16, paddingTop = 8, paddingBottom = 8,
                    fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold,
                    backgroundColor = new StyleColor(NexusEditorStyles.BtnPrimary),
                    color = Color.white,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    borderLeftWidth = 0, borderRightWidth = 0, borderTopWidth = 0, borderBottomWidth = 0
                }
            };
            card.Add(btn);
            _root.Add(card);
        }

        private void RefreshStepStatus()
        {
            int completed = 0;
            foreach (var step in _steps)
            {
                bool done = step.IsComplete?.Invoke() ?? false;
                if (done) completed++;
                if (step.StatusElement != null)
                    step.StatusElement.style.backgroundColor = new StyleColor(
                        done ? NexusEditorStyles.AccentGreen : NexusEditorStyles.DimText);
                if (step.CardElement != null)
                    step.CardElement.style.borderLeftColor = new StyleColor(
                        done ? NexusEditorStyles.AccentGreen : NexusEditorStyles.DimText);
            }
            float pct = _steps.Count > 0 ? (float)completed / _steps.Count * 100f : 0f;
            if (_progressFill != null)
                _progressFill.style.width = Length.Percent(pct);
        }

        // ─── Folder Structure ─────────────────────────────────

        private static void CreateFolderStructure()
        {
            foreach (var sub in SubFolders)
                Directory.CreateDirectory(GameRoot + "/" + sub);
            AssetDatabase.Refresh();
        }

        // ─── ContextData Asset ─────────────────────────────────

        private static void CreateContextDataAsset()
        {
            var existing = AssetDatabase.FindAssets("t:ContextData");
            if (existing.Length > 0) return;

            var asset = ScriptableObject.CreateInstance<ContextData>();
            asset.name = "GameContextData";
            asset.EnableAutoDiscovery = true;
            asset.AssemblyScopes = new[] { "Assembly-CSharp" };
            AssetDatabase.CreateAsset(asset, "Assets/GameContextData.asset");
            AssetDatabase.SaveAssets();
        }

        // ─── Scene + Root + Canvas/UI ─────────────────────────

        private static void CreateSceneWithRootAndUI()
        {
            var currentScene = SceneManager.GetActiveScene();
            if (currentScene.isDirty)
                EditorSceneManager.SaveScene(currentScene);

            var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            newScene.name = "NexusStarter";
            EditorSceneManager.SetActiveScene(newScene);

            // --- GameRoot with ContextData ---
            var rootGo = new GameObject("GameRoot");
            var rootComp = rootGo.AddComponent<Root>();

            var contextDataGuids = AssetDatabase.FindAssets("t:ContextData");
            if (contextDataGuids.Length > 0)
            {
                var dataPath = AssetDatabase.GUIDToAssetPath(contextDataGuids[0]);
                var data = AssetDatabase.LoadAssetAtPath<ContextData>(dataPath);
                if (data != null)
                {
                    var so = new SerializedObject(rootComp);
                    var prop = so.FindProperty("contextData");
                    if (prop != null)
                    {
                        prop.objectReferenceValue = data;
                        so.ApplyModifiedProperties();
                    }
                }
            }
            // Add GameLifecycle component (MonoBehaviour) so Root.GetComponents<IContextLifecycle>() finds it
            var lifecycleType = FindType("Game.GameLifecycle");
            if (lifecycleType != null)
            {
                rootGo.AddComponent(lifecycleType);
            }

            Undo.RegisterCreatedObjectUndo(rootGo, "Create GameRoot");

            // --- UI Canvas ---
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(null);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");

            // --- EventSystem (Input System compatible) ---
            var esGo = new GameObject("EventSystem", typeof(EventSystem));
            // Detect correct input module for the active Input System
            var inputModuleType = FindInputModuleType();
            if (inputModuleType != null)
            {
                esGo.AddComponent(inputModuleType);
            }
            Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");

            // --- Panel (View container) ---
            var panelGo = new GameObject("GamePanel", typeof(RectTransform));
            panelGo.transform.SetParent(canvasGo.transform, false);
            var panelRt = panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            // --- GameView component on Panel (resolved by type name) ---
            UnityEngine.MonoBehaviour gameView = null;
            var gameViewType = FindType("Game.GameView");
            if (gameViewType != null && typeof(IView).IsAssignableFrom(gameViewType))
            {
                gameView = (UnityEngine.MonoBehaviour)panelGo.AddComponent(gameViewType);
            }

            // --- Text (counter display) ---
            var textGo = new GameObject("CounterText", typeof(RectTransform));
            textGo.transform.SetParent(panelGo.transform, false);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = new Vector2(0.5f, 0.5f);
            textRt.anchorMax = new Vector2(0.5f, 0.5f);
            textRt.sizeDelta = new Vector2(300, 60);
            textRt.anchoredPosition = new Vector2(0, 30);
            var text = textGo.AddComponent<Text>();
            text.text = "Counter: 0";
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;

            // --- Button ---
            var btnGo = new GameObject("IncrementButton", typeof(RectTransform));
            btnGo.transform.SetParent(panelGo.transform, false);
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(0.5f, 0.5f);
            btnRt.anchorMax = new Vector2(0.5f, 0.5f);
            btnRt.sizeDelta = new Vector2(200, 60);
            btnRt.anchoredPosition = new Vector2(0, -40);
            var button = btnGo.AddComponent<UnityEngine.UI.Button>();
            var btnTextGo = new GameObject("Text", typeof(RectTransform));
            btnTextGo.transform.SetParent(btnGo.transform, false);
            var btnTextRt = btnTextGo.GetComponent<RectTransform>();
            btnTextRt.anchorMin = Vector2.zero;
            btnTextRt.anchorMax = Vector2.one;
            btnTextRt.offsetMin = Vector2.zero;
            btnTextRt.offsetMax = Vector2.zero;
            var btnText = btnTextGo.AddComponent<Text>();
            btnText.text = "Increment";
            btnText.fontSize = 24;
            btnText.alignment = TextAnchor.MiddleCenter;
            btnText.color = Color.white;
            var colors = button.colors;
            colors.normalColor = new Color(0.2f, 0.4f, 0.8f);
            colors.highlightedColor = new Color(0.3f, 0.5f, 0.9f);
            colors.pressedColor = new Color(0.1f, 0.3f, 0.7f);
            button.colors = colors;

            // --- Wire up GameView references via SerializedObject ---
            if (gameView != null)
            {
                var gvSo = new SerializedObject(gameView);
                var buttonProp = gvSo.FindProperty("_button");
                if (buttonProp != null)
                {
                    buttonProp.objectReferenceValue = button;
                }
                var textProp = gvSo.FindProperty("_counterText");
                if (textProp != null)
                {
                    textProp.objectReferenceValue = text;
                }
                gvSo.ApplyModifiedProperties();

                // Register view with Root for automatic binding
                if (gameView is IView viewInterface)
                    rootComp.RegisterPendingView(viewInterface);
            }

            Selection.activeObject = rootGo;

            // Save scene
            var scenePath = "Assets/Scenes/NexusStarter.unity";
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(newScene, scenePath);
        }

        // ─── Main Scaffold ────────────────────────────────────

        private const string PendingSceneKey = "Nexus_SetupWizard_PendingScene";

        private static void ScaffoldProject()
        {
            Debug.Log("[Nexus] Scaffolding project...");

            // Phase 0: Clean old artifacts (stale script references cause "Missing Script" errors)
            CleanOldArtifacts();

            // Phase 1: Create folders + ContextData + generate all code
            CreateFolderStructure();
            CreateContextDataAsset();
            GenerateAllCode();
            AssetDatabase.Refresh();

            // Phase 2: Set flag that survives domain reload + fast-path delayCall.
            //          IMPORTANT: EditorApplication.delayCall does NOT survive domain reload.
            //          When AssetDatabase.Refresh() triggers script compilation,
            //          domain reload resets ALL static state including delayCall delegates.
            //          [DidReloadScripts] below catches it after domain reload.
            SessionState.SetBool(PendingSceneKey, true);
            EditorApplication.delayCall += OnDelayScaffold;
            Debug.Log("[Nexus] Waiting for script compilation before creating scene...");
        }

        [DidReloadScripts]
        private static void OnDidReloadScripts()
        {
            // Check if we were waiting for scene creation
            if (!SessionState.GetBool(PendingSceneKey, false))
                return;

            Debug.Log("[Nexus] Domain reload completed. Checking compiled types...");

            if (!IsGameViewCompiled())
            {
                Debug.LogWarning("[Nexus] Game.GameView type not found yet — retrying on next reload...");
                SessionState.SetBool(PendingSceneKey, true);
                // Can't use delayCall here because domain reload JUST finished and
                // delayCall may not be ready. The flag is still set, so the next
                // [DidReloadScripts] firing will retry.
                return;
            }

            // IMPORTANT: Do NOT call ExecuteDelayedSceneCreation() directly here!
            // [DidReloadScripts] fires BEFORE Editor enters its main update loop.
            // EditorSceneManager.NewScene() requires GetApplication().MayUpdate() == true,
            // which only becomes true AFTER the first Editor frame update.
            // Use delayCall to defer scene creation by 1 frame.
            SessionState.SetBool(PendingSceneKey, false);
            EditorApplication.delayCall += ExecuteDelayedSceneCreation;
        }

        private static void CleanOldArtifacts()
        {
            // Delete old scene to prevent stale script references
            var oldScenePath = "Assets/Scenes/NexusStarter.unity";
            if (File.Exists(oldScenePath))
            {
                AssetDatabase.DeleteAsset(oldScenePath);
                Debug.Log("[Nexus] Deleted old scene: " + oldScenePath);
            }

            // Delete old sample folder (AssetDatabase.DeleteAsset handles .meta too)
            if (AssetDatabase.IsValidFolder(GameRoot))
            {
                AssetDatabase.DeleteAsset(GameRoot);
                Debug.Log("[Nexus] Deleted old sample files under " + GameRoot);
            }
        }

        private static void OnDelayScaffold()
        {
            EditorApplication.delayCall -= OnDelayScaffold;

            // Fast-path: no compilation needed, scene can be created immediately.
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && IsGameViewCompiled())
            {
                SessionState.SetBool(PendingSceneKey, false);
                ExecuteDelayedSceneCreation();
                return;
            }

            // Compilation is still in progress — wait more.
            // [DidReloadScripts] will handle it after domain reload.
            EditorApplication.delayCall += OnDelayScaffold;
        }

        private static void ExecuteDelayedSceneCreation()
        {
            Debug.Log("[Nexus] Creating scene with GameRoot + Canvas/UI...");
            CreateSceneWithRootAndUI();
            Debug.Log("[Nexus] Setup complete! Open Window/Nexus/Dashboard to inspect.");
        }

        private static bool IsGameViewCompiled()
        {
            return FindType("Game.GameView") != null;
        }

        private static Type FindType(string fullTypeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullTypeName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Finds the correct Input Module type considering different Unity versions.
        /// Unity moved InputSystemUIInputModule between namespaces across versions:
        ///   - 2020-2022: UnityEngine.EventSystems.InputSystemUIInputModule
        ///   - 2023+:     UnityEngine.InputSystem.UI.InputSystemUIInputModule
        /// If the Unity.InputSystem assembly is NOT loaded, uses StandaloneInputModule.
        /// If Input System IS loaded but UI module can't be found, returns null
        /// (StandaloneInputModule would crash with InvalidOperationException).
        /// </summary>
        private static Type FindInputModuleType()
        {
            // Check if Input System package is actually installed
            bool hasInputSystem = false;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetName().Name == "Unity.InputSystem")
                    {
                        hasInputSystem = true;
                        break;
                    }
                }
                catch { }
            }

            if (hasInputSystem)
            {
                // Try both known namespaces for InputSystemUIInputModule
                var t = FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
                if (t != null) return t;
                t = FindType("UnityEngine.EventSystems.InputSystemUIInputModule");
                if (t != null) return t;
                // Input System is installed but UI module not found — strange state
                // Don't add any input module to avoid crashes
                Debug.LogWarning("[Nexus] Input System package detected but InputSystemUIInputModule not found. " +
                    "EventSystem created without an Input Module. Add one manually if needed.");
                return null;
            }

            return typeof(StandaloneInputModule);
        }

        // ─── Code Generation ──────────────────────────────────

        private static void GenerateAllCode()
        {
            WriteFile("Lifecycle/GameLifecycle.cs", @"
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Must be a MonoBehaviour so Root.GetComponents&lt;IContextLifecycle&gt;() can discover it.
    /// Attach this component to the GameRoot GameObject.
    /// </summary>
    public class GameLifecycle : MonoBehaviour, IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            builder.BindReactiveModel<GameModel>();
            builder.BindSignal<GameSignal>().To<GameCommand>();
            builder.BindService<IGameService, GameService>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}");

            WriteFile("Signals/GameSignal.cs", @"
namespace Game
{
    public readonly struct GameSignal
    {
        public readonly int Value;
        public GameSignal(int value) => Value = value;
    }
}");

            WriteFile("Models/GameModel.cs", @"
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Game
{
    public class GameModel : IReactiveModel
    {
        public ObservableProperty<int> Counter { get; } = new(0);
        public ValueTask OnBind(CancellationToken ct) => default;
    }
}");

            WriteFile("Commands/GameCommand.cs", @"
using Nexus.Core;

namespace Game
{
    public class GameCommand : ICommand<GameSignal>
    {
        [Inject] private GameModel _model;

        public void Execute(GameSignal signal) => _model.Counter.Value += signal.Value;
    }
}");

            WriteFile("Services/IGameService.cs", @"
using Nexus.Core;

namespace Game
{
    public interface IGameService : INexusService { }
}");

            WriteFile("Services/GameService.cs", @"
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Game
{
    public class GameService : NexusService<IGameService>, IGameService
    {
        public override ValueTask InitializeAsync(CancellationToken ct) => default;
        public override void OnDispose() { }
    }
}");

            WriteFile("UI/Views/GameView.cs", @"
using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game
{
    [Mediator(typeof(GameMediator))]
    public class GameView : View
    {
        public event System.Action OnIncrementClicked;

        [SerializeField] private Button _button;
        [SerializeField] private Text _counterText;

        protected override void OnBind(IContext context)
        {
            if (_button != null)
                _button.onClick.AddListener(() => OnIncrementClicked?.Invoke());
        }

        protected override void OnUnbind()
        {
            if (_button != null)
                _button.onClick.RemoveAllListeners();
        }

        public void UpdateDisplay(int value)
        {
            if (_counterText != null)
                _counterText.text = ""Counter: "" + value.ToString();
        }
    }
}");

            WriteFile("UI/Mediators/GameMediator.cs", @"
using Nexus.Core;

namespace Game
{
    public class GameMediator : Mediator<GameView>
    {
        [Inject] private GameModel _model;

        protected override void OnBind()
        {
            _model.Counter.OnChanged((o, n) => View.UpdateDisplay(n));
            View.UpdateDisplay(_model.Counter.Value);
            View.OnIncrementClicked += () => SignalBus.Fire(new GameSignal(1));
        }

        protected override void OnUnbind()
        {
            _model.Counter.ClearOnChanged();
        }
    }
}");

            AssetDatabase.Refresh();
            Debug.Log("[Nexus] Sample game files generated under " + GameRoot + "/");
        }

        private static void WriteFile(string relativePath, string content)
        {
            var fullPath = GameRoot + "/" + relativePath;
            File.WriteAllText(fullPath, content.TrimStart('\n', '\r'));
        }
    }
}
