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

        public static NexusEditorSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<NexusEditorSettings>(SettingsPath);
            if (settings == null)
            {
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
