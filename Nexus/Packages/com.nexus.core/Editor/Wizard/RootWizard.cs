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
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            // Dark elegant title
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                margin = new RectOffset(10, 10, 10, 10)
            };
            titleStyle.normal.textColor = new Color(0.3f, 0.8f, 1f);

            GUILayout.Label("Nexus: Observable Architecture Setup", titleStyle);
            EditorGUILayout.Space();

            // Find manifest
            var manifest = FindBootstrapManifest();

            DrawManifestSection(manifest);

            EditorGUILayout.Space(15);
            DrawSeparator();
            EditorGUILayout.Space(15);

            DrawCustomRootSection();

            EditorGUILayout.EndScrollView();
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
    }
}
