using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(ContextData))]
    public class ContextDataEditor : UnityEditor.Editor
    {
        private SerializedProperty _scopeTagProp;
        private SerializedProperty _enableAutoDiscoveryProp;
        private SerializedProperty _assemblyScopesProp;

        private void OnEnable()
        {
            EnsureProperties();
        }

        private bool EnsureProperties()
        {
            if (target == null) return false;
            try
            {
                _scopeTagProp ??= serializedObject.FindProperty("scopeTag");
                _enableAutoDiscoveryProp ??= serializedObject.FindProperty("enableAutoDiscovery");
                _assemblyScopesProp ??= serializedObject.FindProperty("assemblyScopes");
                return _scopeTagProp != null;
            }
            catch
            {
                return false;
            }
        }

        public override void OnInspectorGUI()
        {
            if (target == null || !EnsureProperties()) return;

            try
            {
                serializedObject.Update();
            }
            catch
            {
                return;
            }
            var data = (ContextData)target;

            string tag = string.IsNullOrEmpty(data.ScopeTag) ? "No Scope Tag" : data.ScopeTag;
            StatusType tagType = string.IsNullOrEmpty(data.ScopeTag) ? StatusType.Warning : StatusType.Success;
            NexusInspectorGUI.DrawHeader("Context Data Asset", "Nexus Scope Configuration", tag, tagType);

            // 1. Scope Settings Card
            NexusInspectorGUI.BeginCard("Scope Settings");
            EditorGUILayout.PropertyField(_scopeTagProp, new GUIContent("Scope Tag", "Unique identifier for this context (e.g. Game, UI, Meta)."));

            if (string.IsNullOrEmpty(data.ScopeTag))
            {
                NexusInspectorGUI.DrawMessage("Scope Tag is empty. Giving this context a clear name (e.g., 'Game', 'Battle', 'Lobby') enables convention-based lifecycle discovery and scoped resolution.", StatusType.Warning);
            }

            EditorGUILayout.PropertyField(_enableAutoDiscoveryProp, new GUIContent("Enable Auto-Discovery", "Automatically scans assemblies for {ScopeTag}Lifecycle and [RegisterCommand] attributes."));
            if (data.EnableAutoDiscovery)
            {
                NexusInspectorGUI.DrawMessage($"Auto-Discovery ON: Scans for '{tag}Lifecycle' classes and commands registered to this scope automatically.", StatusType.Info);
            }
            NexusInspectorGUI.EndCard();

            // 2. Assembly Scopes Card
            NexusInspectorGUI.BeginCard("Assembly Scanning");
            EditorGUILayout.PropertyField(_assemblyScopesProp, new GUIContent("Assembly Scopes", "Optional assembly filters. If empty, scans default game assemblies."));

            if (data.AssemblyScopes == null || data.AssemblyScopes.Length == 0)
            {
                NexusInspectorGUI.DrawMessage("Scanning default game assemblies. For larger modular codebases, specify explicit assembly names for faster startup.", StatusType.Info);
            }
            NexusInspectorGUI.EndCard();

            // 3. Quick Actions
            NexusInspectorGUI.BeginCard("Quick Actions");
            if (NexusInspectorGUI.DrawActionButton("✨ Create Scene Root with this Asset", StatusType.Success, 26))
            {
                CreateSceneRootWithAsset(data);
            }

            if (NexusInspectorGUI.DrawActionButton("🔍 Open Nexus Dashboard", StatusType.Info, 22))
            {
                EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n");
            }
            NexusInspectorGUI.EndCard();

            serializedObject.ApplyModifiedProperties();
        }

        private static void CreateSceneRootWithAsset(ContextData data)
        {
            string name = string.IsNullOrEmpty(data.ScopeTag) ? "NexusRoot" : $"[{data.ScopeTag}_Root]";
            var go = new GameObject(name);
            var root = go.AddComponent<Root>();

            var serializedRoot = new SerializedObject(root);
            serializedRoot.FindProperty("contextData").objectReferenceValue = data;
            serializedRoot.ApplyModifiedProperties();

            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            Selection.activeGameObject = go;
        }
    }
}
