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
        // Distinct colors for each plugin's sidebar icon (used as colored circles).
        private static readonly Dictionary<string, Color> PluginIconColors = new()
        {
            { "Dashboard", new Color(0.3f, 0.8f, 1f) },
            { "Wizard", new Color(1f, 0.85f, 0.3f) },
            { "Hierarchy", new Color(0.4f, 1f, 0.4f) },
            { "Explorer", new Color(0.8f, 0.6f, 0.9f) },
            { "Tracer", new Color(1f, 0.7f, 0.2f) },
            { "Graph", new Color(0.9f, 0.4f, 0.4f) },
            { "TypeAnalyzer", new Color(0.6f, 0.6f, 0.6f) },
            { "GameManager", new Color(0.3f, 1f, 0.8f) },
            { "casual_services", new Color(1f, 0.4f, 0.8f) },
            { "Help", new Color(0.6f, 0.6f, 1f) },
            { "ErrorDashboard", new Color(1f, 0.3f, 0.3f) },
            { "PerformanceDashboard", new Color(0.3f, 1f, 0.6f) },
            { "NetworkDashboard", new Color(0.4f, 0.8f, 1f) },
            { "ContextInspector", new Color(0.6f, 1f, 0.9f) },
        };

        private static readonly List<(string CategoryKey, string[] PluginIds)> SidebarCategories = new()
        {
            ("cat_overview",     new[] { "Dashboard", "GameManager" }),
            ("cat_architecture", new[] { "Hierarchy", "Explorer", "Graph", "TypeAnalyzer", "ContextInspector", "FSM" }),
            ("cat_diagnostics",  new[] { "Tracer", "ErrorDashboard", "PerformanceDashboard", "NetworkDashboard" }),
            ("cat_tools",        new[] { "Wizard", "casual_services", "Help" }),
        };

        private static readonly HashSet<string> HiddenPluginIds = new();

        private List<INexusEditorPlugin> _plugins = new();
        private INexusEditorPlugin _activePlugin;
        private bool _discoveryFailed;
        private string _discoveryError;
        private VisualElement _sidebar;
        private VisualElement _contextActionBar;
        private VisualElement _contentArea;
        private Label _statusBar;
        private readonly Dictionary<string, Label> _tabLabels = new();

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

            if (_plugins.Count > 0)
            {
                _activePlugin ??= _plugins[0];
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

        private void RefreshDiscovery()
        {
            DiscoverPlugins();
            CreateGUI();
            Repaint();
        }

        public void OpenPlugin(string pluginId)
        {
            SwitchToPlugin(pluginId);
            Repaint();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            _tabLabels.Clear();

            // Load USS theme
            NexusEditorStyles.LoadTheme(root);

            root.AddToClassList("nexus-root");

            // Left Sidebar
            _sidebar = new VisualElement();
            _sidebar.AddToClassList(NexusEditorStyles.ClassSidebar);

            // Brand Header
            var brandLabel = new Label(NexusLang.Get("brand_title"));
            brandLabel.AddToClassList(NexusEditorStyles.ClassBrandTitle);
            _sidebar.Add(brandLabel);

            var subtitleLabel = new Label("Architecture Suite");
            subtitleLabel.AddToClassList(NexusEditorStyles.ClassBrandSubtitle);
            _sidebar.Add(subtitleLabel);

            // Separator
            var sep = new VisualElement();
            sep.AddToClassList(NexusEditorStyles.ClassSidebarSep);
            _sidebar.Add(sep);

            // Dynamic Tab Buttons grouped into categories
            var rendered = new HashSet<string>();
            foreach (var (categoryKey, pluginIds) in SidebarCategories)
            {
                var members = pluginIds
                    .Select(id => _plugins.FirstOrDefault(p => p.Id == id))
                    .Where(p => p != null)
                    .ToList();
                if (members.Count == 0) continue;

                AddCategoryHeader(NexusLang.Get(categoryKey));
                foreach (var plugin in members)
                {
                    string key = $"tab_{plugin.Id.ToLower()}";
                    string label = NexusLang.Get(key);
                    if (label == key) label = plugin.DisplayName;
                    AddTabButton(label, plugin.Id);
                    rendered.Add(plugin.Id);
                }
            }

            var uncategorized = _plugins.Where(p => !rendered.Contains(p.Id)).ToList();
            if (uncategorized.Count > 0)
            {
                AddCategoryHeader(NexusLang.Get("cat_other"));
                foreach (var plugin in uncategorized)
                {
                    string key = $"tab_{plugin.Id.ToLower()}";
                    string label = NexusLang.Get(key);
                    if (label == key) label = plugin.DisplayName;
                    AddTabButton(label, plugin.Id);
                }
            }

            if (_discoveryFailed)
            {
                var errLabel = new Label(string.Format(NexusLang.Get("sidebar_discovery_failed"), _discoveryError));
                errLabel.AddToClassList("nexus-discovery-error");
                _sidebar.Add(errLabel);
            }
            else if (!_plugins.Any())
            {
                var infoLabel = new Label(NexusLang.Get("sidebar_no_plugins"));
                infoLabel.AddToClassList("nexus-discovery-info");
                _sidebar.Add(infoLabel);
            }

            // Version at bottom of sidebar
            var spacer = new VisualElement { style = { flexGrow = 1 } };
            _sidebar.Add(spacer);

            // Language selector in sidebar
            var langContainer = new VisualElement();
            langContainer.AddToClassList("nexus-lang-container");

            var enBtn = new Button(() => SetLocale("en")) { text = "EN" };
            enBtn.AddToClassList("nexus-lang-btn");
            enBtn.AddToClassList("first");

            var trBtn = new Button(() => SetLocale("tr")) { text = "TR" };
            trBtn.AddToClassList("nexus-lang-btn");
            trBtn.AddToClassList("last");

            var cur = NexusLang.CurrentLocale;
            if (cur == "tr")
            {
                trBtn.AddToClassList("active");
            }
            else
            {
                enBtn.AddToClassList("active");
            }

            langContainer.Add(enBtn);
            langContainer.Add(trBtn);
            _sidebar.Add(langContainer);

            var versionLabel = new Label("v0.4.0");
            versionLabel.AddToClassList("nexus-version-label");
            _sidebar.Add(versionLabel);

            root.Add(_sidebar);

            // Main Content Container (Right Pane)
            var rightPanel = new VisualElement();
            rightPanel.AddToClassList("nexus-right-panel");

            if (_discoveryFailed)
            {
                var discoveryBox = new HelpBox($"Plugin discovery partially failed:\n{_discoveryError}", HelpBoxMessageType.Warning);
                discoveryBox.name = "discovery-diagnostics";
                rightPanel.Add(discoveryBox);
            }

            _contextActionBar = new VisualElement();
            rightPanel.Add(_contextActionBar);

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

            // Keyboard shortcuts: Ctrl+1..9 for tabs
            root.RegisterCallback<KeyDownEvent>(OnKeyDown);
            root.RegisterCallback<ContextClickEvent>(OnContextClick);

            UpdateStatusBarText();

            // Scheduler to update Hierarchy trackers when in Play Mode and Hierarchy tab is active
            root.schedule.Execute(OnScheduledUpdate).Every(200);
        }

        private void DiscoverPlugins()
        {
            _plugins.Clear();
            _hierarchyPlugin = null;
            _discoveryFailed = false;
            _discoveryError = null;

            try
            {
                var pluginType = typeof(INexusEditorPlugin);
                var foundPlugins = new List<INexusEditorPlugin>();

                foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
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
                                if (HiddenPluginIds.Contains(plugin.Id))
                                    continue;
                                plugin.Initialize(this);
                                foundPlugins.Add(plugin);

                                if (plugin is HierarchyPlugin hp)
                                {
                                    _hierarchyPlugin = hp;
                                }
                            }
                        }
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        _discoveryFailed = true;
                        var failedTypes = ex.LoaderExceptions
                            .Where(e => e != null)
                            .Select(e => e.Message)
                            .Distinct()
                            .ToList();

                        _discoveryError = failedTypes.Count > 0
                            ? string.Join("\n", failedTypes)
                            : ex.Message;

                        foreach (var msg in failedTypes)
                        {
                            Debug.LogWarning($"[Nexus] Plugin discovery type load failed: {msg}");
                        }
                    }
                }

                // Sort plugins by their predefined order
                _plugins = foundPlugins.OrderBy(p => p.Order).ToList();
            }
            catch (Exception ex)
            {
                _discoveryFailed = true;
                _discoveryError = ex.Message;
                _plugins.Clear();
            }
        }

        private void AddCategoryHeader(string label)
        {
            var header = new Label(label.ToUpper());
            header.AddToClassList(NexusEditorStyles.ClassCategoryHeader);
            _sidebar.Add(header);
        }

        private void AddTabButton(string label, string pluginId)
        {
            var btn = new Button(() => SwitchToPlugin(pluginId));
            btn.name = $"Tab_{pluginId}";
            btn.AddToClassList(NexusEditorStyles.ClassSidebarBtn);

            if (PluginIconColors.TryGetValue(pluginId, out var iconColor))
            {
                var icon = NexusEditorStyles.CreateColorIcon(iconColor);
                btn.Add(icon);
            }

            var txtLabel = new Label(label);
            txtLabel.AddToClassList(NexusEditorStyles.ClassSidebarLabel);
            _tabLabels[pluginId] = txtLabel;
            btn.Add(txtLabel);

            _sidebar.Add(btn);
        }

        public void SwitchToPlugin(string pluginId)
        {
            var targetPlugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
            if (targetPlugin == null) return;
            if (targetPlugin == _activePlugin)
            {
                RefreshActivePlugin();
                return;
            }

            if (_activePlugin != null)
            {
                try { _activePlugin.OnDisable(); } catch (Exception ex) { Debug.LogException(ex); }
            }

            _activePlugin = targetPlugin;

            foreach (var plugin in _plugins)
            {
                var btn = _sidebar.Q<Button>($"Tab_{plugin.Id}");
                if (btn != null)
                {
                    if (plugin.Id == pluginId)
                    {
                        btn.AddToClassList("active");
                    }
                    else
                    {
                        btn.RemoveFromClassList("active");
                    }
                }
            }

            RefreshActivePlugin();
        }

        private void RefreshActivePlugin()
        {
            if (_contentArea == null || _activePlugin == null) return;
            UpdateContextActionBar();
            _contentArea.Clear();

            try
            {
                _contentArea.Add(_activePlugin.CreateView());
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _contentArea.Add(new Label(string.Format(NexusLang.Get("error_plugin_view"), ex.Message)) { style = { color = Color.red } });
            }
        }

        private void UpdateContextActionBar()
        {
            if (_contextActionBar == null || _activePlugin == null) return;
            _contextActionBar.Clear();

            var row = new VisualElement();
            row.AddToClassList("nexus-actionbar");

            var tagLabel = new Label(string.Format(NexusLang.Get("actions_label"), _activePlugin.DisplayName.ToUpper()));
            tagLabel.AddToClassList("nexus-actionbar-label");
            row.Add(tagLabel);

            var actions = _activePlugin.GetContextActions();
            if (actions != null)
            {
                foreach (var (label, action, color) in actions)
                {
                    AddContextActionButton(row, label, action, color);
                }
            }

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            row.Add(spacer);

            bool playing = Application.isPlaying;
            var statusPill = NexusEditorStyles.CreatePill(
                playing ? NexusLang.Get("status_play_mode_active") : NexusLang.Get("status_edit_mode"),
                playing ? new Color(0.1f, 0.4f, 0.2f) : new Color(0.3f, 0.3f, 0.3f),
                playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.TextSecondary
            );
            statusPill.AddToClassList("nexus-actionbar-status");
            row.Add(statusPill);

            _contextActionBar.Add(row);
        }

        private void AddContextActionButton(VisualElement parent, string label, System.Action onClick, Color color)
        {
            var btn = new Button(() => onClick()) { text = label };
            btn.AddToClassList("nexus-actionbar-btn");
            btn.style.backgroundColor = new StyleColor(color);
            parent.Add(btn);
        }

        private void OnScheduledUpdate()
        {
            if (_activePlugin != null && _activePlugin.Id == "Hierarchy" && Application.isPlaying)
            {
                _hierarchyPlugin?.UpdateVisibleTrackers();
            }

            // Forward to active plugin for lightweight polling updates
            try { _activePlugin?.OnUpdate(); }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[Nexus] Plugin OnUpdate failed: {ex.Message}"); }

            UpdateStatusBarText();
        }

        private void OnContextClick(ContextClickEvent evt)
        {
            if (_discoveryFailed)
            {
                RefreshDiscovery();
                evt.StopPropagation();
            }
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!evt.ctrlKey) return;

            if (evt.keyCode == KeyCode.F5)
            {
                RefreshDiscovery();
                evt.StopPropagation();
                return;
            }

            int index = -1;
            switch (evt.keyCode)
            {
                case KeyCode.Alpha1: index = 0; break;
                case KeyCode.Alpha2: index = 1; break;
                case KeyCode.Alpha3: index = 2; break;
                case KeyCode.Alpha4: index = 3; break;
                case KeyCode.Alpha5: index = 4; break;
                case KeyCode.Alpha6: index = 5; break;
                case KeyCode.Alpha7: index = 6; break;
                case KeyCode.Alpha8: index = 7; break;
                case KeyCode.Alpha9: index = 8; break;
            }

            if (index >= 0 && index < _plugins.Count)
            {
                SwitchToPlugin(_plugins[index].Id);
                evt.StopPropagation();
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
                ? string.Format(NexusLang.Get("statusbar_play"), contextCount, handlerCount)
                : string.Format(NexusLang.Get("statusbar_standby"), rootCount);
        }

        private void SetLocale(string locale)
        {
            EditorPrefs.SetString("Nexus_Locale", locale);
            NexusLang.LoadLocale(locale);
            CreateGUI();
        }
    }
}
