using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(Root))]
    public class RootEditor : UnityEditor.Editor
    {
        private SerializedProperty _contextDataProp;
        private SerializedProperty _parentRootProp;
        private SerializedProperty _isGlobalContextProp;
        private SerializedProperty _autoBindGlobalParentProp;
        private SerializedProperty _initializationPriorityProp;

        private void OnEnable()
        {
            EnsureProperties();
        }

        private bool EnsureProperties()
        {
            if (target == null) return false;
            try
            {
                _contextDataProp ??= serializedObject.FindProperty("contextData");
                _parentRootProp ??= serializedObject.FindProperty("parentRoot");
                _isGlobalContextProp ??= serializedObject.FindProperty("isGlobalContext");
                _autoBindGlobalParentProp ??= serializedObject.FindProperty("autoBindGlobalParent");
                _initializationPriorityProp ??= serializedObject.FindProperty("initializationPriority");
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

            var root = (Root)target;
            var data = root.ContextData;
            bool hasContext = root.Context != null;
            bool hasContextData = data != null;
            bool hasParent = root.ParentRoot != null;
            bool isGlobal = root.IsGlobalContext;

            string badge = isGlobal ? "Global Root" : (hasContext ? "Bound" : "Scene Root");
            StatusType badgeType = isGlobal ? StatusType.Success : (hasContext ? StatusType.Success : StatusType.Info);
            NexusInspectorGUI.DrawHeader("Nexus Root", "Core Scene Context Anchor", badge, badgeType);

            // 1. Health Status Overview
            NexusInspectorGUI.BeginCard("Context Health");
            NexusInspectorGUI.DrawStatusRow("Context Data", hasContextData ? data.name : "Missing", hasContextData ? StatusType.Success : StatusType.Error);
            NexusInspectorGUI.DrawStatusRow("Parent Root", hasParent ? root.ParentRoot.name : (root.AutoBindGlobalParent ? "Auto-Global" : "None"), hasParent ? StatusType.Info : StatusType.Warning);

            if (Application.isPlaying)
            {
                NexusInspectorGUI.DrawStatusRow("Context Status", hasContext ? "Active & Bound" : "Not Bound", hasContext ? StatusType.Success : StatusType.Error);
            }
            NexusInspectorGUI.EndCard();

            // 2. Configuration Card
            NexusInspectorGUI.BeginCard("Configuration");
            EditorGUILayout.PropertyField(_contextDataProp, new GUIContent("Context Data Asset", "ScriptableObject defining scope tags and discovery settings."));

            if (!hasContextData)
            {
                NexusInspectorGUI.DrawMessage("Root requires a ContextData asset to initialize properly. Click below to create one instantly.", StatusType.Warning);
                if (NexusInspectorGUI.DrawActionButton("✨ Create & Assign ContextData", StatusType.Success, 26))
                {
                    CreateAndAssignContextData(root);
                }
            }

            if (_isGlobalContextProp != null)
            {
                EditorGUILayout.PropertyField(_isGlobalContextProp, new GUIContent("Is Global Root", "Marks this Root as a persistent cross-scene DontDestroyOnLoad Project Context."));
            }

            if (_autoBindGlobalParentProp != null && !isGlobal)
            {
                EditorGUILayout.PropertyField(_autoBindGlobalParentProp, new GUIContent("Auto-Bind Global Root", "Automatically attaches to NexusRuntime.GlobalRoot when no parent root exists in this scene."));
            }

            EditorGUILayout.PropertyField(_parentRootProp, new GUIContent("Explicit Parent Root", "Optional parent root in hierarchy."));
            if (_initializationPriorityProp != null)
            {
                EditorGUILayout.PropertyField(_initializationPriorityProp, new GUIContent("Init Priority", "Lower priority values initialize first."));
            }
            NexusInspectorGUI.EndCard();

            // 3. Live Runtime Diagnostics (PlayMode)
            if (Application.isPlaying)
            {
                NexusInspectorGUI.BeginCard("Runtime Diagnostics", "Live", StatusType.Success);
                if (hasContext)
                {
                    NexusInspectorGUI.DrawKeyValue("Scope Tag", root.Context.ScopeTag ?? "Global");
                    NexusInspectorGUI.DrawKeyValue("Bound Services", root.Context.Container.ActiveSingletonsCount.ToString());
                    NexusInspectorGUI.DrawKeyValue("Commands", root.Context.SignalBusInternal.CommandHandlers.Count.ToString());
                    var hasLifecycle = root.Context.Container.IsRegistered(typeof(IContextLifecycle));
                    NexusInspectorGUI.DrawStatusRow("Lifecycle Hook", hasLifecycle ? "Registered" : "None", hasLifecycle ? StatusType.Success : StatusType.Info);
                }
                else
                {
                    NexusInspectorGUI.DrawMessage("Context is not bound yet. Ensure ContextData and parent chain are valid.", StatusType.Error);
                }
                NexusInspectorGUI.EndCard();
            }

            // 4. Quick Actions
            NexusInspectorGUI.BeginCard("Actions");
            if (NexusInspectorGUI.DrawActionButton("🔍 Open Nexus Dashboard", StatusType.Info, 26))
            {
                EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n");
            }
            NexusInspectorGUI.EndCard();

            serializedObject.ApplyModifiedProperties();
        }

        private void CreateAndAssignContextData(Root root)
        {
            var asset = ScriptableObject.CreateInstance<ContextData>();
            asset.ScopeTag = root.gameObject.name.Replace(" ", "_").Replace("[", "").Replace("]", "");
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
            EditorUtility.SetDirty(root);
        }
    }
}
