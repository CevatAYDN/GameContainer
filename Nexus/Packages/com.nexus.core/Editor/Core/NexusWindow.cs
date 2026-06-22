using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Host shell for the Nexus Architecture Suite.
    /// Dynamically loads all implementations of <see cref="INexusEditorPlugin"/> and manages tab switching,
    /// branding sidebar, and event-driven status updates.
    /// </summary>
    public partial class NexusWindow : EditorWindow
    {
        private List<INexusEditorPlugin> _plugins = new();
        private INexusEditorPlugin _activePlugin;

        private VisualElement _sidebar;
        private VisualElement _contentArea;
        private Label _statusBar;

        private HierarchyPlugin _hierarchyPlugin; // Keep reference to update trackers in Play Mode

        [MenuItem("Window/Nexus/Dashboard %#n")]
        public static void ShowWindow()
        {
            var window = GetWindow<NexusWindow>("Nexus Dashboard");
            window.minSize = new Vector2(750, 500);
            window.Show();
        }

        private void OnEnable()
        {
            DiscoverPlugins();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            
            NexusRuntime.OnContextRegistered += OnContextEvent;
            NexusRuntime.OnContextUnregistered += OnContextEvent;

            foreach (var plugin in _plugins)
            {
                try { plugin.OnEnable(); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            
            NexusRuntime.OnContextRegistered -= OnContextEvent;
            NexusRuntime.OnContextUnregistered -= OnContextEvent;

            foreach (var plugin in _plugins)
            {
                try { plugin.OnDisable(); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            UpdateStatusBarText();
            RefreshActivePlugin();
        }

        private void OnHierarchyChanged()
        {
            UpdateStatusBarText();
        }

        private void OnContextEvent(IContext context)
        {
            UpdateStatusBarText();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Row;
            root.style.backgroundColor = new StyleColor(NexusEditorStyles.Background);

            // Left Sidebar
            _sidebar = new VisualElement();
            _sidebar.style.width = 180;
            _sidebar.style.borderRightWidth = 1;
            _sidebar.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            _sidebar.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.12f));
            _sidebar.style.paddingTop = 15;
            _sidebar.style.paddingLeft = 8;
            _sidebar.style.paddingRight = 8;

            // Brand Header
            var brandLabel = new Label("NEXUS");
            brandLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            brandLabel.style.fontSize = 20;
            brandLabel.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
            brandLabel.style.marginBottom = 2;
            brandLabel.style.alignSelf = Align.Center;
            _sidebar.Add(brandLabel);

            var subtitleLabel = new Label("Architecture Suite");
            subtitleLabel.style.fontSize = 9;
            subtitleLabel.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
            subtitleLabel.style.marginBottom = 20;
            subtitleLabel.style.alignSelf = Align.Center;
            _sidebar.Add(subtitleLabel);

            // Build dynamic Tab Buttons based on plugins
            foreach (var plugin in _plugins)
            {
                AddTabButton(plugin.DisplayName, plugin.Id);
            }

            root.Add(_sidebar);

            // Main Content Container (Right Pane)
            var rightPanel = new VisualElement();
            rightPanel.style.flexGrow = 1;
            rightPanel.style.flexDirection = FlexDirection.Column;

            _contentArea = new VisualElement();
            _contentArea.style.flexGrow = 1;
            rightPanel.Add(_contentArea);

            // Bottom Status Bar
            _statusBar = NexusEditorStyles.CreateStatusBar();
            rightPanel.Add(_statusBar);

            root.Add(rightPanel);

            // Select default tab
            if (_plugins.Count > 0)
            {
                SwitchToPlugin(_plugins[0].Id);
            }

            UpdateStatusBarText();

            // Scheduler to update Hierarchy trackers when in Play Mode and Hierarchy tab is active
            root.schedule.Execute(OnScheduledUpdate).Every(200);
        }

        private void DiscoverPlugins()
        {
            _plugins.Clear();
            _hierarchyPlugin = null;

            var pluginType = typeof(INexusEditorPlugin);
            var foundPlugins = new List<INexusEditorPlugin>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono") || name.StartsWith("UnityEngine"))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (pluginType.IsAssignableFrom(type) && type.IsClass && !type.IsAbstract)
                        {
                            var plugin = (INexusEditorPlugin)Activator.CreateInstance(type);
                            plugin.Initialize(this);
                            foundPlugins.Add(plugin);

                            if (plugin is HierarchyPlugin hp)
                            {
                                _hierarchyPlugin = hp;
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            // Sort plugins by their predefined order
            _plugins = foundPlugins.OrderBy(p => p.Order).ToList();
        }

        private void AddTabButton(string label, string pluginId)
        {
            var btn = new Button(() => SwitchToPlugin(pluginId)) { text = label };
            btn.name = $"Tab_{pluginId}";
            btn.style.backgroundColor = new StyleColor(Color.clear);
            btn.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
            btn.style.fontSize = 11;
            btn.style.paddingLeft = 12;
            btn.style.paddingRight = 12;
            btn.style.paddingTop = 8;
            btn.style.paddingBottom = 8;
            btn.style.borderTopLeftRadius = 4;
            btn.style.borderTopRightRadius = 4;
            btn.style.borderBottomLeftRadius = 4;
            btn.style.borderBottomRightRadius = 4;
            btn.style.borderTopWidth = 0;
            btn.style.borderBottomWidth = 0;
            btn.style.borderLeftWidth = 0;
            btn.style.borderRightWidth = 0;
            btn.style.marginTop = 3;
            btn.style.marginBottom = 3;
            btn.style.unityFontStyleAndWeight = FontStyle.Normal;
            btn.style.alignItems = Align.FlexStart;

            _sidebar.Add(btn);
        }

        public void SwitchToPlugin(string pluginId)
        {
            var targetPlugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
            if (targetPlugin == null) return;

            _activePlugin = targetPlugin;

            // Highlight active sidebar button
            foreach (var plugin in _plugins)
            {
                var btn = _sidebar.Q<Button>($"Tab_{plugin.Id}");
                if (btn != null)
                {
                    if (plugin.Id == pluginId)
                    {
                        btn.style.backgroundColor = new StyleColor(new Color(0.18f, 0.22f, 0.28f));
                        btn.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
                        btn.style.unityFontStyleAndWeight = FontStyle.Bold;
                    }
                    else
                    {
                        btn.style.backgroundColor = new StyleColor(Color.clear);
                        btn.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
                        btn.style.unityFontStyleAndWeight = FontStyle.Normal;
                    }
                }
            }

            RefreshActivePlugin();
        }

        private void RefreshActivePlugin()
        {
            if (_contentArea == null || _activePlugin == null) return;
            _contentArea.Clear();
            
            try
            {
                _contentArea.Add(_activePlugin.CreateView());
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _contentArea.Add(new Label($"Error loading eklenti view: {ex.Message}") { style = { color = Color.red } });
            }
        }

        private void OnScheduledUpdate()
        {
            if (_activePlugin != null && _activePlugin.Id == "Hierarchy" && Application.isPlaying)
            {
                _hierarchyPlugin?.UpdateVisibleTrackers();
            }
        }

        private void UpdateStatusBarText()
        {
            if (_statusBar == null) return;

            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            _statusBar.text = playing
                ? $"Nexus ● ACTIVE  |  {contextCount} context(s) active  |  {handlerCount} static handler(s) registered"
                : $"Nexus ○ STANDBY  |  {rootCount} Root(s) in scene  |  Enter Play Mode to activate";
        }
    }
}
