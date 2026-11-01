using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using Nexus.Core;
using Nexus.Core.Services;
using Object = UnityEngine.Object;

namespace Nexus.Editor
{
    /// <summary>
    /// The Setup section's start page: generates the starter scene, imports the package samples,
    /// leads an existing project to the Context Wizard, and opens the canonical documents.
    /// </summary>
    public class NexusSetupWizard : NexusEditorPlugin
    {
        public override string Id => "SetupWizard";
        public override string DisplayName => NexusLang.Get("setup_name");
        public override int Order => -1;
        public override string Category => "cat_setup";

        private VisualElement _root;
        internal const string GameRoot = "Assets/Scripts/Game/Samples";
        private const string ContextDataPath = "Assets/GameContextData.asset";
        private const string StarterScenePath = "Assets/Scenes/NexusStarter.unity";

        private static readonly string[] SubFolders = {
            "Models", "Commands", "Signals", "Services",
            "UI/Views", "UI/Mediators", "Lifecycle"
        };

        public override VisualElement CreateView()
        {
            _root = new VisualElement { style = { flexGrow = 1 } };
            NexusEditorStyles.LoadTheme(_root);
            var scroll = new ScrollView { style = { flexGrow = 1, paddingLeft = 24, paddingRight = 24, paddingTop = 20, paddingBottom = 20 } };
            _root.Add(scroll);

            scroll.Add(new Label(NexusLang.Get("setup_title"))
            {
                style = { fontSize = 22, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue), marginBottom = 2 }
            });
            scroll.Add(new Label(NexusLang.Get("setup_description"))
            {
                style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 16, whiteSpace = WhiteSpace.Normal }
            });

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusSetupWizard).Assembly);

            // A new project: the generated starter scene.
            var starter = NexusEditorStyles.CreateInfoCard(scroll, NexusLang.Get("setup_scaffold"), NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBg, NexusLang.Get("setup_scaffold_description"));
            bool generated = HasScaffoldConflict(Directory.GetCurrentDirectory());
            var generate = NexusEditorStyles.CreateButton(NexusLang.Get("setup_create"), () => { ScaffoldProject(); Window?.RefreshActivePlugin(); }, NexusEditorStyles.BtnPrimary);
            generate.name = "nexus-starter-generate";
            generate.SetEnabled(!generated);
            starter.Add(generate);
            if (generated) starter.Add(NexusEditorStyles.CreateHint(NexusLang.Get("setup_scaffold_exists")));
            starter.Add(NexusEditorStyles.CreateHint(NexusLang.Get("setup_tip")));

            // The package's samples, imported in place.
            var samples = NexusEditorStyles.CreateInfoCard(scroll, NexusLang.Get("setup_samples"), NexusEditorStyles.AccentOrange, NexusEditorStyles.CardBg, NexusLang.Get("setup_samples_description"));
            if (package != null)
            {
                foreach (var sample in UnityEditor.PackageManager.UI.Sample.FindByPackage(package.name, package.version))
                {
                    var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 4 } };
                    row.Add(new Label($"<b>{sample.displayName}</b>  {sample.description}")
                    {
                        style = { flexGrow = 1, flexShrink = 1, fontSize = 10, whiteSpace = WhiteSpace.Normal, color = new StyleColor(NexusEditorStyles.TextPrimary) }
                    });
                    var toImport = sample;
                    var import = NexusEditorStyles.CreateButton(NexusLang.Get(sample.isImported ? "setup_sample_imported" : "setup_sample_import"),
                        () => { toImport.Import(); Window?.RefreshActivePlugin(); }, NexusEditorStyles.BtnBlue);
                    import.SetEnabled(!sample.isImported);
                    row.Add(import);
                    samples.Add(row);
                }
            }

            // An existing project: a context in the open scene.
            var existing = NexusEditorStyles.CreateInfoCard(scroll, NexusLang.Get("setup_existing"), NexusEditorStyles.AccentGreen, NexusEditorStyles.CardBg, NexusLang.Get("setup_existing_description"));
            existing.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_open_wizard"), () => Window?.SwitchToPlugin("Wizard"), NexusEditorStyles.BtnBlue));

            // The canonical documents, in the editor's language when a translation exists.
            var docs = NexusEditorStyles.CreateInfoCard(scroll, NexusLang.Get("setup_docs"), NexusEditorStyles.AccentPurple, NexusEditorStyles.CardBg, null);
            var guide = NexusEditorStyles.CreateButton(NexusLang.Get("setup_guide"), () => OpenDocument("docs/INTEGRATION_PATHS.md"), NexusEditorStyles.BtnBlue);
            guide.name = "nexus-integration-guide";
            docs.Add(guide);
            docs.Add(NexusEditorStyles.CreateButton(NexusLang.Get("setup_doc_quickstart"), () => OpenDocument("docs/10_MIN_QUICKSTART.md"), NexusEditorStyles.BtnGray));
            docs.Add(NexusEditorStyles.CreateButton(NexusLang.Get("setup_doc_getting_started"), () => OpenDocument("docs/GETTING_STARTED.md"), NexusEditorStyles.BtnGray));
            docs.Add(NexusEditorStyles.CreateButton(NexusLang.Get("setup_doc_troubleshooting"), () => OpenDocument("TROUBLESHOOTING.md"), NexusEditorStyles.BtnGray));
            docs.Add(NexusEditorStyles.CreateButton(NexusLang.Get("setup_doc_changelog"), () => OpenDocument("CHANGELOG.md"), NexusEditorStyles.BtnGray));

            scroll.Add(new Label(string.Format(NexusLang.Get("setup_version"), package?.version, Application.unityVersion))
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 12 }
            });
            return _root;
        }

        private static void OpenDocument(string relativePath)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusSetupWizard).Assembly);
            if (package == null) throw new InvalidOperationException("Nexus package location could not be resolved.");
            string path = Path.Combine(package.resolvedPath, relativePath);
            string translated = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_TR.md");
            if (NexusLang.CurrentLocale == "tr" && File.Exists(translated)) path = translated;
            EditorUtility.OpenWithDefaultApp(path);
        }

        public override void OnDisable()
        {
            _root = null;
            base.OnDisable();
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
            AssetDatabase.CreateAsset(NewStarterContextData(), ContextDataPath);
            AssetDatabase.SaveAssets();
        }

        internal static ContextData NewStarterContextData()
        {
            var asset = ScriptableObject.CreateInstance<ContextData>();
            asset.name = "GameContextData";
            asset.EnableAutoDiscovery = true;
            // No AssemblyScopes: the runtime then scans every assembly that references Nexus, and Assembly-CSharp. A list is
            // all it scans, and Assembly-CSharp alone left the handlers in an asmdef the game added unregistered (#260).
            asset.AssemblyScopes = System.Array.Empty<string>();
            return asset;
        }

        // ─── Starter scene: Root, camera, EventSystem, UI Toolkit panel ─────

        internal const string StarterPanelSettingsPath = "Assets/Scenes/NexusStarterPanelSettings.asset";
        internal const string StarterThemePath = "Assets/Scenes/NexusStarterTheme.tss";

        private static void CreateSceneWithRootAndUI()
        {
            if (File.Exists(StarterScenePath))
                throw new IOException("Nexus starter scene already exists; existing content is preserved.");
            CreateStarterScene(StarterScenePath);
        }

        /// <summary>
        /// Builds the starter scene additively and saves it at <paramref name="scenePath"/>; over an existing file its
        /// .meta, and so the scene's guid in the build settings, stays. The scene holds the GameRoot with the context data
        /// and the generated lifecycle, a camera, an EventSystem with <see cref="ToolkitPanelInput"/> (ADR-0003, decision
        /// 7), and the GamePanel: a <see cref="UIDocument"/> with the generated GameView, which builds its elements.
        /// </summary>
        internal static void CreateStarterScene(string scenePath)
        {
            // Add the starter scene without saving or closing the user's scenes. Unity adds a scene only beside saved ones:
            // an untitled scene with no change is closed (replaced when it is the only kind open), one with changes stops here.
            var mode = NewSceneMode.Additive;
            bool onlyUntitled = true;
            var untitled = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (!string.IsNullOrEmpty(open.path)) { onlyUntitled = false; continue; }
                if (open.isDirty) throw new InvalidOperationException("Save or close the untitled scene first: Unity adds the starter scene only beside saved scenes.");
                untitled.Add(open);
            }
            if (untitled.Count > 0 && onlyUntitled) mode = NewSceneMode.Single;
            else foreach (var open in untitled) EditorSceneManager.CloseScene(open, true);
            var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            newScene.name = Path.GetFileNameWithoutExtension(scenePath);
            EditorSceneManager.SetActiveScene(newScene);

            // --- GameRoot with ContextData ---
            var rootGo = new GameObject("GameRoot");
            var rootComp = rootGo.AddComponent<Root>();

            var data = AssetDatabase.LoadAssetAtPath<ContextData>(ContextDataPath);
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
            // Add GameLifecycle component (MonoBehaviour) so Root.GetComponents<IContextLifecycle>() finds it
            var lifecycleType = FindType("Game.GameLifecycle");
            if (lifecycleType != null)
            {
                rootGo.AddComponent(lifecycleType);
            }

            Undo.RegisterCreatedObjectUndo(rootGo, "Create GameRoot");

            // --- Camera: clears the screen behind the panel and holds the AudioListener ---
            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            Undo.RegisterCreatedObjectUndo(cameraGo, "Create Camera");

            // --- EventSystem (Input System compatible) ---
            var esGo = new GameObject("EventSystem", typeof(EventSystem));
            // Detect correct input module for the active Input System
            var inputModuleType = FindInputModuleType();
            if (inputModuleType != null)
            {
                esGo.AddComponent(inputModuleType);
            }
            else
            {
                Debug.LogWarning("[Nexus] Could not find an InputModule type to attach to the EventSystem. " +
                    "The UI will not process input events until a module (e.g. StandaloneInputModule or " +
                    "InputSystemUIInputModule) is added manually.");
            }
            // UI Toolkit input through the EventSystem without uGUI's per-frame allocation (ADR-0003, decision 7)
            esGo.AddComponent<ToolkitPanelInput>();
            Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");

            // --- GamePanel: the UI Toolkit document and the GameView on it ---
            var panelGo = new GameObject("GamePanel");
            var document = panelGo.AddComponent<UIDocument>();
            document.panelSettings = LoadOrCreatePanelSettings();
            var gameViewType = FindType("Game.GameView");
            if (gameViewType != null && typeof(IView).IsAssignableFrom(gameViewType) && panelGo.AddComponent(gameViewType) is IView view)
                rootComp.RegisterPendingView(view); // Register view with Root for automatic binding
            Undo.RegisterCreatedObjectUndo(panelGo, "Create GamePanel");

            Selection.activeObject = rootGo;

            Directory.CreateDirectory(Path.GetDirectoryName(scenePath));
            EditorSceneManager.SaveScene(newScene, scenePath);
        }

        /// <summary>The panel settings of the starter's document, with Unity's default runtime theme: created once, then kept.</summary>
        private static PanelSettings LoadOrCreatePanelSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(StarterPanelSettingsPath);
            if (settings != null) return settings;
            Directory.CreateDirectory(Path.GetDirectoryName(StarterPanelSettingsPath));
            if (!File.Exists(StarterThemePath))
            {
                // what Unity's Create > UI Toolkit > Default Runtime Theme File writes; a panel without a theme warns on every Play
                File.WriteAllText(StarterThemePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(StarterThemePath);
            }
            settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(StarterThemePath);
            // the values of the uGUI canvas scaler the starter had
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.match = 0.5f;
            AssetDatabase.CreateAsset(settings, StarterPanelSettingsPath);
            return settings;
        }

        // ─── Main Scaffold ────────────────────────────────────

        private const string PendingSceneKey = "Nexus_SetupWizard_PendingScene";
        private const string PendingSceneRetryKey = "Nexus_SetupWizard_PendingSceneRetries";

        private static void ScaffoldProject()
        {
            if (HasScaffoldConflict(Directory.GetCurrentDirectory()))
            {
                Debug.LogWarning("[Nexus] Starter output already exists. Existing files and scenes were preserved. " +
                    "Use the imported sample or ContextFactory.StartAsync in an existing project.");
                return;
            }
            Debug.Log("[Nexus] Scaffolding project...");

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
            SessionState.SetInt(PendingSceneRetryKey, 0);
            EditorApplication.delayCall += OnDelayScaffold;
            // E-H10 fix: PlayMode geçişinde flag'i sıfırla — play mode domain reload'unda
            // [DidReloadScripts] tetiklenmez, flag sonsuza dek set kalırdı.
            // Subscribe ONCE per domain — re-running the wizard must not stack duplicate
            // handlers on the static event (they would all fire, harmless but wasteful).
            EditorApplication.playModeStateChanged -= OnPlayModeClearPending;
            EditorApplication.playModeStateChanged += OnPlayModeClearPending;
            Debug.Log("[Nexus] Waiting for script compilation before creating scene...");
        }

        // E-H10 fix: play mode'a girerken bekleyen scaffold'u iptal et. Scene creation
        // play mode'da zaten anlamsız (editor scene'leri yaratılmalı) ve flag'in play
        // mode reload'unda hayatta kalması edit mode'a dönüldüğünde kilitli bırakırdı.
        private static void OnPlayModeClearPending(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode)
            {
                if (SessionState.GetBool(PendingSceneKey, false))
                {
                    Debug.LogWarning("[Nexus] Scaffold pending scene creation cancelled: editor entered Play Mode before compilation finished. Re-run the setup wizard after exiting Play Mode.");
                    SessionState.SetBool(PendingSceneKey, false);
                }
            }
        }

        private const int MaxScaffoldRetries = 3;

        [DidReloadScripts]
        private static void OnDidReloadScripts()
        {
            // Check if we were waiting for scene creation
            if (!SessionState.GetBool(PendingSceneKey, false))
                return;

            Debug.Log("[Nexus] Domain reload completed. Checking compiled types...");

            if (!IsGameViewCompiled())
            {
                // E-H10 fix: bounded retry — the flag previously survived FOREVER when the
                // generated Game.GameView never compiled (e.g. compile error in user code),
                // leaving every future wizard run thinking a scaffold was still pending.
                int retries = SessionState.GetInt(PendingSceneRetryKey, 0) + 1;
                if (retries > MaxScaffoldRetries)
                {
                    Debug.LogError($"[Nexus] Game.GameView type still not found after {MaxScaffoldRetries} reloads — scaffold aborted. Fix compile errors and re-run the wizard.");
                    SessionState.SetBool(PendingSceneKey, false);
                    SessionState.SetInt(PendingSceneRetryKey, 0);
                    return;
                }
                SessionState.SetInt(PendingSceneRetryKey, retries);
                Debug.LogWarning($"[Nexus] Game.GameView type not found yet — retrying on next reload ({retries}/{MaxScaffoldRetries})...");
                SessionState.SetBool(PendingSceneKey, true);
                // Can't use delayCall here because domain reload JUST finished and
                // delayCall may not be ready. The flag is still set, so the next
                // [DidReloadScripts] firing will retry.
                return;
            }
            SessionState.SetInt(PendingSceneRetryKey, 0);

            // IMPORTANT: Do NOT call ExecuteDelayedSceneCreation() directly here!
            // [DidReloadScripts] fires BEFORE Editor enters its main update loop.
            // EditorSceneManager.NewScene() requires GetApplication().MayUpdate() == true,
            // which only becomes true AFTER the first Editor frame update.
            // Use delayCall to defer scene creation by 1 frame.
            SessionState.SetBool(PendingSceneKey, false);
            EditorApplication.delayCall += ExecuteDelayedSceneCreation;
        }

        internal static bool HasScaffoldConflict(string projectRoot)
        {
            return Directory.Exists(Path.Combine(projectRoot, GameRoot))
                || File.Exists(Path.Combine(projectRoot, StarterScenePath))
                || File.Exists(Path.Combine(projectRoot, ContextDataPath));
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
            Debug.Log("[Nexus] Creating the starter scene: GameRoot and a UI Toolkit panel...");
            CreateSceneWithRootAndUI();
            Debug.Log("[Nexus] Setup complete! Open Nexus > Status to inspect.");
        }

        private static bool IsGameViewCompiled()
        {
            return FindType("Game.GameView") != null;
        }

        private static Type FindType(string fullTypeName)
        {
            foreach (var asm in AssemblyCatalog.LoadedAssemblies)
            {
                try
                {
                    var t = asm.GetType(fullTypeName);
                    if (t != null) return t;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Nexus] Failed to inspect assembly '{asm.GetName().Name}' for GameView: {ex.Message}");
                }
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
            foreach (var asm in AssemblyCatalog.LoadedAssemblies)
            {
                try
                {
                    if (asm.GetName().Name == "Unity.InputSystem")
                    {
                        hasInputSystem = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Nexus] Failed to inspect assembly '{asm.GetName().Name}' for input system: {ex.Message}");
                }
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

        // Each template below is a separate internal static so the editor test suite
        // (WizardTemplateSyncTests) can lock ALL EIGHT wizard outputs byte-for-byte
        // against the canonical Assets/Scripts/Game/Samples files. A fix applied to
        // only one side breaks the "wizard output mirrors the canonical example"
        // guarantee (NEXUS_READY Kapı 10) and silently ships the stale pattern to
        // every new project. Generation is a straight copy of these strings: no
        // trimming or transformation, so the written bytes are the tested bytes.

        internal static string GameLifecycleTemplate =>
@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Lifecycle configuration for the Game context.
    /// Supports two clean workflows:
    /// 1. Attached as a MonoBehaviour component to the GameRoot GameObject (auto-discovered by Root.GetComponents&lt;IContextLifecycle&gt;()).
    /// 2. Implemented as a plain C# class named '{ScopeTag}Lifecycle' (e.g., GameLifecycle) when ContextData has EnableAutoDiscovery enabled.

    /// </summary>
    public class GameLifecycle : MonoBehaviour, IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            builder.BindReactiveModel<GameModel>();
            builder.BindCommand<GameSignal, GameCommand>();
            builder.BindService<IGameService, GameService>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}";

        internal static string GameSignalTemplate => @"namespace Game
{
    public readonly struct GameSignal
    {
        public readonly int Value;
        public GameSignal(int value) => Value = value;
    }
}";

        internal static string GameModelTemplate => @"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Game
{
    public class GameModel : IReactiveModel
    {
        public ObservableProperty<int> Counter { get; } = new(0);
        public ValueTask OnBind(CancellationToken ct) => default;
    }
}";

        internal static string GameCommandTemplate => @"using Nexus.Core;

namespace Game
{
    public class GameCommand : ICommand<GameSignal>
    {
        [Inject] private GameModel _model;

        public void Execute(GameSignal signal) => _model.Counter.Value += signal.Value;
    }
}";

        internal static string IGameServiceTemplate => @"using Nexus.Core;

namespace Game
{
    public interface IGameService : INexusService { }
}";

        internal static string GameServiceTemplate => @"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Game
{
    public class GameService : NexusService<IGameService>, IGameService
    {
        public override ValueTask InitializeAsync(CancellationToken ct) => default;
        public override void OnDispose() { }
    }
}";

        internal static string GameViewTemplate => @"using Nexus.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    [Mediator(typeof(GameMediator))]
    [RequireComponent(typeof(UIDocument))]
    public class GameView : View
    {
        public event System.Action OnIncrementClicked;

        private VisualElement _panel;
        private Label _counterText;

        protected override void OnBind(IContext context)
        {
            // UIDocument builds a new root each time it is enabled, before this view binds (its execution order is -100),
            // so the elements are made at each bind and taken out at each unbind.
            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0;
            _panel.style.top = 0;
            _panel.style.right = 0;
            _panel.style.bottom = 0;
            _panel.style.alignItems = Align.Center;
            _panel.style.justifyContent = Justify.Center;

            _counterText = new Label(""Counter: 0"");
            _counterText.style.fontSize = 32;
            _counterText.style.color = Color.white;
            _counterText.style.marginBottom = 20;
            _panel.Add(_counterText);

            var button = new Button(OnClicked) { text = ""Increment"" };
            button.style.width = 200;
            button.style.height = 60;
            button.style.fontSize = 24;
            _panel.Add(button);

            GetComponent<UIDocument>().rootVisualElement.Add(_panel);
        }

        protected override void OnUnbind()
        {
            _panel?.RemoveFromHierarchy();
            _panel = null;
            _counterText = null;
        }

        private void OnClicked() => OnIncrementClicked?.Invoke();

        public void UpdateDisplay(int value)
        {
            if (_counterText != null)
                _counterText.text = ""Counter: "" + value.ToString();
        }
    }
}";

        /// <summary>
        /// Verbatim template for the generated <c>GameMediator</c>. Kept on the wizard so
        /// the generated sample and the canonical <c>Assets/Scripts/Game/Samples</c> file
        /// share ONE source of truth; <c>WizardTemplateSyncTests</c> asserts the two stay
        /// byte-identical (normalized) so a fix in one place can never orphan the other.
        /// </summary>
        internal static string GameMediatorTemplate => @"using Nexus.Core;

namespace Game
{
    public class GameMediator : Mediator<GameView>
    {
        [Inject] private GameModel _model;

        protected override void OnBind()
        {
            // Named handlers, not inline lambdas: OnUnbind must be able to remove the
            // EXACT delegate that was added. Inline lambdas cannot be unsubscribed, so
            // pooled mediator reuse would stack duplicate handlers.
            _model.Counter.OnChanged(OnCounterChanged);
            View.UpdateDisplay(_model.Counter.Value);
            View.OnIncrementClicked += OnIncrementClicked;
        }

        protected override void OnUnbind()
        {
            // Remove only THIS mediator's handler — ClearOnChanged() would wipe every
            // other subscriber of the shared model property.
            _model.Counter.RemoveOnChanged(OnCounterChanged);
            View.OnIncrementClicked -= OnIncrementClicked;
        }

        private void OnCounterChanged(int oldValue, int newValue)
        {
            View.UpdateDisplay(newValue);
        }

        private void OnIncrementClicked()
        {
            SignalBus.Fire(new GameSignal(1));
        }
    }
}";

        /// <summary>
        /// The complete set of files the wizard generates, as (relative path, content)
        /// pairs. This mapping is the single source of truth for WHAT gets written WHERE:
        /// <c>GenerateAllCode</c> writes exactly these files, and the editor test suite
        /// asserts each entry is byte-identical to its canonical
        /// <c>Assets/Scripts/Game/Samples</c> counterpart. A template can never be
        /// renamed, re-pathed, or orphaned without a test failing.
        /// </summary>
        internal static IReadOnlyList<(string RelativePath, string Content)> GeneratedFiles()
        {
            return new (string, string)[]
            {
                ("Lifecycle/GameLifecycle.cs", GameLifecycleTemplate),
                ("Signals/GameSignal.cs", GameSignalTemplate),
                ("Models/GameModel.cs", GameModelTemplate),
                ("Commands/GameCommand.cs", GameCommandTemplate),
                ("Services/IGameService.cs", IGameServiceTemplate),
                ("Services/GameService.cs", GameServiceTemplate),
                ("UI/Views/GameView.cs", GameViewTemplate),
                ("UI/Mediators/GameMediator.cs", GameMediatorTemplate)
            };
        }

        /// <summary>
        /// SHA-256 (lowercase hex) of each generated file's content, normalized to LF and
        /// trimmed, so the value is stable across line-ending modes (autocrlf checkouts).
        /// These hashes are documented in CHANGELOG.md and pinned by
        /// <c>WizardTemplateSyncTests</c>: a template that changes without its documented
        /// hash being updated fails the test the moment the change happens, so unintended
        /// template edits cannot cross a release silently.
        /// </summary>
        internal static IReadOnlyList<(string RelativePath, string Sha256)> GeneratedFileHashes()
        {
            var hashes = new List<(string, string)>(GeneratedFiles().Count);
            foreach (var (relativePath, content) in GeneratedFiles())
            {
                var normalized = content.Replace("\r\n", "\n").Trim();
                var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var digest = sha.ComputeHash(bytes);
                var builder = new System.Text.StringBuilder(digest.Length * 2);
                foreach (var b in digest)
                    builder.Append(b.ToString("x2"));
                hashes.Add((relativePath, builder.ToString()));
            }
            return hashes;
        }

        /// <summary>
        /// Writes every generated file under <paramref name="root"/> — an absolute path
        /// outside the AssetDatabase in tests, <c>GameRoot</c> in real generation.
        /// Separated from <c>GenerateAllCode</c> so the test suite can exercise the real
        /// write path (directory creation + atomic file writes, exact bytes) without
        /// touching the AssetDatabase or the project folder.
        /// </summary>
        internal static void WriteGeneratedFiles(string root)
        {
            foreach (var (relativePath, content) in GeneratedFiles())
            {
                var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                WriteFileAtomically(fullPath, content);
            }
        }

        /// <summary>
        /// Stages <paramref name="content"/> to a unique temp file in the target's own
        /// directory, then atomically moves it over <paramref name="fullPath"/>. An
        /// interrupted generation (editor crash, compile error mid-write) can never leave
        /// a truncated or corrupt file at the destination: the target either keeps its
        /// previous bytes or receives the complete new content. The temp file shares the
        /// target's directory so the move is a same-volume rename, never a cross-volume
        /// copy — the same atomic-write idiom as
        /// <c>EncryptedStorageService.WriteRawDataAtomically</c>.
        /// </summary>
        internal static void WriteFileAtomically(string fullPath, string content)
        {
            string tempPath = fullPath + ".tmp" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, content);

                if (File.Exists(fullPath))
                {
                    try
                    {
                        // File.Replace is atomic (MoveFileEx REPLACE_EXISTING on Windows,
                        // rename(2) on Unix) and preserves the destination metadata.
                        File.Replace(tempPath, fullPath, null);
                    }
                    catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException)
                    {
                        // Degraded fallback for exotic host runtimes, same as the storage
                        // service: delete + move. The temp file is fully written first, so
                        // the target is never partial — worst case it is briefly absent.
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        File.Move(tempPath, fullPath);
                    }
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static void GenerateAllCode()
        {
            // GenerateAllCode writes exactly the mapping the test suite locks against:
            // same bytes, same paths — no trimming, no transformation.
            WriteGeneratedFiles(GameRoot);

            AssetDatabase.Refresh();
            Debug.Log("[Nexus] Sample game files generated under " + GameRoot + "/");
        }
    }
}
