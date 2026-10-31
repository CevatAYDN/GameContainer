using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.UIElements;
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

    /// <summary>The generator tabs' shared parts: the scripts folder, the validation line and the result line.</summary>
    internal static class WizardForm
    {
        private const string FolderKey = "Nexus.Wizard.ScriptsFolder";

        /// <summary>A text field bound to the scripts folder every generator writes under.</summary>
        internal static TextField FolderField(VisualElement container)
        {
            // SessionState: a generation recompiles and reloads the domain, which resets static fields.
            var field = new TextField(NexusLang.Get("wizard_field_scripts_folder")) { value = SessionState.GetString(FolderKey, ScriptGenerator.DefaultFolder) };
            field.RegisterValueChangedCallback(evt => SessionState.SetString(FolderKey, evt.newValue));
            container.Add(field);
            return field;
        }

        /// <summary>Null for a folder under Assets, otherwise the message to show.</summary>
        internal static string FolderError(string folder) =>
            folder == "Assets" || (folder != null && folder.StartsWith("Assets/", StringComparison.Ordinal))
                ? null
                : NexusLang.Get("wizard_folder_outside_assets");

        /// <summary>A message line; with a <paramref name="stateKey"/> it shows the last result again after a domain reload.</summary>
        internal static Label MessageLabel(string stateKey = null)
        {
            var label = new Label { userData = stateKey, style = { fontSize = 10, marginTop = 6, marginBottom = 4, whiteSpace = WhiteSpace.Normal, display = DisplayStyle.None } };
            label.selection.isSelectable = true;
            if (stateKey != null)
            {
                string saved = SessionState.GetString(stateKey, "");
                if (saved.Length > 1) Show(label, saved.Substring(1), saved[0] == 'E');
            }
            return label;
        }

        internal static void Show(Label label, string text, bool error)
        {
            label.text = text ?? "";
            label.style.color = new StyleColor(error ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentGreen);
            label.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            if (label.userData is string key)
                SessionState.SetString(key, string.IsNullOrEmpty(text) ? "" : (error ? "E" : "K") + text);
        }

        /// <summary>Shows the first error, or hides the line, and enables the button only when there is none.</summary>
        internal static void Validate(Label validation, Button button, params string[] errors)
        {
            string first = errors.FirstOrDefault(e => e != null);
            Show(validation, first, true);
            button.SetEnabled(first == null);
        }

        /// <summary>Writes the files, or none when one exists, and says what happened and what to do next.</summary>
        internal static void Generate((string Path, string Content)[] files, Label result, string nextStep)
        {
            if (!ScriptGenerator.TryWrite(files, out var existing))
            {
                Show(result, string.Format(NexusLang.Get("wizard_files_exist"), string.Join("\n", existing)), true);
                return;
            }
            string text = string.Format(NexusLang.Get("wizard_files_written"), string.Join("\n", files.Select(f => f.Path))) + "\n\n" + nextStep;
            Show(result, text, false);
            Debug.Log("[Nexus] " + text);
            AssetDatabase.Refresh();
        }
    }

    // ─── Context Tab ────────────────────────────────────────────
    internal class ContextTab : IWizardTab
    {
        private const string SettingsFolderKey = "Nexus.Wizard.SettingsFolder";
        public string Title => NexusLang.Get("wizard_subtab_create_root");
        private string _scopeTag = "Gameplay";

        public void BuildUI(VisualElement container)
        {
            container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_context_desc")));
            var validation = WizardForm.MessageLabel();
            container.Add(validation);

            var tagField = new TextField(NexusLang.Get("wizard_field_scope_tag")) { value = _scopeTag };
            container.Add(tagField);

            var folderField = new TextField(NexusLang.Get("wizard_field_settings_folder")) { value = SessionState.GetString(SettingsFolderKey, "Assets/Settings") };
            container.Add(folderField);

            // By index, not by name: two Roots may share a name.
            var roots = Object.FindObjectsByType<Root>(FindObjectsInactive.Include).OrderBy(r => r.name, StringComparer.Ordinal).ToList();
            var parentChoices = new List<string> { NexusLang.Get("wizard_parent_none") };
            parentChoices.AddRange(roots.Select(r => r.name));
            var parentField = new DropdownField(NexusLang.Get("wizard_field_parent_root"), parentChoices, 0);
            container.Add(parentField);

            // One set backs both the checkboxes and the asset (every box showed checked while the asset got none).
            var assemblies = ContextGenerator.GameAssemblies();
            var scopes = new HashSet<string>(assemblies);
            var scopesBox = NexusEditorStyles.CreateActionGroup(container, NexusLang.Get("wizard_context_scopes"));
            if (assemblies.Count == 0) scopesBox.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_context_no_assemblies")));
            foreach (var asm in assemblies)
            {
                var toggle = new Toggle(asm) { value = true };
                toggle.RegisterValueChangedCallback(evt => { if (evt.newValue) scopes.Add(asm); else scopes.Remove(asm); });
                scopesBox.Add(toggle);
            }

            var result = WizardForm.MessageLabel();
            var create = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_create_root_caps"), () =>
            {
                var parent = parentField.index > 0 ? roots[parentField.index - 1] : null;
                var root = ContextGenerator.Create(_scopeTag, folderField.value, parent, assemblies.Where(scopes.Contains).ToArray(), out var error);
                WizardForm.Show(result, root != null ? string.Format(NexusLang.Get("wizard_context_created"), root.name, AssetDatabase.GetAssetPath(root.ContextData)) : error, root == null);
            }, NexusEditorStyles.BtnBlue);
            create.style.marginTop = 8;
            container.Add(create);
            container.Add(result);

            void Validate() => WizardForm.Validate(validation, create, ScriptGenerator.TypeNameError(_scopeTag), WizardForm.FolderError(folderField.value));
            tagField.RegisterValueChangedCallback(evt => { _scopeTag = evt.newValue; WizardForm.Show(result, null, false); Validate(); });
            folderField.RegisterValueChangedCallback(evt => { SessionState.SetString(SettingsFolderKey, evt.newValue); Validate(); });
            Validate();
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
            var validation = WizardForm.MessageLabel();
            container.Add(validation);
            var folder = WizardForm.FolderField(container);
            var nameField = new TextField(NexusLang.Get("wizard_field_service_name")) { value = _serviceName };
            container.Add(nameField);

            var result = WizardForm.MessageLabel("Nexus.Wizard.Result.Service");
            var generate = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_generate_service"), () =>
                WizardForm.Generate(ScriptGenerator.Service(folder.value, _serviceName), result,
                    string.Format(NexusLang.Get("wizard_next_bind"), ScriptGenerator.ServiceBinding(_serviceName))), NexusEditorStyles.BtnBlue);
            generate.style.marginTop = 8;
            container.Add(generate);
            container.Add(result);

            void Validate() => WizardForm.Validate(validation, generate, WizardForm.FolderError(folder.value), ScriptGenerator.TypeNameError(_serviceName));
            nameField.RegisterValueChangedCallback(evt => { _serviceName = evt.newValue; WizardForm.Show(result, null, false); Validate(); });
            folder.RegisterValueChangedCallback(_ => Validate());
            Validate();
        }
    }

    // ─── View/Mediator Gen Tab ──────────────────────────────────
    internal class ViewMediatorGenTab : IWizardTab
    {
        public string Title => NexusLang.Get("wizard_subtab_view_gen");
        private string _viewName = "GameHUD";

        public void BuildUI(VisualElement container)
        {
            container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_view_gen_desc")));
            var validation = WizardForm.MessageLabel();
            container.Add(validation);
            var folder = WizardForm.FolderField(container);
            var nameField = new TextField(NexusLang.Get("wizard_field_view_name")) { value = _viewName };
            container.Add(nameField);

            var result = WizardForm.MessageLabel("Nexus.Wizard.Result.View");
            var generate = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_generate_view"), () =>
                WizardForm.Generate(ScriptGenerator.ViewMediator(folder.value, _viewName), result,
                    string.Format(NexusLang.Get("wizard_next_view"), _viewName)), NexusEditorStyles.BtnBlue);
            generate.style.marginTop = 8;
            container.Add(generate);
            container.Add(result);

            void Validate() => WizardForm.Validate(validation, generate, WizardForm.FolderError(folder.value), ScriptGenerator.TypeNameError(_viewName));
            nameField.RegisterValueChangedCallback(evt => { _viewName = evt.newValue; WizardForm.Show(result, null, false); Validate(); });
            folder.RegisterValueChangedCallback(_ => Validate());
            Validate();
        }
    }

    // ─── Signal/Command Gen Tab ─────────────────────────────────
    internal class SignalCommandGenTab : IWizardTab
    {
        public string Title => NexusLang.Get("wizard_subtab_signal_gen");
        private string _signalName = "PlayerScoreChanged";
        private string _commandName = "UpdateScoreCommand";

        public void BuildUI(VisualElement container)
        {
            container.Add(NexusEditorStyles.CreateHint(NexusLang.Get("wizard_signal_gen_desc")));
            var validation = WizardForm.MessageLabel();
            container.Add(validation);
            var folder = WizardForm.FolderField(container);
            var signalField = new TextField(NexusLang.Get("wizard_field_signal_name")) { value = _signalName };
            container.Add(signalField);
            var commandField = new TextField(NexusLang.Get("wizard_field_command_name")) { value = _commandName };
            container.Add(commandField);

            var result = WizardForm.MessageLabel("Nexus.Wizard.Result.Signal");
            var generate = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_generate_signal"), () =>
                WizardForm.Generate(ScriptGenerator.SignalCommand(folder.value, _signalName, _commandName), result,
                    string.Format(NexusLang.Get("wizard_next_bind"), ScriptGenerator.CommandBinding(_signalName, _commandName))), NexusEditorStyles.BtnBlue);
            generate.style.marginTop = 8;
            container.Add(generate);
            container.Add(result);

            void Validate() => WizardForm.Validate(validation, generate, WizardForm.FolderError(folder.value),
                ScriptGenerator.TypeNameError(_signalName), ScriptGenerator.TypeNameError(_commandName),
                _signalName == _commandName ? NexusLang.Get("wizard_validation_same_name") : null);
            signalField.RegisterValueChangedCallback(evt => { _signalName = evt.newValue; WizardForm.Show(result, null, false); Validate(); });
            commandField.RegisterValueChangedCallback(evt => { _commandName = evt.newValue; WizardForm.Show(result, null, false); Validate(); });
            folder.RegisterValueChangedCallback(_ => Validate());
            Validate();
        }
    }

    /// <summary>Creates a ContextData asset and a scene Root that starts with it.</summary>
    internal static class ContextGenerator
    {
        /// <summary>The project's own assemblies: player assemblies with a source file under Assets.</summary>
        internal static List<string> GameAssemblies() =>
            CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Where(a => a.sourceFiles.Any(f => f.StartsWith("Assets/", StringComparison.Ordinal)))
                .Select(a => a.name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Writes <c>&lt;settingsFolder&gt;/&lt;scopeTag&gt;ContextData.asset</c> and creates <c>[&lt;scopeTag&gt;_Root]</c> with it;
        /// creates nothing when the asset exists, and returns null with <paramref name="error"/> set.
        /// </summary>
        internal static Root Create(string scopeTag, string settingsFolder, Root parent, string[] assemblyScopes, out string error)
        {
            error = ScriptGenerator.TypeNameError(scopeTag) ?? WizardForm.FolderError(settingsFolder);
            if (error != null) return null;
            string assetPath = $"{settingsFolder.TrimEnd('/')}/{scopeTag}ContextData.asset";
            if (File.Exists(assetPath))
            {
                error = string.Format(NexusLang.Get("wizard_files_exist"), assetPath);
                return null;
            }
            if (!AssetDatabase.IsValidFolder(settingsFolder))
            {
                Directory.CreateDirectory(settingsFolder);
                AssetDatabase.Refresh();
            }

            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = scopeTag;
            data.EnableAutoDiscovery = true;
            data.AssemblyScopes = assemblyScopes;
            AssetDatabase.CreateAsset(data, assetPath);
            AssetDatabase.SaveAssets();
            return NexusHierarchyMenus.CreateSceneRootWithAsset(data, parent);
        }
    }
}
