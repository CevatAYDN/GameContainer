using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nexus.Editor
{
    public class NexusEditorSettings : ScriptableObject
    {
        private const string SettingsPath = "Assets/NexusEditorSettings.asset";

        [SerializeField]
        private string binderOutputPath = "Assets/Scripts/Nexus";

        [SerializeField]
        private string linkXmlOutputPath = "Assets/Scripts/Nexus";

        public string BinderOutputPath
        {
            get => binderOutputPath;
            set { binderOutputPath = value; Save(); }
        }

        public string LinkXmlOutputPath
        {
            get => linkXmlOutputPath;
            set { linkXmlOutputPath = value; Save(); }
        }

        private static bool EnsureAssetFolderExists(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return true;

            var normalizedPath = assetPath.Replace('\\', '/');
            var parent = Path.GetDirectoryName(normalizedPath).Replace('\\', '/');

            if (string.IsNullOrEmpty(parent) || parent == normalizedPath)
            {
                // Root-level folder (e.g., "Assets") — fall back to filesystem.
                // AssetDatabase will pick it up on the next import cycle.
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var fullPath = Path.Combine(projectRoot, normalizedPath);
                Directory.CreateDirectory(fullPath);
                return false;
            }

            if (!EnsureAssetFolderExists(parent))
                return false;

            var folderName = Path.GetFileName(normalizedPath);
            var guid = AssetDatabase.CreateFolder(parent, folderName);
            return !string.IsNullOrEmpty(guid);
        }

        public static NexusEditorSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<NexusEditorSettings>(SettingsPath);
            if (settings == null)
            {
                var folder = Path.GetDirectoryName(SettingsPath).Replace('\\', '/');
                if (!EnsureAssetFolderExists(folder))
                {
                    Debug.LogWarning($"[Nexus] Cannot create settings folder '{folder}'. Settings will use defaults.");
                    return CreateInstance<NexusEditorSettings>();
                }

                settings = CreateInstance<NexusEditorSettings>();
                AssetDatabase.CreateAsset(settings, SettingsPath);
                AssetDatabase.SaveAssets();
            }
            return settings;
        }

        private void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Nexus/Editor Settings")]
        public static void OpenSettings()
        {
            var settings = GetOrCreateSettings();
            Selection.activeObject = settings;
        }
    }
}
