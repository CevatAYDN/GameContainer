using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    public class RootWizard : EditorWindow
    {
        private string _contextName = "Gameplay";
        private string _scopeTag = "Gameplay";

        [MenuItem("GameObject/Nexus/Create Root", false, 10)]
        [MenuItem("Window/Nexus/Root Wizard")]
        public static void ShowWindow()
        {
            var window = GetWindow<RootWizard>("Nexus Root Wizard");
            window.minSize = new Vector2(350, 180);
            window.Show();
        }

        private void OnGUI()
        {
            // Dark elegant styling for modern look
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(10, 10, 10, 5)
            };

            GUILayout.Label("Create Nexus Root Context", titleStyle);
            EditorGUILayout.Space();

            EditorGUI.indentLevel = 1;
            _contextName = EditorGUILayout.TextField("Context Name", _contextName);
            _scopeTag = EditorGUILayout.TextField("Scope Tag", _scopeTag);
            EditorGUI.indentLevel = 0;

            EditorGUILayout.Space(15);

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fixedHeight = 30
            };

            if (GUILayout.Button("Create Root & ContextData", buttonStyle))
            {
                CreateRoot();
                Close();
            }
        }

        private void CreateRoot()
        {
            // 1. Create GameObject
            var go = new GameObject($"{_contextName}Root");
            var root = go.AddComponent<Root>();

            // 2. Create ContextData asset
            var contextData = ScriptableObject.CreateInstance<ContextData>();
            contextData.ScopeTag = _scopeTag;
            contextData.AssemblyScopes = new string[0]; // Empty means scan active executing assembly

            // Find or create asset directory
            string path = $"Assets/{_contextName}ContextData.asset";
            AssetDatabase.CreateAsset(contextData, path);
            AssetDatabase.SaveAssets();

            // 3. Assign ContextData to Root
            var contextDataField = typeof(Root).GetField("contextData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            contextDataField?.SetValue(root, contextData);

            // 4. Register Undo & Focus
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Nexus Root");
            
            Debug.Log($"[Nexus] Successfully created {go.name} and registered context data at {path}.");
        }
    }
}
