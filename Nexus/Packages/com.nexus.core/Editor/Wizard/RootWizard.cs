using UnityEditor;
using UnityEngine;
using System.IO;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Editor
{
    public class RootWizard : EditorWindow
    {
        private string _contextName = "Gameplay";
        private string _scopeTag = "Gameplay";
        private Vector2 _scrollPosition;
        private Root _parentRoot = null;
        private List<string> _availableAssemblies = new();
        private HashSet<string> _selectedAssemblies = new();
        private bool _assembliesFoldout = false;
        private Vector2 _assembliesScroll;
        private bool _generateLifecycleScript = false;
        private bool _generateSampleArchitecture = false;

        // Tab Fields
        private string[] _tabNames = { "Create Root", "View/Mediator Gen", "Clean Deletion" };
        private int _selectedTab = 0;

        // View/Mediator Gen Fields
        private string _viewName = "GameplayHUD";
        private Root _viewTargetRoot = null;
        private bool _createViewGo = true;

        // Clean Deletion Fields
        private Root _rootToDelete = null;

        [MenuItem("GameObject/Nexus/Create Root", false, 10)]
        [MenuItem("Window/Nexus/Root Wizard")]
        public static void ShowWindow()
        {
            var window = GetWindow<RootWizard>("Nexus Root Wizard");
            window.minSize = new Vector2(400, 450);
            window.Show();
        }

        private void OnEnable()
        {
            PopulateAssemblies();
        }

        private void PopulateAssemblies()
        {
            _availableAssemblies.Clear();
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono") || 
                    name.StartsWith("UnityEngine") || name.StartsWith("UnityEditor") || name.StartsWith("nunit") || 
                    name.Contains("PlayerLoop") || name.Contains("JetBrains"))
                {
                    continue;
                }
                if (!_availableAssemblies.Contains(name))
                {
                    _availableAssemblies.Add(name);
                }
            }
            _availableAssemblies.Sort();

            // Pre-select Assembly-CSharp by default since most game project code lives there
            if (_availableAssemblies.Contains("Assembly-CSharp"))
            {
                _selectedAssemblies.Add("Assembly-CSharp");
            }
        }

        private void OnGUI()
        {
            // Dark elegant title
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                margin = new RectOffset(10, 10, 10, 10)
            };
            titleStyle.normal.textColor = new Color(0.3f, 0.8f, 1f);

            GUILayout.Label("Nexus: Observable Architecture Setup", titleStyle);
            
            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames);
            EditorGUILayout.Space(10);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            switch (_selectedTab)
            {
                case 0:
                    DrawCreateRootTab();
                    break;
                case 1:
                    DrawViewMediatorGenTab();
                    break;
                case 2:
                    DrawCleanDeletionTab();
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCreateRootTab()
        {
            // Find manifest
            var manifest = FindBootstrapManifest();

            DrawManifestSection(manifest);

            EditorGUILayout.Space(15);
            DrawSeparator();
            EditorGUILayout.Space(15);

            DrawCustomRootSection();
        }

        private void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
        }

        private NexusBootstrapManifest FindBootstrapManifest()
        {
            var guids = AssetDatabase.FindAssets("t:NexusBootstrapManifest");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<NexusBootstrapManifest>(path);
            }
            return null;
        }

        private void DrawManifestSection(NexusBootstrapManifest manifest)
        {
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            headerStyle.normal.textColor = Color.white;

            GUILayout.Label("Bootstrap Manifest Generation", headerStyle);
            EditorGUILayout.Space(5);

            if (manifest == null)
            {
                EditorGUILayout.HelpBox("No NexusBootstrapManifest found in the project. Create one to enable skeleton generation.", MessageType.Info);
                if (GUILayout.Button("Create Default Bootstrap Manifest", GUILayout.Height(25)))
                {
                    CreateDefaultManifest();
                }
            }
            else
            {
                EditorGUILayout.ObjectField("Active Manifest", manifest, typeof(NexusBootstrapManifest), false);
                EditorGUILayout.Space(5);

                EditorGUI.indentLevel = 1;
                EditorGUILayout.LabelField("Default Contexts:", string.Join(", ", manifest.DefaultContextNames));
                EditorGUILayout.Toggle("Generate Samples", manifest.GenerateSampleSignals || manifest.GenerateSampleCommands);
                EditorGUILayout.Toggle("Enable Inspector", manifest.EnableInspector);
                EditorGUI.indentLevel = 0;

                EditorGUILayout.Space(10);

                var buttonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontStyle = FontStyle.Bold,
                    fixedHeight = 30
                };
                buttonStyle.normal.textColor = new Color(0.4f, 1f, 0.4f);

                if (GUILayout.Button("Generate Skeleton from Manifest", buttonStyle))
                {
                    GenerateSkeleton(manifest);
                }
            }
        }

        private void DrawCustomRootSection()
        {
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            headerStyle.normal.textColor = Color.white;

            GUILayout.Label("Custom Root Context Creation", headerStyle);
            EditorGUILayout.Space(5);

            EditorGUI.indentLevel = 1;
            _contextName = EditorGUILayout.TextField("Context Name", _contextName);
            _scopeTag = EditorGUILayout.TextField("Scope Tag", _scopeTag);

            // Parent Root Dropdown selection
            var sceneRoots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            var rootNames = GetSceneRootNames(sceneRoots);
            int selectedIndex = 0;
            if (_parentRoot != null)
            {
                for (int i = 0; i < sceneRoots.Length; i++)
                {
                    if (sceneRoots[i] == _parentRoot)
                    {
                        selectedIndex = i + 1;
                        break;
                    }
                }
            }
            int newIndex = EditorGUILayout.Popup("Parent Root", selectedIndex, rootNames);
            if (newIndex == 0)
            {
                _parentRoot = null;
            }
            else
            {
                _parentRoot = sceneRoots[newIndex - 1];
            }

            // Assembly Scope Multi-select foldout
            EditorGUILayout.Space(5);
            _assembliesFoldout = EditorGUILayout.Foldout(_assembliesFoldout, $"Assembly Scopes ({_selectedAssemblies.Count} selected)");
            if (_assembliesFoldout)
            {
                EditorGUI.indentLevel++;
                var scrollHeight = Mathf.Min(_availableAssemblies.Count * 20 + 5, 120);
                _assembliesScroll = EditorGUILayout.BeginScrollView(_assembliesScroll, GUILayout.Height(scrollHeight));
                foreach (var assemblyName in _availableAssemblies)
                {
                    bool isSelected = _selectedAssemblies.Contains(assemblyName);
                    bool newSelected = EditorGUILayout.ToggleLeft(assemblyName, isSelected);
                    if (newSelected && !isSelected)
                    {
                        _selectedAssemblies.Add(assemblyName);
                    }
                    else if (!newSelected && isSelected)
                    {
                        _selectedAssemblies.Remove(assemblyName);
                    }
                }
                EditorGUILayout.EndScrollView();
                EditorGUI.indentLevel--;
            }

            // Lifecycle template toggle
            EditorGUILayout.Space(5);
            _generateLifecycleScript = EditorGUILayout.Toggle("Create Lifecycle Template", _generateLifecycleScript);

            EditorGUI.BeginDisabledGroup(!_generateLifecycleScript);
            _generateSampleArchitecture = EditorGUILayout.Toggle("Create Architecture Boilerplate", _generateSampleArchitecture && _generateLifecycleScript);
            EditorGUI.EndDisabledGroup();

            EditorGUI.indentLevel = 0;

            EditorGUILayout.Space(10);

            // Validation logic
            bool isValid = true;
            string validationError = "";

            if (string.IsNullOrWhiteSpace(_contextName))
            {
                isValid = false;
                validationError = "Context Name cannot be empty.";
            }
            else if (string.IsNullOrWhiteSpace(_scopeTag))
            {
                isValid = false;
                validationError = "Scope Tag cannot be empty.";
            }
            else
            {
                // Validate if Asset already exists
                string path = $"Assets/Settings/{_contextName}ContextData.asset";
                if (File.Exists(path))
                {
                    isValid = false;
                    validationError = $"A ContextData asset already exists at {path}. Use a different Context Name.";
                }
            }

            if (!isValid)
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            }

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fixedHeight = 30
            };

            EditorGUI.BeginDisabledGroup(!isValid);
            if (GUILayout.Button("Create Root & ContextData", buttonStyle))
            {
                CreateRoot(_contextName, _scopeTag);
            }
            EditorGUI.EndDisabledGroup();
        }

        private string[] GetSceneRootNames(Root[] roots)
        {
            var names = new string[roots.Length + 1];
            names[0] = "None (Root Context)";
            for (int i = 0; i < roots.Length; i++)
            {
                names[i + 1] = roots[i].gameObject.name;
            }
            return names;
        }

        private void CreateDefaultManifest()
        {
            var manifest = ScriptableObject.CreateInstance<NexusBootstrapManifest>();
            manifest.DefaultContextNames = new string[] { "Global", "Gameplay", "UI" };
            manifest.GenerateSampleSignals = true;
            manifest.GenerateSampleCommands = true;
            manifest.EnableInspector = true;

            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
            {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            string path = "Assets/Settings/NexusBootstrapManifest.asset";
            AssetDatabase.CreateAsset(manifest, path);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Nexus] Created default Bootstrap Manifest at {path}");
            AssetDatabase.Refresh();
        }

        private void GenerateSkeleton(NexusBootstrapManifest manifest)
        {
            foreach (var name in manifest.DefaultContextNames)
            {
                CreateRoot(name, name);
            }

            if (manifest.GenerateSampleSignals || manifest.GenerateSampleCommands)
            {
                string samplesDir = "Assets/Samples/Nexus";
                if (!Directory.Exists(samplesDir))
                {
                    Directory.CreateDirectory(samplesDir);
                }

                if (manifest.GenerateSampleSignals)
                {
                    string signalPath = Path.Combine(samplesDir, "SampleSignals.cs");
                    File.WriteAllText(signalPath, GetSampleSignalCode());
                }

                if (manifest.GenerateSampleCommands)
                {
                    string commandPath = Path.Combine(samplesDir, "SampleCommands.cs");
                    File.WriteAllText(commandPath, GetSampleCommandCode());
                }

                AssetDatabase.Refresh();
            }

            EditorUtility.DisplayDialog("Nexus Setup", "Skeleton generated successfully!", "OK");
        }

        private void CreateRoot(string contextName, string scopeTag)
        {
            // 1. Create GameObject
            var go = new GameObject($"{contextName}Root");
            var root = go.AddComponent<Root>();
            if (_parentRoot != null)
            {
                go.transform.SetParent(_parentRoot.transform);
            }

            // 2. Create ContextData asset
            var contextData = ScriptableObject.CreateInstance<ContextData>();
            contextData.ScopeTag = scopeTag;
            contextData.AssemblyScopes = new List<string>(_selectedAssemblies).ToArray();

            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
            {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            string path = $"Assets/Settings/{contextName}ContextData.asset";
            AssetDatabase.CreateAsset(contextData, path);
            AssetDatabase.SaveAssets();

            var serializedRoot = new SerializedObject(root);
            var contextDataProp = serializedRoot.FindProperty("contextData");
            if (contextDataProp != null)
            {
                contextDataProp.objectReferenceValue = contextData;
            }

            if (_parentRoot != null)
            {
                var parentProp = serializedRoot.FindProperty("parentRoot");
                if (parentProp != null)
                {
                    parentProp.objectReferenceValue = _parentRoot;
                }
            }
            serializedRoot.ApplyModifiedProperties();

            // 3. Create Lifecycle script template if checked
            if (_generateLifecycleScript)
            {
                string scriptsDir = "Assets/Scripts/Nexus";
                if (!Directory.Exists(scriptsDir))
                {
                    Directory.CreateDirectory(scriptsDir);
                }
                
                string scriptPath;
                if (_generateSampleArchitecture)
                {
                    string contextDir = Path.Combine(scriptsDir, contextName);
                    string signalsDir = Path.Combine(contextDir, "Signals");
                    string modelsDir = Path.Combine(contextDir, "Models");
                    string commandsDir = Path.Combine(contextDir, "Commands");
                    string viewsDir = Path.Combine(contextDir, "Views");

                    Directory.CreateDirectory(contextDir);
                    Directory.CreateDirectory(signalsDir);
                    Directory.CreateDirectory(modelsDir);
                    Directory.CreateDirectory(commandsDir);
                    Directory.CreateDirectory(viewsDir);

                    File.WriteAllText(Path.Combine(signalsDir, $"{contextName}Signals.cs"), GetSignalsBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(modelsDir, $"I{contextName}Model.cs"), GetModelInterfaceBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(modelsDir, $"{contextName}Model.cs"), GetModelImplementationBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(commandsDir, $"{contextName}Command.cs"), GetCommandBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(viewsDir, $"{contextName}View.cs"), GetViewBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(viewsDir, $"{contextName}Mediator.cs"), GetMediatorBoilerplate(contextName));

                    scriptPath = Path.Combine(contextDir, $"{contextName}Lifecycle.cs");
                    File.WriteAllText(scriptPath, GetLifecycleBoilerplateWithBindings(contextName));
                }
                else
                {
                    scriptPath = Path.Combine(scriptsDir, $"{contextName}Lifecycle.cs");
                    File.WriteAllText(scriptPath, GetLifecycleTemplateCode(contextName));
                }
                Debug.Log($"[Nexus] Generated lifecycle template at {scriptPath}");
            }

            // 4. Register Undo & Focus
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Nexus Root");

            AssetDatabase.Refresh();

            string parentInfo = _parentRoot != null ? $" with Parent '{_parentRoot.gameObject.name}'" : "";
            string lifecycleInfo = _generateLifecycleScript ? 
                (_generateSampleArchitecture ? $"\n\nGenerated full architecture folders and templates under Assets/Scripts/Nexus/{contextName}/" : $"\n\nGenerated lifecycle class template at Assets/Scripts/Nexus/{contextName}Lifecycle.cs.") : "";
            
            EditorUtility.DisplayDialog("Root Created", 
                $"Successfully created {go.name}{parentInfo} and registered ContextData asset at {path}.{lifecycleInfo}", 
                "OK");

            Debug.Log($"[Nexus] Successfully created {go.name} and registered context data at {path}.");
        }

        private string GetLifecycleTemplateCode(string contextName)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    // Automatically discovered and bound by Nexus based on naming convention ({contextName}Lifecycle).
    // No need to attach this to any GameObject!
    public class {contextName}Lifecycle : IContextLifecycle
    {{
        public void OnConfigure(IContextBuilder builder)
        {{
            // Bind models, commands, and dependencies here
            Debug.Log(""[{contextName}Lifecycle] Configuring context..."");
        }}

        public ValueTask OnInitializeAsync(CancellationToken ct)
        {{
            // Async initialization logic
            return default;
        }}

        public ValueTask OnStartAsync(CancellationToken ct)
        {{
            // Start logic (executed after initialization)
            return default;
        }}

        public void OnDispose()
        {{
            // Cleanup logic
            Debug.Log(""[{contextName}Lifecycle] Context disposed."");
        }}
    }}
}}
";
        }

        private string GetSampleSignalCode()
        {
            return @"namespace Nexus.Samples
{
    public readonly struct SampleSignal
    {
        public readonly string Message;
        public SampleSignal(string message) => Message = message;
    }
}
";
        }

        private string GetSampleCommandCode()
        {
            return @"using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples
{
    [SignalHandler(typeof(SampleSignal))]
    public class SampleCommand : ICommand
    {
        public void Execute()
        {
            Debug.Log($""[Nexus] SampleCommand executed successfully with message: {Application.productName}"");
        }
    }
}
";
        }

        private string GetSignalsBoilerplate(string contextName)
        {
            return $@"namespace Nexus
{{
    // Simple struct signal with counter payload
    public readonly struct {contextName}CounterSignal
    {{
        public readonly int Value;
        public {contextName}CounterSignal(int value) => Value = value;
    }}
}}
";
        }

        private string GetModelInterfaceBoilerplate(string contextName)
        {
            return $@"using System;

namespace Nexus
{{
    public interface I{contextName}Model
    {{
        int Counter {{ get; }}
        event Action<int> OnCounterChanged;
        void Increment(int amount);
    }}
}}
";
        }

        private string GetModelImplementationBoilerplate(string contextName)
        {
            return $@"using System;
using UnityEngine;

namespace Nexus
{{
    public class {contextName}Model : I{contextName}Model
    {{
        public int Counter {{ get; private set; }}
        public event Action<int> OnCounterChanged;

        public void Increment(int amount)
        {{
            Counter += amount;
            Debug.Log($""[{{nameof({contextName}Model)}}] Counter changed to: {{Counter}}"");
            OnCounterChanged?.Invoke(Counter);
        }}
    }}
}}
";
        }

        private string GetCommandBoilerplate(string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    // Command that handles the struct signal and updates the injected model
    public class {contextName}IncrementCommand : ICommand
    {{
        [Inject] public I{contextName}Model Model {{ get; set; }}
        [Inject] public {contextName}CounterSignal Signal {{ get; set; }}

        public void Execute()
        {{
            Debug.Log($""[{{nameof({contextName}IncrementCommand)}}] Executing command with signal payload: {{Signal.Value}}"");
            Model.Increment(Signal.Value);
        }}
    }}
}}
";
        }

        private string GetViewBoilerplate(string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus
{{
    // Automatically binds the view instance to its custom Mediator on context registration
    [Mediator(typeof({contextName}Mediator))]
    public class {contextName}View : View
    {{
        public event System.Action OnButtonClicked;

        [Header(""UI References (Assign in Inspector)"")]
        [SerializeField] private Button incrementButton;
        [SerializeField] private Text counterText;

        protected override void OnBind(IContext context)
        {{
            if (incrementButton != null)
            {{
                incrementButton.onClick.AddListener(() => OnButtonClicked?.Invoke());
            }}
        }}

        protected override void OnUnbind()
        {{
            if (incrementButton != null)
            {{
                incrementButton.onClick.RemoveAllListeners();
            }}
        }}

        [ContextMenu(""Simulate Button Click"")]
        public void SimulateClick()
        {{
            OnButtonClicked?.Invoke();
        }}

        public void UpdateCounterText(int value)
        {{
            if (counterText != null)
            {{
                counterText.text = $""Counter: {{value}}"";
            }}
            else
            {{
                Debug.Log($""[{{nameof({contextName}View)}}] UI Counter updated to: {{value}}"");
            }}
        }}
    }}
}}
";
        }

        private string GetMediatorBoilerplate(string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    public class {contextName}Mediator : Mediator<{contextName}View>
    {{
        [Inject] public I{contextName}Model Model {{ get; set; }}

        protected override void OnBind()
        {{
            Debug.Log($""[{{nameof({contextName}Mediator)}}] Binding View to Model..."");

            // Listen to model changes
            Model.OnCounterChanged += OnModelCounterChanged;

            // Initialize view state
            View.UpdateCounterText(Model.Counter);

            // Respond to user interaction
            View.OnButtonClicked += OnViewButtonClicked;
        }}

        protected override void OnUnbind()
        {{
            Debug.Log($""[{{nameof({contextName}Mediator)}}] Unbinding..."");

            if (Model != null)
            {{
                Model.OnCounterChanged -= OnModelCounterChanged;
            }}

            if (View != null)
            {{
                View.OnButtonClicked -= OnViewButtonClicked;
            }}
        }}

        private void OnViewButtonClicked()
        {{
            Debug.Log($""[{{nameof({contextName}Mediator)}}] Button clicked on view! Dispatching counter signal..."");
            SignalBus.Fire(new {contextName}CounterSignal(1));
        }}

        private void OnModelCounterChanged(int newValue)
        {{
            View.UpdateCounterText(newValue);
        }}
    }}
}}
";
        }

        private string GetLifecycleBoilerplateWithBindings(string contextName)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    // Automatically discovered and bound by Nexus based on naming convention ({contextName}Lifecycle).
    // No need to attach this to any GameObject!
    public class {contextName}Lifecycle : IContextLifecycle
    {{
        public void OnConfigure(IContextBuilder builder)
        {{
            Debug.Log($""[{{nameof({contextName}Lifecycle)}}] Configuring architecture layers..."");

            // 1. Bind Observable/Reactive Model
            builder.BindModel<I{contextName}Model, {contextName}Model>();

            // 2. Bind Command that reacts to the struct signal
            builder.BindCommand<{contextName}CounterSignal, {contextName}IncrementCommand>();
        }}

        public ValueTask OnInitializeAsync(CancellationToken ct)
        {{
            // Async initialization logic
            return default;
        }}

        public ValueTask OnStartAsync(CancellationToken ct)
        {{
            // Start logic (executed after initialization)
            return default;
        }}

        public void OnDispose()
        {{
            Debug.Log($""[{{nameof({contextName}Lifecycle)}}] Context disposed."");
        }}
    }}
}}
";
        }

        // --- View & Mediator Generator Tab ---
        private void DrawViewMediatorGenTab()
        {
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            headerStyle.normal.textColor = Color.white;

            GUILayout.Label("Generate View & Mediator", headerStyle);
            EditorGUILayout.Space(5);

            _viewName = EditorGUILayout.TextField("View Name", _viewName);

            var sceneRoots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            var rootNames = new string[sceneRoots.Length];
            int selectedIndex = 0;
            for (int i = 0; i < sceneRoots.Length; i++)
            {
                rootNames[i] = sceneRoots[i].gameObject.name;
                if (sceneRoots[i] == _viewTargetRoot)
                {
                    selectedIndex = i;
                }
            }

            if (sceneRoots.Length == 0)
            {
                EditorGUILayout.HelpBox("No active Roots found in scene. Create a Root first.", MessageType.Warning);
                return;
            }

            int newIndex = EditorGUILayout.Popup("Target Root Context", selectedIndex, rootNames);
            _viewTargetRoot = sceneRoots[newIndex];

            _createViewGo = EditorGUILayout.Toggle("Sahnede GameObject Oluştur", _createViewGo);

            EditorGUILayout.Space(10);

            bool isValid = !string.IsNullOrWhiteSpace(_viewName);
            if (!isValid)
            {
                EditorGUILayout.HelpBox("View Name cannot be empty.", MessageType.Warning);
            }

            EditorGUI.BeginDisabledGroup(!isValid);
            if (GUILayout.Button("Generate View & Mediator", GUILayout.Height(30)))
            {
                GenerateViewAndMediator(_viewName, _viewTargetRoot, _createViewGo);
            }
            EditorGUI.EndDisabledGroup();
        }

        private void GenerateViewAndMediator(string viewName, Root targetRoot, bool createGo)
        {
            string contextName = targetRoot.ContextData != null ? targetRoot.ContextData.name.Replace("ContextData", "") : targetRoot.gameObject.name.Replace("Root", "");
            string viewsDir = $"Assets/Scripts/Nexus/{contextName}/Views";

            try
            {
                if (!Directory.Exists(viewsDir))
                {
                    Directory.CreateDirectory(viewsDir);
                }

                string viewPath = Path.Combine(viewsDir, $"{viewName}View.cs");
                string mediatorPath = Path.Combine(viewsDir, $"{viewName}Mediator.cs");

                if (File.Exists(viewPath) || File.Exists(mediatorPath))
                {
                    if (!EditorUtility.DisplayDialog("Overwrite Files?", $"Files for {viewName}View already exist. Do you want to overwrite them?", "Yes", "No"))
                    {
                        return;
                    }
                }

                File.WriteAllText(viewPath, GetGenericViewBoilerplate(viewName, contextName));
                File.WriteAllText(mediatorPath, GetGenericMediatorBoilerplate(viewName, contextName));

                if (createGo)
                {
                    EditorPrefs.SetString("Nexus_PendingViewName", viewName);
                    EditorPrefs.SetString("Nexus_PendingViewRootName", targetRoot.gameObject.name);
                }

                AssetDatabase.Refresh();

                string goMsg = createGo ? " and queued GameObject creation post-compile" : "";
                EditorUtility.DisplayDialog("Generated successfully", $"Successfully generated {viewName}View and {viewName}Mediator under {viewsDir}{goMsg}.", "OK");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Nexus] View/Mediator generation failed: {ex.Message}");
                EditorUtility.DisplayDialog("Generation Error", $"Failed to generate View/Mediator: {ex.Message}", "OK");
            }
        }

        private string GetGenericViewBoilerplate(string viewName, string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    [Mediator(typeof({viewName}Mediator))]
    public class {viewName}View : View
    {{
        // Define your view events, fields and UI elements here

        protected override void OnBind(IContext context)
        {{
            Debug.Log($""[{{nameof({viewName}View)}}] Bound to context {contextName}"");
        }}

        protected override void OnUnbind()
        {{
            Debug.Log($""[{{nameof({viewName}View)}}] Unbound"");
        }}
    }}
}}
";
        }

        private string GetGenericMediatorBoilerplate(string viewName, string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    public class {viewName}Mediator : Mediator<{viewName}View>
    {{
        protected override void OnBind()
        {{
            Debug.Log($""[{{nameof({viewName}Mediator)}}] Binding View to Model..."");
        }}

        protected override void OnUnbind()
        {{
            Debug.Log($""[{{nameof({viewName}Mediator)}}] Unbinding..."");
        }}
    }}
}}
";
        }

        // --- Clean Deletion Tab ---
        private void DrawCleanDeletionTab()
        {
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            headerStyle.normal.textColor = Color.white;

            GUILayout.Label("Clean Deletion Tool", headerStyle);
            EditorGUILayout.Space(5);

            var sceneRoots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            if (sceneRoots.Length == 0)
            {
                EditorGUILayout.HelpBox("No active Roots found in scene.", MessageType.Info);
                return;
            }

            var rootNames = new string[sceneRoots.Length];
            int selectedIndex = 0;
            for (int i = 0; i < sceneRoots.Length; i++)
            {
                rootNames[i] = sceneRoots[i].gameObject.name;
                if (sceneRoots[i] == _rootToDelete)
                {
                    selectedIndex = i;
                }
            }

            int newIndex = EditorGUILayout.Popup("Root Context to Delete", selectedIndex, rootNames);
            _rootToDelete = sceneRoots[newIndex];

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("WARNING: This will permanently delete:\n" +
                                    "- The Root GameObject from the active scene.\n" +
                                    "- The associated ContextData ScriptableObject.\n" +
                                    "- The generated script directory Assets/Scripts/Nexus/<ContextName>/\n\n" +
                                    "Make sure you have backed up your custom script changes before proceeding!", MessageType.Warning);

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fixedHeight = 30
            };
            buttonStyle.normal.textColor = new Color(1f, 0.3f, 0.3f);

            if (GUILayout.Button("DELETE ROOT & ALL RELATED ASSETS", buttonStyle))
            {
                string contextName = _rootToDelete.ContextData != null ? _rootToDelete.ContextData.name.Replace("ContextData", "") : _rootToDelete.gameObject.name.Replace("Root", "");
                if (EditorUtility.DisplayDialog("Confirm Clean Deletion", 
                    $"Are you absolutely sure you want to delete context '{contextName}' and all its assets/GameObjects? This action cannot be fully undone.", 
                    "Yes, Delete", "Cancel"))
                  {
                      DeleteRootContext(_rootToDelete);
                  }
            }
        }

        private void DeleteRootContext(Root root)
        {
            if (root == null) return;
            try
            {
                var go = root.gameObject;
                string contextName = root.ContextData != null ? root.ContextData.name.Replace("ContextData", "") : go.name.Replace("Root", "");

                // 1. Delete ContextData Asset
                if (root.ContextData != null)
                {
                    string assetPath = AssetDatabase.GetAssetPath(root.ContextData);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        AssetDatabase.DeleteAsset(assetPath);
                        Debug.Log($"[Nexus] Deleted ContextData asset at: {assetPath}");
                    }
                }

                // 2. Delete generated scripts directory or lifecycle script
                string scriptsDir = $"Assets/Scripts/Nexus/{contextName}";
                if (AssetDatabase.IsValidFolder(scriptsDir))
                {
                    AssetDatabase.DeleteAsset(scriptsDir);
                    Debug.Log($"[Nexus] Deleted script directory: {scriptsDir}");
                }
                else
                {
                    string flatScriptPath = $"Assets/Scripts/Nexus/{contextName}Lifecycle.cs";
                    if (File.Exists(flatScriptPath))
                    {
                        AssetDatabase.DeleteAsset(flatScriptPath);
                        Debug.Log($"[Nexus] Deleted flat lifecycle script: {flatScriptPath}");
                    }
                }

                // 3. Destroy GameObject in scene
                Undo.DestroyObjectImmediate(go);
                Debug.Log($"[Nexus] Destroyed Root GameObject '{go.name}'");

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Root Deleted", $"Successfully deleted context '{contextName}' and its related assets.", "OK");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Nexus] Failed to cleanly delete root: {ex.Message}");
                EditorUtility.DisplayDialog("Deletion Error", $"Failed to delete root: {ex.Message}", "OK");
            }
        }
    }

    [InitializeOnLoad]
    public static class NexusPostCompileViewCreator
    {
        static NexusPostCompileViewCreator()
        {
            EditorApplication.delayCall += CheckAndCreatePendingViewObject;
        }

        private static void CheckAndCreatePendingViewObject()
        {
            if (!EditorPrefs.HasKey("Nexus_PendingViewName")) return;

            string viewName = EditorPrefs.GetString("Nexus_PendingViewName");
            string rootName = EditorPrefs.GetString("Nexus_PendingViewRootName");
            EditorPrefs.DeleteKey("Nexus_PendingViewName");
            EditorPrefs.DeleteKey("Nexus_PendingViewRootName");

            System.Type viewType = null;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "Assembly-CSharp" || assembly.GetName().Name == "com.nexus.core")
                {
                    viewType = assembly.GetType($"Nexus.{viewName}View");
                    if (viewType != null) break;
                }
            }

            if (viewType == null)
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    viewType = assembly.GetType($"Nexus.{viewName}View");
                    if (viewType != null) break;
                }
            }

            if (viewType == null)
            {
                Debug.LogError($"[Nexus] Could not find compiled type 'Nexus.{viewName}View' after assembly reload.");
                return;
            }

            GameObject parentGo = GameObject.Find(rootName);
            if (parentGo == null)
            {
                var rootComp = GameObject.FindAnyObjectByType<Root>();
                if (rootComp != null) parentGo = rootComp.gameObject;
            }

            var viewGo = new GameObject(viewName);
            if (parentGo != null)
            {
                viewGo.transform.SetParent(parentGo.transform);
            }
            
            var viewComponent = viewGo.AddComponent(viewType);
            
            Undo.RegisterCreatedObjectUndo(viewGo, $"Create {viewName} GameObject");
            Selection.activeGameObject = viewGo;

            Debug.Log($"[Nexus] Successfully created GameObject '{viewName}' with component '{viewType.Name}' attached under root '{parentGo?.name}'.");
        }
    }
}
