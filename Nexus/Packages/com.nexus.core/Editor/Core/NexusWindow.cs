using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// The one Nexus editor window (menu Nexus, Ctrl+Shift+N). It discovers every <see cref="INexusEditorPlugin"/>,
    /// shows each as a tab in its sidebar section and forwards status updates to the open tab.
    /// </summary>
    public partial class NexusWindow : EditorWindow
    {
        /// <summary>The sidebar sections in their order; a plugin's <see cref="INexusEditorPlugin.Category"/> names one,
        /// and a plugin with any other category is listed after them.</summary>
        internal static readonly string[] Sections = { "cat_status", "cat_setup", "cat_architecture", "cat_live" };

        // Discovered plugin TYPES are cached across window open/close: the assembly-wide
        // scan is the expensive part of discovery (it was re-run on every OnEnable). Fresh
        // instances are still created per open so per-window plugin state stays clean.
        // Invalidated on script reload (the type universe changes).
        private static List<Type> s_discoveredPluginTypes;

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_discoveredPluginTypes = null;
        }

        private List<INexusEditorPlugin> _plugins = new();
        private INexusEditorPlugin _activePlugin;
        private bool _discoveryFailed;
        private string _discoveryError;
        private VisualElement _sidebar;
        private VisualElement _contextActionBar;
        private VisualElement _contentArea;
        private Label _statusBar;
        private readonly Dictionary<string, Label> _tabLabels = new();
        private bool _uiCallbacksRegistered;
        // serialized, so a recompile or a play-mode domain reload reopens the tab that was open
        [SerializeField] private string _activePluginId;

        /// <summary>Opens the window, on the tab with this plugin id when one is given.</summary>
        public static NexusWindow Open(string pluginId = null)
        {
            var window = GetWindow<NexusWindow>();
            window.minSize = new Vector2(750, 500);
            if (pluginId != null) window.SwitchToPlugin(pluginId);
            return window;
        }

        /// <summary>Opens the window on the first tab of a sidebar section (one of <see cref="Sections"/>).</summary>
        public static NexusWindow OpenSection(string category)
        {
            var window = Open();
            var first = window._plugins.FirstOrDefault(p => p.Category == category);
            if (first != null) window.SwitchToPlugin(first.Id);
            return window;
        }

        [MenuItem("Nexus/Open Window %#n", priority = 0)]
        private static void OpenFromMenu() => Open();

        [MenuItem("Nexus/Status", priority = 20)]
        private static void OpenStatus() => OpenSection("cat_status");

        [MenuItem("Nexus/Setup", priority = 21)]
        private static void OpenSetup() => OpenSection("cat_setup");

        [MenuItem("Nexus/Architecture", priority = 22)]
        private static void OpenArchitecture() => OpenSection("cat_architecture");

        [MenuItem("Nexus/Live", priority = 23)]
        private static void OpenLive() => OpenSection("cat_live");

        private void OnEnable()
        {
            titleContent = new GUIContent("Nexus");
            DiscoverPlugins();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;

            NexusRuntime.OnContextRegistered += OnContextEvent;
            NexusRuntime.OnContextUnregistered += OnContextEvent;
        }

        private void OnDisable()
        {
            _uiCallbacksRegistered = false;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;

            NexusRuntime.OnContextRegistered -= OnContextEvent;
            NexusRuntime.OnContextUnregistered -= OnContextEvent;

            DeactivatePlugin();
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

            var subtitleLabel = new Label(NexusLang.Get("brand_subtitle"));
            subtitleLabel.AddToClassList(NexusEditorStyles.ClassBrandSubtitle);
            _sidebar.Add(subtitleLabel);

            // Separator
            var sep = new VisualElement();
            sep.AddToClassList(NexusEditorStyles.ClassSidebarSep);
            _sidebar.Add(sep);

            // _plugins is in sidebar order already (DiscoverPlugins)
            foreach (var group in _plugins.GroupBy(p => p.Category))
            {
                AddCategoryHeader(NexusLang.Get(group.Key));
                foreach (var plugin in group)
                {
                    string key = $"tab_{plugin.Id.ToLower()}";
                    string label = NexusLang.Get(key);
                    if (label == key) label = plugin.DisplayName;
                    AddTabButton(label, plugin);
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

            var versionLabel = new Label("v" + NexusLang.PackageVersion);
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

            // the tab that was open, or the first tab of the first section
            ActivatePlugin(_plugins.FirstOrDefault(p => p.Id == _activePluginId) ?? _plugins.FirstOrDefault());

            // Keyboard shortcuts: Ctrl+1..9 for tabs.
            // Guard against double registration: RefreshDiscovery() and SetLocale() re-run
            // CreateGUI() on the same root, and root.Clear() does not remove callbacks/schedules.
            if (!_uiCallbacksRegistered)
            {
                root.RegisterCallback<KeyDownEvent>(OnKeyDown);
                root.RegisterCallback<ContextClickEvent>(OnContextClick);
                root.schedule.Execute(OnScheduledUpdate).Every(200);
                _uiCallbacksRegistered = true;
            }

            UpdateStatusBarText();
        }

        private void DiscoverPlugins()
        {
            _plugins.Clear();
            _discoveryFailed = false;
            _discoveryError = null;

            try
            {
                var pluginType = typeof(INexusEditorPlugin);

                // Scan once per script-reload; reuse the cached type list on window reopens.
                if (s_discoveredPluginTypes == null)
                {
                    s_discoveredPluginTypes = new List<Type>();
                    foreach (var assembly in AssemblyCatalog.LoadedAssemblies)
                    {
                        foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                        {
                            if (pluginType.IsAssignableFrom(type) && type.IsClass && !type.IsAbstract
                                && type.GetConstructor(Type.EmptyTypes) != null)
                                s_discoveredPluginTypes.Add(type);
                        }
                    }
                }

                // Each plugin loads on its own: one that throws while it is created, initialized or asked for its Category
                // or Order loses its tab and is named in the error, and the others keep theirs. One try around them all
                // cleared every tab and logged the TargetInvocationException's message, which names no plugin (#255).
                var foundPlugins = new List<(INexusEditorPlugin Plugin, string Category, int Order)>();
                foreach (var type in s_discoveredPluginTypes)
                {
                    try
                    {
                        var plugin = (INexusEditorPlugin)Activator.CreateInstance(type);
                        plugin.Initialize(this);
                        foundPlugins.Add((plugin, plugin.Category, plugin.Order));
                    }
                    catch (Exception ex)
                    {
                        var cause = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
                        Debug.LogError($"[NexusWindow] The plugin {type.FullName} failed to load and has no tab: {cause.GetType().Name}: {cause.Message}\n{cause.StackTrace}");
                    }
                }

                // Sidebar order: the sections in their order, then the other categories; by Order inside each.
                // Ctrl+1..9 index this list, so it is grouped exactly as the sidebar shows it.
                _plugins = foundPlugins
                    .OrderBy(p => SectionRank(p.Category))
                    .ThenBy(p => p.Order)
                    .GroupBy(p => p.Category)
                    .SelectMany(g => g)
                    .Select(p => p.Plugin)
                    .ToList();
            }
            catch (Exception ex)
            {
                _discoveryFailed = true;
                _discoveryError = ex.Message;
                Debug.LogError($"[NexusWindow] Plugin discovery failed: {ex.Message}\n{ex.StackTrace}");
                _plugins.Clear();
            }
        }

        private static int SectionRank(string category)
        {
            int index = Array.IndexOf(Sections, category);
            return index < 0 ? Sections.Length : index;
        }

        private void AddCategoryHeader(string label)
        {
            var header = new Label(label.ToUpper());
            header.AddToClassList(NexusEditorStyles.ClassCategoryHeader);
            _sidebar.Add(header);
        }

        private void AddTabButton(string label, INexusEditorPlugin plugin)
        {
            var btn = new Button(() => SwitchToPlugin(plugin.Id));
            btn.name = $"Tab_{plugin.Id}";
            btn.AddToClassList(NexusEditorStyles.ClassSidebarBtn);

            var icon = NexusEditorStyles.CreateColorIcon(plugin.IconColor);
            btn.Add(icon);

            var txtLabel = new Label(label);
            txtLabel.AddToClassList(NexusEditorStyles.ClassSidebarLabel);
            _tabLabels[plugin.Id] = txtLabel;
            btn.Add(txtLabel);

            _sidebar.Add(btn);
        }

        public void SwitchToPlugin(string pluginId)
        {
            var targetPlugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
            if (targetPlugin == null) return;
            if (_contentArea == null)
            {
                // the window has no UI yet (opened this frame): CreateGUI opens this tab
                _activePluginId = targetPlugin.Id;
                return;
            }
            if (targetPlugin == _activePlugin)
            {
                // Same tab re-clicked: keep its view, refresh the action bar and status text.
                UpdateContextActionBar();
                UpdateStatusBarText();
                return;
            }

            ActivatePlugin(targetPlugin);
        }

        internal void RefreshActivePlugin()
        {
            if (_activePlugin != null) ActivatePlugin(_activePlugin);
        }

        // OnEnable and OnDisable bracket every view a plugin builds: a view rebuilt for play mode, a language
        // switch or a rediscovery first disables the old one, so no subscription of the old view stays behind.
        private void ActivatePlugin(INexusEditorPlugin plugin)
        {
            DeactivatePlugin();
            if (plugin == null || _contentArea == null) return;

            _activePlugin = plugin;
            _activePluginId = plugin.Id;
            try { plugin.OnEnable(); } catch (Exception ex) { Debug.LogException(ex); }

            foreach (var btn in _sidebar.Query<Button>(className: NexusEditorStyles.ClassSidebarBtn).ToList())
                btn.EnableInClassList("active", btn.name == $"Tab_{plugin.Id}");

            UpdateContextActionBar();
            _contentArea.Clear();
            try
            {
                _contentArea.Add(plugin.CreateView());
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _contentArea.Add(new Label(string.Format(NexusLang.Get("error_plugin_view"), ex.Message)) { style = { color = Color.red } });
            }
        }

        private void DeactivatePlugin()
        {
            if (_activePlugin == null) return;
            try { _activePlugin.OnDisable(); } catch (Exception ex) { Debug.LogException(ex); }
            _activePlugin = null;
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

            // This runs from the 200ms scheduled tick AND from play-mode/hierarchy/context
            // callbacks. A throwing data-provider call (e.g. during scene teardown or domain
            // reload) must not spam the console every 200ms and leave the window stuck.
            try
            {
                bool playing = Application.isPlaying;
                int contextCount = NexusEditorDataProvider.GetActiveContextCount();
                int handlerCount = NexusEditorDataProvider.GetHandlerCount();
                var roots = NexusEditorDataProvider.GetSceneRoots();
                int rootCount = roots?.Length ?? 0;

                _statusBar.text = playing
                    ? string.Format(NexusLang.Get("statusbar_play"), contextCount, handlerCount)
                    : string.Format(NexusLang.Get("statusbar_standby"), rootCount);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nexus] Status bar update failed: {ex.Message}");
            }
        }

        private void SetLocale(string locale)
        {
            EditorPrefs.SetString("Nexus_Locale", locale);
            NexusLang.LoadLocale(locale);
            CreateGUI();
        }
    }
}
