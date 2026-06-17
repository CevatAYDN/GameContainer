using UnityEditor;
using UnityEngine;
using System.IO;
using Nexus.Core;

namespace Nexus.Editor
{
    public class RootWizard : EditorWindow
    {
        private string _contextName = "Gameplay";
        private string _scopeTag = "Gameplay";
        private Vector2 _scrollPosition;

        [MenuItem("GameObject/Nexus/Create Root", false, 10)]
        [MenuItem("Window/Nexus/Root Wizard")]
        public static void ShowWindow()
        {
            var window = GetWindow<RootWizard>("Nexus Root Wizard");
            window.minSize = new Vector2(400, 350);
            window.Show();
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
            EditorGUI.indentLevel = 0;

            EditorGUILayout.Space(10);

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fixedHeight = 30
            };

            if (GUILayout.Button("Create Root & ContextData", buttonStyle))
            {
                CreateRoot(_contextName, _scopeTag);
            }
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
            // 1. Create Context GameObjects & Assets
            foreach (var name in manifest.DefaultContextNames)
            {
                CreateRoot(name, name);
            }

            // 2. Generate samples if checked
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
            contextData.AssemblyScopes = new string[0]; // Empty scans executing assembly

            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
            {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            string path = $"Assets/Settings/{contextName}ContextData.asset";
            AssetDatabase.CreateAsset(contextData, path);
            AssetDatabase.SaveAssets();

            // 3. Assign ContextData using reflection
            var contextDataField = typeof(Root).GetField("contextData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            contextDataField?.SetValue(root, contextData);

            // 4. Register Undo & Focus
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Nexus Root");

            Debug.Log($"[Nexus] Successfully created {go.name} and registered context data at {path}.");
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
    }
}
