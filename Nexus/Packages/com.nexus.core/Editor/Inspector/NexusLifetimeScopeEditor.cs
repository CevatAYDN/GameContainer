using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(NexusLifetimeScope))]
    public class NexusLifetimeScopeEditor : UnityEditor.Editor
    {
        private SerializedProperty _parentScopeProp;
        private SerializedProperty _contextDataProp;
        private SerializedProperty _autoBindGlobalProp;

        private void OnEnable()
        {
            EnsureProperties();
        }

        private bool EnsureProperties()
        {
            if (target == null) return false;
            try
            {
                _parentScopeProp ??= serializedObject.FindProperty("_parentScope");
                _contextDataProp ??= serializedObject.FindProperty("contextData");
                _autoBindGlobalProp ??= serializedObject.FindProperty("autoBindGlobalParent");
                return _contextDataProp != null;
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
            var scope = (NexusLifetimeScope)target;

            string scopeTag = scope.ContextData != null ? scope.ContextData.ScopeTag : "Unassigned";
            NexusInspectorGUI.DrawHeader("Nexus Lifetime Scope", "Hierarchical DI Lifetime Scope", scopeTag, StatusType.Info);

            // 1. Hierarchy Configuration Card
            NexusInspectorGUI.BeginCard("Hierarchy Scoping", "Configured", StatusType.Success);
            EditorGUILayout.PropertyField(_parentScopeProp, new GUIContent("Parent Scope / Root", "Explicit parent scope. If unassigned, automatically walks up the GameObject transform hierarchy."));

            if (_autoBindGlobalProp != null)
            {
                EditorGUILayout.PropertyField(_autoBindGlobalProp, new GUIContent("Auto-Bind Global Root", "If no parent is found in scene hierarchy, fallback to cross-scene NexusRuntime.GlobalRoot."));
            }

            if (scope.ParentScope == null)
            {
                NexusInspectorGUI.DrawMessage("Parent scope is not explicitly set. Nexus will auto-discover the nearest parent Root in the GameObject hierarchy or fall back to the Global Project Root.", StatusType.Info);
            }
            NexusInspectorGUI.EndCard();

            // 2. Context Data Configuration Card
            NexusInspectorGUI.BeginCard("Context Configuration");
            EditorGUILayout.PropertyField(_contextDataProp, new GUIContent("Context Data Asset"));

            if (scope.ContextData == null)
            {
                NexusInspectorGUI.DrawMessage("No ContextData asset assigned. A ContextData asset is recommended for defining scope tags and discovery settings.", StatusType.Warning);

                if (NexusInspectorGUI.DrawActionButton("✨ Create & Assign ContextData", StatusType.Success, 26))
                {
                    CreateAndAssignContextData(scope);
                }
            }
            NexusInspectorGUI.EndCard();

            NexusInspectorGUI.BeginCard("Additional Settings");
            DrawPropertiesExcluding(serializedObject, "m_Script", "_parentScope", "contextData", "autoBindGlobalParent");
            NexusInspectorGUI.EndCard();

            // 3. Runtime Diagnostics (PlayMode)
            if (Application.isPlaying && scope.Context != null)
            {
                NexusInspectorGUI.BeginCard("Live Diagnostics", "Active", StatusType.Success);
                NexusInspectorGUI.DrawKeyValue("Active Singletons", scope.Context.Container.ActiveSingletonsCount.ToString());
                NexusInspectorGUI.DrawKeyValue("Command Handlers", scope.Context.SignalBusInternal.CommandHandlers.Count.ToString());
                NexusInspectorGUI.DrawStatusRow("Parent Scope Linked", scope.Context.Parent != null ? "Yes" : "No", scope.Context.Parent != null ? StatusType.Success : StatusType.Warning);
                NexusInspectorGUI.EndCard();
            }

            // 4. Actions
            NexusInspectorGUI.BeginCard("Quick Actions");
            if (NexusInspectorGUI.DrawActionButton("🔍 Open in Dashboard", StatusType.Info, 24))
            {
                EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n");
            }
            NexusInspectorGUI.EndCard();

            serializedObject.ApplyModifiedProperties();
        }

        private void CreateAndAssignContextData(NexusLifetimeScope scope)
        {
            var asset = ScriptableObject.CreateInstance<ContextData>();
            asset.ScopeTag = scope.gameObject.name.Replace(" ", "_");
            asset.EnableAutoDiscovery = true;

            string folder = "Assets/Data";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "Data");
            }
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{asset.ScopeTag}_ContextData.asset");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            _contextDataProp.objectReferenceValue = asset;
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(scope);
        }
    }
}
