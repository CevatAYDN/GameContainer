using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    /// <summary>
    /// The one form that fires a signal from the editor (#193): its public fields, the context to fire it in, presets,
    /// and the result. It fires with <see cref="ISignalPublisher.FireAsync{T}"/> and waits for the handler chain, so a
    /// signal with asynchronous handlers fires too (<c>Fire</c> throws for one). The Architecture tab and the Context
    /// Inspector both show it.
    /// </summary>
    internal sealed class SignalFireForm
    {
        private static readonly MethodInfo s_fireAsync = typeof(ISignalPublisher).GetMethod(nameof(ISignalPublisher.FireAsync));

        private readonly Type _signalType;
        private readonly IContext _fixedContext;
        private readonly VisualElement _root = new();
        private object _signal;
        private DropdownField _contextField;
        private List<IContext> _contexts;
        private Label _result;

        private SignalFireForm(Type signalType, IContext fixedContext)
        {
            _signalType = signalType;
            _fixedContext = fixedContext;
        }

        /// <summary>The form for <paramref name="signalType"/>, firing in <paramref name="context"/> when one is given,
        /// otherwise in the running context the form's dropdown selects.</summary>
        internal static VisualElement Create(Type signalType, IContext context = null)
        {
            var form = new SignalFireForm(signalType, context);
            form.Build();
            return form._root;
        }

        private void Build()
        {
            _root.Clear();
            if (!Application.isPlaying)
            {
                _root.Add(NexusEditorStyles.CreateHint(NexusLang.Get("fire_needs_play_mode")));
                return;
            }

            try { _signal ??= Activator.CreateInstance(_signalType); }
            catch (Exception ex)
            {
                _root.Add(ErrorLabel(string.Format(NexusLang.Get("fire_cannot_create"), _signalType.Name, ex.Message)));
                return;
            }

            var fields = _signalType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length == 0) _root.Add(NexusEditorStyles.CreateHint(NexusLang.Get("fire_no_fields")));
            foreach (var field in fields)
            {
                var captured = field;
                var element = NexusFieldInspector.CreateField(field.Name, field.FieldType,
                    () => captured.GetValue(_signal), value => captured.SetValue(_signal, value));
                _root.Add(element ?? new Label(string.Format(NexusLang.Get("fire_field_not_editable"), field.Name, field.FieldType.Name)));
            }

            if (_fixedContext == null)
            {
                _contexts = new List<IContext>(NexusRuntime.ActiveContexts);
                if (_contexts.Count == 0)
                {
                    _root.Add(NexusEditorStyles.CreateHint(NexusLang.Get("fire_no_context")));
                    return;
                }
                var labels = new List<string>(_contexts.Count);
                foreach (var context in _contexts) labels.Add(ContextLabel(context));
                _contextField = new DropdownField(NexusLang.Get("fire_context"), labels, 0);
                _root.Add(_contextField);
            }

            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 6 } };
            buttons.Add(NexusEditorStyles.CreateButton(string.Format(NexusLang.Get("fire_button"), _signalType.Name), Fire, NexusEditorStyles.BtnGreen));
            buttons.Add(NexusEditorStyles.CreateButton(NexusLang.Get("fire_refresh_contexts"), Build, NexusEditorStyles.BtnGray));
            _root.Add(buttons);

            _result = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 6, fontSize = 11 } };
            _root.Add(_result);

            AddPresets();
        }

        private static string ContextLabel(IContext context) =>
            string.IsNullOrEmpty(context.ScopeTag) ? NexusLang.Get("fire_untagged_context") : context.ScopeTag;

        private async void Fire()
        {
            var context = _fixedContext ?? (_contextField != null && _contextField.index >= 0 && _contextField.index < _contexts.Count
                ? _contexts[_contextField.index] : null);
            if (context?.SignalBus == null || !NexusRuntime.ActiveContexts.Contains(context))
            {
                Show(NexusLang.Get("fire_context_gone"), NexusEditorStyles.AccentOrange);
                return;
            }

            string scope = ContextLabel(context);
            Show(string.Format(NexusLang.Get("fire_running"), _signalType.Name, scope), NexusEditorStyles.TextSecondary);
            try
            {
                var pending = (ValueTask)s_fireAsync.MakeGenericMethod(_signalType).Invoke(context.SignalBus, new[] { _signal });
                await pending;
                Show(string.Format(NexusLang.Get("fire_done"), _signalType.Name, scope, DateTime.Now.ToString("HH:mm:ss.fff")), NexusEditorStyles.AccentGreen);
            }
            catch (Exception ex)
            {
                // The signal bus has already logged a handler's exception; the form says what happened to this firing.
                var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                Show(string.Format(NexusLang.Get("fire_failed"), _signalType.Name, cause.GetType().Name, cause.Message), NexusEditorStyles.AccentRed);
            }
        }

        private void Show(string text, Color color)
        {
            if (_result == null) return;
            _result.text = text;
            _result.style.color = new StyleColor(color);
        }

        private static Label ErrorLabel(string text) =>
            new(text) { style = { color = new StyleColor(NexusEditorStyles.AccentRed), whiteSpace = WhiteSpace.Normal } };

        // Presets keep the keys the Explorer tab used, so presets saved there still load.
        private void AddPresets()
        {
            _root.Add(NexusEditorStyles.CreateSectionTitle(NexusLang.Get("fire_presets")));

            var saveRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            var nameField = new TextField { style = { flexGrow = 1, marginRight = 4 } };
            nameField.SetValueWithoutNotify(NexusLang.Get("fire_preset_default"));
            saveRow.Add(nameField);
            saveRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("fire_preset_save"), () => SavePreset(nameField.value), NexusEditorStyles.BtnBlue));
            _root.Add(saveRow);

            var names = PresetNames();
            if (names.Count == 0)
            {
                _root.Add(NexusEditorStyles.CreateHint(NexusLang.Get("fire_no_presets")));
                return;
            }
            var loadRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            var choice = new DropdownField(names, 0) { style = { flexGrow = 1, marginRight = 4 } };
            loadRow.Add(choice);
            loadRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("fire_preset_load"), () => LoadPreset(choice.value), NexusEditorStyles.BtnGray));
            _root.Add(loadRow);
        }

        private string PresetListKey => "NexusPresets_" + _signalType.FullName;
        private string PresetKey(string name) => $"NexusPreset_{_signalType.FullName}_{name}";

        private List<string> PresetNames()
        {
            var saved = EditorPrefs.GetString(PresetListKey, string.Empty);
            return new List<string>(saved.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        private void SavePreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains(";")) return;
            EditorPrefs.SetString(PresetKey(name), EditorJsonUtility.ToJson(_signal));
            var names = PresetNames();
            if (!names.Contains(name))
            {
                names.Add(name);
                EditorPrefs.SetString(PresetListKey, string.Join(";", names));
            }
            Build();
        }

        private void LoadPreset(string name)
        {
            var json = EditorPrefs.GetString(PresetKey(name), string.Empty);
            if (string.IsNullOrEmpty(json)) return;
            EditorJsonUtility.FromJsonOverwrite(json, _signal);
            Build();
        }
    }
}
