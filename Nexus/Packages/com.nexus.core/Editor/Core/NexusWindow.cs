using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public partial class NexusWindow : EditorWindow
    {
        private enum TabType
        {
            Dashboard,
            Wizard,
            Hierarchy,
            Explorer,
            Tracer
        }

        private TabType _activeTab = TabType.Dashboard;
        private VisualElement _sidebar;
        private VisualElement _contentArea;
        private Label _statusBar;

        // --- Common Editor Styles ---
        private GUIStyle _headerStyle;
        private GUIStyle _actionButtonStyle;
        private GUIStyle _deleteButtonStyle;
        private GUIStyle _miniBoldLabelStyle;
        private static readonly Color HeaderColor = new(0.3f, 0.8f, 1f);
        private static readonly Color ButtonGreenColor = new(0.4f, 1f, 0.4f);
        private static readonly Color ButtonRedColor = new(1f, 0.3f, 0.3f);

        // --- Wizard Tab Fields ---
        private string _wizardContextName = "Gameplay";
        private string _wizardScopeTag = "Gameplay";
        private Root _wizardParentRoot;
        private List<string> _wizardAvailableAssemblies = new();
        private HashSet<string> _wizardSelectedAssemblies = new();
        private bool _wizardAssembliesFoldout;
        private Vector2 _wizardAssembliesScroll;
        private bool _wizardGenerateLifecycleScript = true;
        private bool _wizardGenerateSampleArchitecture = true;
        private int _wizardSelectedSubTab; // 0 = Create Root, 1 = View/Mediator Gen, 2 = Clean Deletion
        private readonly string[] _wizardSubTabNames = { "Create Root", "View/Mediator Gen", "Clean Deletion" };
        private Vector2 _wizardScroll;
        private string _wizardViewName = "GameplayHUD";
        private Root _wizardViewTargetRoot;
        private bool _wizardCreateViewGo = true;
        private Root _wizardRootToDelete;

        // Wizard caching fields
        private Root[] _cachedSceneRoots;
        private double _lastRootCacheTime;
        private const double RootCacheDuration = 1.0;
        private NexusBootstrapManifest _cachedManifest;
        private bool _manifestCacheValid;

        // --- Hierarchy & Data Tab Fields ---
        private int _lastContextVersion;
        private Context _selectedContextForInspector;
        private Vector2 _inspectorScrollPosition;
        private readonly Dictionary<string, bool> _inspectorFoldoutStates = new();
        private string _inspectorSearchFilter = "";
        private double _lastInspectorCleanupTime;

        // --- Signal Explorer & Tester Tab Fields ---
        private string _explorerSearchQuery = "";
        private string _explorerSelectedAssembly = "All Assemblies";
        private List<MappingInfo> _explorerAllMappings = new();
        private List<VisualElement> _explorerRenderedRows = new();
        private DropdownField _explorerAssemblyDropdown;
        private TextField _explorerSearchField;
        private ScrollView _explorerScrollView;

        // Signal Tester details nested in Explorer
        private Type _testerSelectedSignalType;
        private object _testerSignalInstance;
        private FieldInfo[] _testerSignalFields;
        private Vector2 _testerScrollPos;
        private string _testerResultLog;
        private Color _testerResultColor = Color.white;

        // Static assembly reflection caches
        private static List<MappingInfo> s_cachedMappings;
        private static List<string> s_cachedAssemblies;
        private static List<Type> s_cachedSignalTypes;

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_cachedMappings = null;
            s_cachedAssemblies = null;
            s_cachedSignalTypes = null;
        }

        // --- Tracer Tab Fields ---
        private ScrollView _tracerScrollView;
        private Toggle _tracerPauseToggle;
        private bool _tracerIsPaused = false;
        private TraceEvent[] _tracerPausedEvents;
        private int _tracerPausedCount;
        private string _tracerSearchFilter = "";
        private bool _tracerFilterSignal = true;
        private bool _tracerFilterCommand = true;
        private bool _tracerFilterModelChange = true;
        private bool _tracerFilterOk = true;
        private bool _tracerFilterFailed = true;
        private bool _tracerFilterCancelled = true;
        private VisualElement _tracerDetailPanel;
        private Label _tracerDetailContent;
        private TextField _tracerSearchField;
        private readonly List<VisualElement> _tracerRenderedItems = new();
        private readonly Dictionary<int, List<TraceEvent>> _tracerChildrenCache = new();
        private readonly Dictionary<int, int> _tracerDepthsCache = new();
        private int _tracerSelectedEventId = -1;

        // --- Window Entry Point ---
        [MenuItem("Window/Nexus/Dashboard %#n")]
        public static void ShowWindow()
        {
            var window = GetWindow<NexusWindow>("Nexus Dashboard");
            window.minSize = new Vector2(750, 500);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            PopulateAssemblies();
            _manifestCacheValid = false;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            RefreshActiveTabContent();
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

            // Tab Buttons
            AddTabButton("Dashboard", TabType.Dashboard);
            AddTabButton("Context Wizard", TabType.Wizard);
            AddTabButton("Hierarchy & Data", TabType.Hierarchy);
            AddTabButton("Signal Explorer", TabType.Explorer);
            AddTabButton("Live Tracer", TabType.Tracer);

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

            // Set default view
            SwitchTab(TabType.Dashboard);

            // Global updater loop
            root.schedule.Execute(OnScheduledRefresh).Every(100);
        }

        private void AddTabButton(string label, TabType tab)
        {
            var btn = new Button(() => SwitchTab(tab)) { text = label };
            btn.name = $"Tab_{tab}";
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

        private void SwitchTab(TabType tab)
        {
            _activeTab = tab;

            // Highlight active button
            foreach (TabType t in Enum.GetValues(typeof(TabType)))
            {
                var btn = _sidebar.Q<Button>($"Tab_{t}");
                if (btn != null)
                {
                    if (t == _activeTab)
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

            RefreshActiveTabContent();
        }

        private void RefreshActiveTabContent()
        {
            if (_contentArea == null) return;
            _contentArea.Clear();

            switch (_activeTab)
            {
                case TabType.Dashboard:
                    BuildDashboardTab();
                    break;
                case TabType.Wizard:
                    BuildWizardTab();
                    break;
                case TabType.Hierarchy:
                    BuildHierarchyTab();
                    break;
                case TabType.Explorer:
                    BuildExplorerTab();
                    break;
                case TabType.Tracer:
                    BuildTracerTab();
                    break;
            }

            UpdateStatusBarText();
        }

        private void OnScheduledRefresh()
        {
            UpdateStatusBarText();

            // Perform context changes checking for Graph tab
            if (_activeTab == TabType.Hierarchy && Application.isPlaying)
            {
                var activeContexts = NexusRuntime.ActiveContexts;
                int versionHash = ComputeContextVersion(activeContexts);
                if (versionHash != _lastContextVersion)
                {
                    _lastContextVersion = versionHash;
                    RefreshActiveTabContent();
                }
            }

            // Perform live tracing updating for Tracer tab
            if (_activeTab == TabType.Tracer && Application.isPlaying && !_tracerIsPaused)
            {
                var events = NexusTrace.GetRecentEvents(out int count);
                if (count == 0 && _tracerScrollView != null && _tracerScrollView.childCount > 0)
                {
                    _tracerScrollView.Clear();
                    _tracerRenderedItems.Clear();
                }
                if (count > 0)
                {
                    RenderLiveEvents(events, count);
                }
            }
        }

        private void UpdateStatusBarText()
        {
            if (_statusBar == null) return;

            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = GetCachedSceneRoots();
            int rootCount = roots?.Length ?? 0;

            _statusBar.text = playing
                ? $"Nexus ● ACTIVE  |  {contextCount} context(s) active  |  {handlerCount} static handler(s) registered"
                : $"Nexus ○ STANDBY  |  {rootCount} Root(s) in scene  |  Enter Play Mode to activate";
        }

        private void EnsureStyles()
        {
            if (_headerStyle != null) return;
            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            _headerStyle.normal.textColor = Color.white;
            _actionButtonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fixedHeight = 25 };
            _actionButtonStyle.normal.textColor = ButtonGreenColor;
            _deleteButtonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fixedHeight = 25 };
            _deleteButtonStyle.normal.textColor = ButtonRedColor;
            _miniBoldLabelStyle = new GUIStyle(EditorStyles.miniBoldLabel);
        }

        private struct MappingInfo
        {
            public string SignalName;
            public string CommandName;
            public string Mode;
            public string Priority;
            public bool IsAsync;
            public string AssemblyName;
            public Type SignalType;

            public MappingInfo(string signalName, string commandName, string mode, string priority, bool isAsync, string assemblyName, Type signalType)
            {
                SignalName = signalName;
                CommandName = commandName;
                Mode = mode;
                Priority = priority;
                IsAsync = isAsync;
                AssemblyName = assemblyName;
                SignalType = signalType;
            }
        }
    }
}
