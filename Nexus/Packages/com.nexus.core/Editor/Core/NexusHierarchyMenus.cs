using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Nexus.Core;
using Nexus.Core.Components;

namespace Nexus.Editor
{
    /// <summary>
    /// Developer-friendly hierarchy context menus and quick-creation shortcuts for Nexus objects.
    /// Accessible via Hierarchy Right-Click -> Nexus or GameObject -> Nexus.
    /// </summary>
    public static class NexusHierarchyMenus
    {
        [MenuItem("GameObject/Nexus/Scene Root", false, 10)]
        public static void CreateSceneRoot(MenuCommand menuCommand)
        {
            CreateRoot<Root>("[Scene_Root]", menuCommand?.context as GameObject, null, false, "Create Nexus Scene Root");
        }

        [MenuItem("GameObject/Nexus/Global Project Root (DontDestroyOnLoad)", false, 11)]
        public static void CreateGlobalProjectRoot(MenuCommand menuCommand)
        {
            CreateRoot<Root>("[Global_Project_Root]", null, null, true, "Create Global Project Root");
        }

        [MenuItem("GameObject/Nexus/Child Lifetime Scope", false, 12)]
        public static void CreateLifetimeScope(MenuCommand menuCommand)
        {
            CreateRoot<NexusLifetimeScope>("[Lifetime_Scope]", menuCommand?.context as GameObject, null, false, "Create Nexus Lifetime Scope");
        }

        [MenuItem("GameObject/Nexus/Entity with Nexus Binding", false, 13)]
        public static void CreateEntityWithBinding(MenuCommand menuCommand)
        {
            var go = new GameObject("NexusEntity");
            go.SetActive(false);
            GameObjectUtility.SetParentAndAlign(go, menuCommand?.context as GameObject);
            go.AddComponent<NexusBinding>();
            go.SetActive(true);
            Undo.RegisterCreatedObjectUndo(go, "Create Entity with Nexus Binding");
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/Nexus/UI Canvas Root", false, 20)]
        public static void CreateUICanvasRootObject(MenuCommand menuCommand)
        {
            var canvasGo = new GameObject("[Nexus_UICanvas]");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            GameObjectUtility.SetParentAndAlign(canvasGo, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create UI Canvas Root");
            Selection.activeObject = canvasGo;
        }

        // ─── Top-Level Nexus Menu Shortcuts ───

        [MenuItem("Nexus/Open Dashboard", false, 0)]
        public static void OpenDashboard()
        {
            EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n");
        }

        [MenuItem("Nexus/Create/Context Data Asset", false, 10)]
        public static void CreateContextDataAsset()
        {
            var asset = ScriptableObject.CreateInstance<ContextData>();
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/NewContextData.asset");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
        }

        [MenuItem("Nexus/Create/Scene Root in Active Scene", false, 11)]
        public static void CreateSceneRootInActiveScene()
        {
            CreateSceneRoot(null);
        }

        [MenuItem("Nexus/Create/Global Project Root in Active Scene", false, 12)]
        public static void CreateGlobalRootInActiveScene()
        {
            CreateGlobalProjectRoot(null);
        }

        internal static Root CreateSceneRootWithAsset(ContextData data)
        {
            string name = string.IsNullOrEmpty(data.ScopeTag) ? "NexusRoot" : $"[{data.ScopeTag}_Root]";
            return CreateRoot<Root>(name, null, data, false, $"Create {name}");
        }

        private static T CreateRoot<T>(string name, GameObject parent, ContextData data, bool isGlobal, string undoName) where T : Root
        {
            // Parent and settings must be in place before activation invokes Root/Scope.Awake.
            var go = new GameObject(name);
            go.SetActive(false);
            if (!isGlobal) GameObjectUtility.SetParentAndAlign(go, parent);
            var root = go.AddComponent<T>();
            root.SetUp(data);
            var serializedRoot = new SerializedObject(root);
            serializedRoot.FindProperty("isGlobalContext").boolValue = isGlobal;
            serializedRoot.ApplyModifiedPropertiesWithoutUndo();
            go.SetActive(true);
            Undo.RegisterCreatedObjectUndo(go, undoName);
            Selection.activeObject = go;
            return root;
        }
    }
}
