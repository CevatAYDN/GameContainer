using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using Nexus.Core;
using Object = UnityEngine.Object;

namespace Nexus.Editor
{
    /// <summary>
    /// Contract for a Wizard sub-tab. Each tab owns its UI and logic,
    /// keeping <see cref="WizardPlugin"/> as a thin coordinator.
    /// </summary>
    internal interface IWizardTab
    {
        string Title { get; }
        void BuildUI(VisualElement container);
    }

    // ─── Create Root Tab ────────────────────────────────────────
    internal class CreateRootTab : IWizardTab
    {
        public string Title => NexusLang.Get("wizard_subtab_create_root");

        private string _contextName = "Gameplay";
        private string _scopeTag = "Gameplay";
        private string _scriptsPath = "Assets/Scripts/Nexus";
        private string _settingsPath = "Assets/Settings";
        private string _parentRootName = "None (Root Context)";
        private Label _validationLabel;
        private Button _createBtn;

        public void BuildUI(VisualElement container)
        {
            var manifest = FindBootstrapManifest();
            if (manifest == null)
            {
                container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_hint_no_manifest")));
                container.Add(NexusEditorStyles.CreateButton(NexusLang.Get("wizard_create_manifest"), CreateDefaultManifest, NexusEditorStyles.BtnBlue));
            }
            else
            {
                var details = new VisualElement { style = { paddingLeft = 10, marginTop = 4 } };
                details.Add(new Label(string.Format(NexusLang.Get("wizard_label_active_manifest"), manifest.name)) { style = { color = Color.white, fontSize = 10 } });
                details.Add(new Label(string.Format(NexusLang.Get("wizard_label_default_contexts"), string.Join(", ", manifest.DefaultContextNames))) { style = { color = NexusEditorStyles.TextSecondary, fontSize = 9 } });
                container.Add(details);
                container.Add(NexusEditorStyles.CreateButton(NexusLang.Get("wizard_gen_skeleton"), () => GenerateSkeleton(manifest), NexusEditorStyles.BtnBlue));
            }

            var creationGroup = NexusEditorStyles.CreateActionGroup(container, NexusLang.Get("wizard_section_create_root"));

            // Validation label (hidden by default)
            _validationLabel = new Label { style = { color = new StyleColor(NexusEditorStyles.AccentRed), fontSize = 10, marginBottom = 4, display = DisplayStyle.None } };
            creationGroup.Add(_validationLabel);

            var contextNameField = new TextField(NexusLang.Get("wizard_field_context_name")) { value = _contextName };
            contextNameField.RegisterValueChangedCallback(evt => { _contextName = evt.newValue; ValidateForm(); });
            creationGroup.Add(contextNameField);

            var scopeTagField = new TextField(NexusLang.Get("wizard_field_scope_tag")) { value = _scopeTag };
            scopeTagField.RegisterValueChangedCallback(evt => { _scopeTag = evt.newValue; ValidateForm(); });
            creationGroup.Add(scopeTagField);

            // Paths
            var pathsGroup = new VisualElement { style = { marginTop = 4, borderTopWidth = 1, borderTopColor = new StyleColor(NexusEditorStyles.BorderColor), paddingTop = 6 } };
            pathsGroup.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_paths_config")));

            var scriptsPathRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var scriptsPathField = new TextField(NexusLang.Get("wizard_field_scripts_folder")) { value = _scriptsPath, style = { flexGrow = 1 } };
            scriptsPathField.RegisterValueChangedCallback(evt => _scriptsPath = evt.newValue);
            scriptsPathRow.Add(scriptsPathField);
            scriptsPathRow.Add(new Button(() => BrowseFolder(path => scriptsPathField.value = path)) { text = NexusLang.Get("wizard_browse") });
            pathsGroup.Add(scriptsPathRow);

            var settingsPathRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 4 } };
            var settingsPathField = new TextField(NexusLang.Get("wizard_field_settings_folder")) { value = _settingsPath, style = { flexGrow = 1 } };
            settingsPathField.RegisterValueChangedCallback(evt => _settingsPath = evt.newValue);
            settingsPathRow.Add(settingsPathField);
            settingsPathRow.Add(new Button(() => BrowseFolder(path => settingsPathField.value = path)) { text = NexusLang.Get("wizard_browse") });
            pathsGroup.Add(settingsPathRow);
            creationGroup.Add(pathsGroup);

            // Parent root
            var rootChoices = GetSceneRootNames();
            var parentRootDropdown = new DropdownField(NexusLang.Get("wizard_field_parent_root"), rootChoices, 0);
            parentRootDropdown.RegisterValueChangedCallback(evt => _parentRootName = evt.newValue);
            creationGroup.Add(parentRootDropdown);

            // Assembly scopes
            var assemblies = GetAvailableAssemblies();
            var selectedAssemblies = new HashSet<string>();
            foreach (var asm in assemblies)
            {
                var toggle = new Toggle(asm) { value = true };
                toggle.RegisterValueChangedCallback(evt => { if (evt.newValue) selectedAssemblies.Add(asm); else selectedAssemblies.Remove(asm); });
                creationGroup.Add(toggle);
            }

            // Create button (with validation)
            _createBtn = new Button(() =>
            {
                if (!IsValid()) return;
                NexusSetupWizardHelper.CreateRootContext(_contextName, _scopeTag, _scriptsPath, _settingsPath, _parentRootName, selectedAssemblies.ToArray());
            })
            {
                text = NexusLang.Get("wizard_create_root_caps"),
                style = { marginTop = 10, backgroundColor = new StyleColor(NexusEditorStyles.BtnBlue), color = Color.white }
            };
            creationGroup.Add(_createBtn);

            ValidateForm();
        }

        private bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(_contextName) && !string.IsNullOrWhiteSpace(_scopeTag);
        }

        private void ValidateForm()
        {
            if (_validationLabel == null) return;
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(_contextName))
                errors.Add(NexusLang.Get("wizard_validation_context_name"));
            if (string.IsNullOrWhiteSpace(_scopeTag))
                errors.Add(NexusLang.Get("wizard_validation_scope_tag"));
            bool valid = errors.Count == 0;
            _validationLabel.text = string.Join("\n", errors);
            _validationLabel.style.display = valid ? DisplayStyle.None : DisplayStyle.Flex;
            if (_createBtn != null) _createBtn.SetEnabled(valid);
        }

        private static NexusBootstrapManifest FindBootstrapManifest()
        {
            var guids = AssetDatabase.FindAssets("t:NexusBootstrapManifest");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                return AssetDatabase.LoadAssetAtPath<NexusBootstrapManifest>(path);
            }
            return null;
        }

        private static void CreateDefaultManifest()
        {
            var manifest = ScriptableObject.CreateInstance<NexusBootstrapManifest>();
            manifest.name = "GameBootstrapManifest";
            AssetDatabase.CreateAsset(manifest, "Assets/GameBootstrapManifest.asset");
            AssetDatabase.SaveAssets();
            Debug.Log("[Nexus] Created bootstrap manifest at Assets/GameBootstrapManifest.asset");
        }

        private static void GenerateSkeleton(NexusBootstrapManifest manifest)
        {
            foreach (var ctxName in manifest.DefaultContextNames)
            {
                if (!string.IsNullOrEmpty(ctxName))
                {
                    var scriptsPath = $"Assets/Scripts/Game/{ctxName}";
                    Directory.CreateDirectory(scriptsPath);
                    GenerateContextFiles(ctxName, scriptsPath);
                }
            }
            AssetDatabase.Refresh();
            Debug.Log("[Nexus] Skeleton generation complete.");
        }

        private static void GenerateContextFiles(string ctxName, string scriptsPath) { }

        private static void BrowseFolder(Action<string> onSelected)
        {
            var path = EditorUtility.OpenFolderPanel("Select Folder", "Assets", "");
            if (!string.IsNullOrEmpty(path))
            {
                if (path.StartsWith(Application.dataPath))
                    path = "Assets" + path.Substring(Application.dataPath.Length);
                onSelected(path);
            }
        }

        private static List<string> GetSceneRootNames()
        {
            var names = new List<string> { "None (Root Context)" };
            foreach (var root in GameObject.FindObjectsByType<Root>(FindObjectsInactive.Include))
                names.Add(root.gameObject.name);
            return names;
        }

        private static List<string> GetAvailableAssemblies()
        {
            var assemblies = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (!name.StartsWith("System") && !name.StartsWith("Unity") && !name.StartsWith("mscorlib") && !name.StartsWith("Mono"))
                    assemblies.Add(name);
            }
            return assemblies;
        }
    }

    // ─── Service Gen Tab ────────────────────────────────────────
    internal class ServiceGenTab : IWizardTab
    {
        public string Title => NexusLang.Get("wizard_subtab_service_gen");
        private string _serviceName = "PlayerDataService";

        public void BuildUI(VisualElement container)
        {
            container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_service_gen_desc")));

            var nameField = new TextField(NexusLang.Get("wizard_field_service_name")) { value = _serviceName };
            nameField.RegisterValueChangedCallback(evt => _serviceName = evt.newValue);
            container.Add(nameField);

            container.Add(new Button(GenerateServiceFiles) { text = NexusLang.Get("wizard_generate_service"), style = { marginTop = 8, backgroundColor = new StyleColor(NexusEditorStyles.BtnBlue), color = Color.white } });
        }

        private void GenerateServiceFiles()
        {
            var path = $"Assets/Scripts/Game/{_serviceName}";
            Directory.CreateDirectory(path);
            File.WriteAllText($"{path}/I{_serviceName}.cs", $@"
using Nexus.Core;
public interface I{_serviceName} : INexusService {{ }}");
            File.WriteAllText($"{path}/{_serviceName}.cs", $@"
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
public class {_serviceName} : NexusService<I{_serviceName}>, I{_serviceName}
{{
    public override ValueTask InitializeAsync(CancellationToken ct) => default;
}}");
            AssetDatabase.Refresh();
            Debug.Log($"[Nexus] Service generated: {_serviceName}");
        }
    }

    // ─── View/Mediator Gen Tab ──────────────────────────────────
    internal class ViewMediatorGenTab : IWizardTab
    {
        public string Title => NexusLang.Get("wizard_subtab_view_gen");
        private string _viewName = "GameplayHUD";
        private bool _createViewGo = true;
        private Label _validationLabel;
        private Button _generateBtn;

        public void BuildUI(VisualElement container)
        {
            container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_view_gen_desc")));

            _validationLabel = new Label { style = { color = new StyleColor(NexusEditorStyles.AccentRed), fontSize = 10, marginBottom = 4, display = DisplayStyle.None } };
            container.Add(_validationLabel);

            var nameField = new TextField(NexusLang.Get("wizard_field_view_name")) { value = _viewName };
            nameField.RegisterValueChangedCallback(evt => { _viewName = evt.newValue; ValidateForm(); });
            container.Add(nameField);

            var createToggle = new Toggle(NexusLang.Get("wizard_create_view_go")) { value = _createViewGo };
            createToggle.RegisterValueChangedCallback(evt => _createViewGo = evt.newValue);
            container.Add(createToggle);

            _generateBtn = new Button(GenerateViewFiles) { text = NexusLang.Get("wizard_generate_view"), style = { marginTop = 8, backgroundColor = new StyleColor(NexusEditorStyles.BtnBlue), color = Color.white } };
            container.Add(_generateBtn);

            ValidateForm();
        }

        private void ValidateForm()
        {
            if (_validationLabel == null) return;
            bool valid = !string.IsNullOrWhiteSpace(_viewName);
            _validationLabel.text = valid ? "" : NexusLang.Get("wizard_validation_view_name");
            _validationLabel.style.display = valid ? DisplayStyle.None : DisplayStyle.Flex;
            if (_generateBtn != null) _generateBtn.SetEnabled(valid);
        }

        private void GenerateViewFiles()
        {
            var path = $"Assets/Scripts/Game/UI";
            Directory.CreateDirectory(path);
            File.WriteAllText($"{path}/{_viewName}.cs", $@"
using Nexus.Core;
using UnityEngine;

[Mediator(typeof({_viewName}Mediator))]
public class {_viewName} : View
{{
    protected override void OnBind(IContext context) {{ }}
    protected override void OnUnbind() {{ }}
}}");
            File.WriteAllText($"{path}/{_viewName}Mediator.cs", $@"
using Nexus.Core;
public class {_viewName}Mediator : Mediator<{_viewName}>
{{
    protected override void OnBind() {{ }}
    protected override void OnUnbind() {{ }}
}}");
            AssetDatabase.Refresh();

            // R2026-H10 fix: the "Create View GameObject" toggle was previously dead UI —
            // the flag was stored but never acted on. When enabled, create the scene
            // GameObject after the compile finishes (the generated View type only exists
            // post-refresh, so creation is deferred via delayCall).
            if (_createViewGo)
            {
                string viewTypeName = _viewName;
                EditorApplication.delayCall += () =>
                {
                    // The freshly generated type lives in Assembly-CSharp (or a game
                    // asmdef) — scan loaded assemblies by simple name via the catalog.
                    Type viewType = null;
                    foreach (var asm in AssemblyCatalog.LoadedAssemblies)
                    {
                        if (asm.IsDynamic) continue;
                        foreach (var t in AssemblyCatalog.GetTypesSafe(asm))
                        {
                            if (t != null && t.Name == viewTypeName && typeof(Nexus.Core.View).IsAssignableFrom(t))
                            {
                                viewType = t;
                                break;
                            }
                        }
                        if (viewType != null) break;
                    }
                    if (viewType == null)
                    {
                        Debug.LogWarning($"[Nexus] View GameObject creation skipped: type '{viewTypeName}' not found after compile.");
                        return;
                    }
                    // R2026: FindAnyObjectByType — the ordering-dependent FindFirstObjectByType
                    // overload is deprecated in Unity 6.
                    if (GameObject.FindAnyObjectByType(viewType) != null)
                    {
                        Debug.Log($"[Nexus] Scene already contains a '{viewTypeName}' instance; skipping GameObject creation.");
                        return;
                    }
                    var go = new GameObject(viewTypeName);
                    go.AddComponent(viewType);
                    Undo.RegisterCreatedObjectUndo(go, $"Create {viewTypeName}");
                    Debug.Log($"[Nexus] View GameObject created in scene: {viewTypeName}");
                };
            }
            Debug.Log($"[Nexus] View/Mediator generated: {_viewName}");
        }
    }

    // ─── Signal/Command Gen Tab ─────────────────────────────────
    internal class SignalCommandGenTab : IWizardTab
    {
        public string Title => NexusLang.Get("wizard_subtab_signal_gen");
        private string _signalName = "PlayerScoreChanged";
        private string _commandName = "UpdateScoreCommand";
        private Label _validationLabel;
        private Button _generateBtn;

        public void BuildUI(VisualElement container)
        {
            container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_signal_gen_desc")));

            _validationLabel = new Label { style = { color = new StyleColor(NexusEditorStyles.AccentRed), fontSize = 10, marginBottom = 4, display = DisplayStyle.None } };
            container.Add(_validationLabel);

            var signalField = new TextField(NexusLang.Get("wizard_field_signal_name")) { value = _signalName };
            signalField.RegisterValueChangedCallback(evt => { _signalName = evt.newValue; ValidateForm(); });
            container.Add(signalField);

            var cmdField = new TextField(NexusLang.Get("wizard_field_command_name")) { value = _commandName };
            cmdField.RegisterValueChangedCallback(evt => { _commandName = evt.newValue; ValidateForm(); });
            container.Add(cmdField);

            _generateBtn = new Button(GenerateSignalCommandFiles) { text = NexusLang.Get("wizard_generate_signal"), style = { marginTop = 8, backgroundColor = new StyleColor(NexusEditorStyles.BtnBlue), color = Color.white } };
            container.Add(_generateBtn);

            ValidateForm();
        }

        private void ValidateForm()
        {
            if (_validationLabel == null) return;
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(_signalName))
                errors.Add(NexusLang.Get("wizard_validation_signal_name"));
            if (string.IsNullOrWhiteSpace(_commandName))
                errors.Add(NexusLang.Get("wizard_validation_command_name"));
            bool valid = errors.Count == 0;
            _validationLabel.text = string.Join("\n", errors);
            _validationLabel.style.display = valid ? DisplayStyle.None : DisplayStyle.Flex;
            if (_generateBtn != null) _generateBtn.SetEnabled(valid);
        }

        private void GenerateSignalCommandFiles()
        {
            var path = $"Assets/Scripts/Game";
            Directory.CreateDirectory(path + "/Signals");
            Directory.CreateDirectory(path + "/Commands");
            File.WriteAllText($"{path}/Signals/{_signalName}.cs", $@"
public readonly struct {_signalName}
{{
    public readonly int Value;
    public {_signalName}(int value) => Value = value;
}}");
            File.WriteAllText($"{path}/Commands/{_commandName}.cs", $@"
using Nexus.Core;
public class {_commandName} : ICommand<{_signalName}>
{{
    public void Execute({_signalName} signal) {{ }}
}}");
            AssetDatabase.Refresh();
            Debug.Log($"[Nexus] Signal/Command generated: {_signalName}, {_commandName}");
        }
    }

    // ─── Clean Deletion Tab ─────────────────────────────────────
    internal class CleanDeletionTab : IWizardTab
    {
        public string Title => NexusLang.Get("wizard_subtab_clean_deletion");
        private string _rootToDeleteName = "";

        public void BuildUI(VisualElement container)
        {
            container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_clean_desc")));

            var rootNames = GetRootNames();
            var dropdown = new DropdownField(NexusLang.Get("wizard_field_root_to_delete"), rootNames, 0);
            dropdown.RegisterValueChangedCallback(evt => _rootToDeleteName = evt.newValue);
            container.Add(dropdown);

            container.Add(new Button(() =>
            {
                var roots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Include);
                foreach (var root in roots)
                {
                    if (root.gameObject.name == _rootToDeleteName)
                    {
                        Object.DestroyImmediate(root.gameObject);
                        Debug.Log($"[Nexus] Deleted root: {_rootToDeleteName}");
                        break;
                    }
                }
            })
            {
                text = NexusLang.Get("wizard_delete_root"),
                style = { marginTop = 8, backgroundColor = new StyleColor(NexusEditorStyles.BtnRed), color = Color.white }
            });
        }

        private static List<string> GetRootNames()
        {
            var names = new List<string> { "Select Root..." };
            foreach (var root in GameObject.FindObjectsByType<Root>(FindObjectsInactive.Include))
                names.Add(root.gameObject.name);
            return names;
        }
    }

    // ─── Shared Helpers (internal for WizardPlugin + tests) ─────
    internal static class NexusSetupWizardHelper
    {
        internal static void CreateRootContext(string contextName, string scopeTag, string scriptsPath, string settingsPath, string parentRootName, string[] selectedAssemblies)
        {
            Debug.Log($"[Nexus Wizard] Creating root context: {contextName} (scope: {scopeTag})");

            var data = ScriptableObject.CreateInstance<ContextData>();
            data.name = $"{contextName}ContextData";
            data.ScopeTag = scopeTag;
            data.EnableAutoDiscovery = true;
            data.AssemblyScopes = selectedAssemblies;
            Directory.CreateDirectory(settingsPath);
            AssetDatabase.CreateAsset(data, $"{settingsPath}/{data.name}.asset");

            var go = new GameObject($"{contextName}Root");
            var root = go.AddComponent<Root>();
            var so = new SerializedObject(root);
            var contextDataProp = so.FindProperty("contextData");
            if (contextDataProp != null)
            {
                contextDataProp.objectReferenceValue = data;
                so.ApplyModifiedProperties();
            }

            if (!string.IsNullOrEmpty(parentRootName) && parentRootName != "None (Root Context)")
            {
                var parentRoots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Include);
                foreach (var pr in parentRoots)
                {
                    if (pr.gameObject.name == parentRootName)
                    {
                        var parentProp = so.FindProperty("parentRoot");
                        if (parentProp != null)
                        {
                            parentProp.objectReferenceValue = pr;
                            so.ApplyModifiedProperties();
                        }
                        break;
                    }
                }
            }

            Undo.RegisterCreatedObjectUndo(go, $"Create {contextName}Root");
            Selection.activeObject = go;
            AssetDatabase.SaveAssets();
            Debug.Log($"[Nexus Wizard] Root context created: {contextName}");
        }
    }
}
