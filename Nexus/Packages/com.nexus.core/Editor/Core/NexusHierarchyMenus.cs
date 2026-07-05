using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Nexus.Core;

namespace Nexus.Editor.Core
{
    public static class NexusHierarchyMenus
    {
        [MenuItem("GameObject/Nexus/Create Root", false, 10)]
        public static void CreateRootObject(MenuCommand menuCommand)
        {
            var go = new GameObject("NexusRoot");
            go.AddComponent<Root>();
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Nexus Root");
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/Nexus/Create UI Canvas Root", false, 11)]
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

        [MenuItem("Nexus/Create Context Data Asset")]
        public static void CreateContextDataAsset()
        {
            var asset = ScriptableObject.CreateInstance<ContextData>();
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/NewContextData.asset");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
        }
    }
}
