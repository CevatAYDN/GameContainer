using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    /// <summary>
    /// Editor window wrapper that hosts <see cref="TypeAnalyzerPlugin"/>.
    /// Opened via Window/Nexus/Type Analyzer.
    /// </summary>
    public class TypeDependencyAnalyzerWindow : EditorWindow
    {
        private TypeAnalyzerPlugin _plugin;

        [MenuItem("Window/Nexus/Type Analyzer")]
        public static void ShowWindow()
        {
            var window = GetWindow<TypeDependencyAnalyzerWindow>("Nexus Type Analyzer");
            window.minSize = new Vector2(400, 450);
            window.Show();
        }

        private void CreateGUI()
        {
            _plugin?.OnDisable();
            _plugin = new TypeAnalyzerPlugin();
            _plugin.Initialize(null); // No main window shell required for standalone mode
            rootVisualElement.Clear();
            rootVisualElement.Add(_plugin.CreateView());
        }

        private void OnDisable()
        {
            _plugin?.OnDisable();
            _plugin = null;
        }
    }
}
