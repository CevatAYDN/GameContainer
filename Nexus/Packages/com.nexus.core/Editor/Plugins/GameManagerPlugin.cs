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
    /// Nexus Game Manager — central hub for inspecting and managing every
    /// registered model, signal, command, view, and service across all active contexts.
    /// </summary>
    public class GameManagerPlugin : NexusEditorPlugin
    {
        public override string Id => "GameManager";
        public override string DisplayName => NexusLang.Get("action_gamemanager_title");
        public override int Order => 6;

        // ─── Categories ────────────────────────────────────────
        private enum Section { Overview, Contexts, Models, Signals, Commands, Views, Services, Live, SignalTest }
        private Section _activeSection = Section.Overview;
        private readonly Dictionary<Section, Button> _sectionButtons = new();

        private static HashSet<string> s_cachedSignals;
        private static HashSet<(string cmd, string sig, string mode)> s_cachedCommands;
        private static HashSet<string> s_cachedModels;
        private static HashSet<string> s_cachedViews;
        private static HashSet<string> s_cachedServices;
        private static bool s_staticScanValid;

        private static readonly Dictionary<Type, MethodInfo> s_fireMethodCache = new();

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_staticScanValid = false;
            s_fireMethodCache.Clear();
        }

        // ─── UI ────────────────────────────────────────────────
        private VisualElement _root;
        private ScrollView _content;
        private double _lastRefreshTime;
        private const double RefreshInterval = 0.5;

        private VisualElement _breadcrumb;
        private TextField _quickFindField;
        private string _searchQuery = string.Empty;

        // Live Section cached elements (G-3)
        private VisualElement _sigFill;
        private VisualElement _cmdFill;
        private Label _sigRateLabel;
        private Label _cmdRateLabel;

        // ─── Cached data (refreshed on demand) ─────────────────
        private class Snapshot
        {
            public int ContextCount;
            public int ModelCount;
            public int SignalCount;
            public int CommandCount;
            public int ViewCount;
            public int ServiceCount;
            public List<string> ContextTags = new();
            public List<string> ModelNames = new();
            public List<string> SignalNames = new();
            public List<string> ViewNames = new();
            public List<string> ServiceNames = new();
            public List<(string cmd, string sig, string mode)> CommandEntries = new();
            public int RootCount;
        }

        private Snapshot _snapshot = new();
        private const string SectionClass = "gm-section-btn";
        private const string SectionActiveClass = "gm-section-btn-active";

        // ─── Plugin Lifecycle ──────────────────────────────────
        public override VisualElement CreateView()
        {
            _root = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("gamemanager_title"));
            _root.Add(toolbar);

            // Breadcrumb / section bar
            _breadcrumb = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    backgroundColor = new StyleColor(NexusEditorStyles.ToolbarBg),
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 4,
                    paddingBottom = 4,
                    borderBottomWidth = 1,
                    borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor),
                    flexWrap = Wrap.Wrap
                }
            };
            _root.Add(_breadcrumb);
            BuildBreadcrumb();

            _content = new ScrollView { style = { flexGrow = 1 } };
            _root.Add(_content);

            var quickBar = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginLeft = 12, marginRight = 12, marginTop = 8 } };
            _quickFindField = new TextField { value = string.Empty, isDelayed = false };
            _quickFindField.style.flexGrow = 1;
            _quickFindField.label = NexusLang.Get("gm_quick_find");
            _quickFindField.tooltip = NexusLang.Get("gm_quick_find_tooltip");
            _quickFindField.RegisterValueChangedCallback(evt =>
            {
                _searchQuery = evt.newValue?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(_searchQuery))
                    return;
            });
            quickBar.Add(_quickFindField);
            quickBar.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gm_go"), ExecuteQuickFind, NexusEditorStyles.BtnBlue));
            quickBar.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gm_refresh_all"), () => { RefreshSnapshot(); RenderActiveSection(); }, NexusEditorStyles.BtnGray));
            _root.Add(quickBar);

            if (!_subscribedToPlayMode)
            {
                EditorApplication.playModeStateChanged += OnPlayModeChange;
                _subscribedToPlayMode = true;
            }

            RefreshSnapshot();
            RenderActiveSection();
            return _root;
        }

        private bool _subscribedToPlayMode;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_subscribedToPlayMode)
            {
                EditorApplication.playModeStateChanged += OnPlayModeChange;
                _subscribedToPlayMode = true;
            }
        }

        public override void OnDisable()
        {
            base.OnDisable();
            UnsubscribePlayMode();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            OnScheduledRefresh();
        }

        private void UnsubscribePlayMode()
        {
            if (_subscribedToPlayMode)
            {
                EditorApplication.playModeStateChanged -= OnPlayModeChange;
                _subscribedToPlayMode = false;
            }
        }

        private void OnPlayModeChange(PlayModeStateChange change)
        {
            RefreshSnapshot();
            RenderActiveSection();
        }

        private void ExecuteQuickFind()
        {
            var query = (_searchQuery ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(query)) return;

            if (query.Contains("context")) SelectSection(Section.Contexts);
            else if (query.Contains("model")) SelectSection(Section.Models);
            else if (query.Contains("signal")) SelectSection(Section.Signals);
            else if (query.Contains("command")) SelectSection(Section.Commands);
            else if (query.Contains("view")) SelectSection(Section.Views);
            else if (query.Contains("service")) SelectSection(Section.Services);
            else if (query.Contains("live")) SelectSection(Section.Live);
            else if (query.Contains("test")) SelectSection(Section.SignalTest);
            else SelectSection(Section.Overview);
        }

        // ─── Breadcrumb ────────────────────────────────────────
        private void BuildBreadcrumb()
        {
            _breadcrumb.Clear();
            _sectionButtons.Clear();

            var sections = new (Section s, string label, Color color)[]
            {
                (Section.Overview,  NexusLang.Get("gamemanager_section_overview"),  NexusEditorStyles.AccentBlue),
                (Section.Contexts,  NexusLang.Get("gamemanager_section_contexts"),  NexusEditorStyles.AccentGreen),
                (Section.Models,    NexusLang.Get("gamemanager_section_models"),    NexusEditorStyles.AccentYellow),
                (Section.Signals,   NexusLang.Get("gamemanager_section_signals"),   NexusEditorStyles.AccentPurple),
                (Section.Commands,  NexusLang.Get("gamemanager_section_commands"),  NexusEditorStyles.AccentOrange),
                (Section.Views,     NexusLang.Get("gamemanager_section_views"),     NexusEditorStyles.AccentBlue),
                (Section.Services,  NexusLang.Get("gamemanager_section_services"),  NexusEditorStyles.AccentGreen),
                (Section.Live,      NexusLang.Get("gamemanager_section_live"),      new Color(1f, 0.5f, 0.8f)),
                (Section.SignalTest,NexusLang.Get("gamemanager_section_test"),      new Color(1f, 0.4f, 0.4f)),
            };

            foreach (var (s, label, color) in sections)
            {
                var btn = new Button(() => SelectSection(s));
                btn.name = $"gm_{s}";
                btn.style.unityFontStyleAndWeight = FontStyle.Bold;
                btn.style.fontSize = 10;
                btn.style.paddingLeft = 10;
                btn.style.paddingRight = 10;
                btn.style.paddingTop = 5;
                btn.style.paddingBottom = 5;
                btn.style.marginLeft = 2;
                btn.style.marginRight = 2;
                btn.style.marginBottom = 4;
                btn.style.borderTopLeftRadius = 4;
                btn.style.borderTopRightRadius = 4;
                btn.style.borderBottomLeftRadius = 4;
                btn.style.borderBottomRightRadius = 4;
                btn.style.borderTopWidth = 0;
                btn.style.borderBottomWidth = 0;
                btn.style.borderLeftWidth = 0;
                btn.style.borderRightWidth = 0;
                btn.style.backgroundColor = new StyleColor(Color.clear);
                btn.style.flexShrink = 0;
                btn.style.alignItems = Align.Center;
                btn.style.flexDirection = FlexDirection.Row;

                var dot = NexusEditorStyles.CreateStatusDot(color, 6);
                btn.Add(dot);

                var txtLabel = new Label(label);
                txtLabel.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
                btn.Add(txtLabel);

                _breadcrumb.Add(btn);
                _sectionButtons[s] = btn;
            }

            HighlightSection();
        }

        private void SelectSection(Section s)
        {
            _activeSection = s;
            HighlightSection();
            RefreshSnapshot();
            RenderActiveSection();
        }

        private void HighlightSection()
        {
            foreach (var kvp in _sectionButtons)
            {
                bool active = kvp.Key == _activeSection;
                kvp.Value.style.backgroundColor = active
                    ? new StyleColor(NexusEditorStyles.HighlightBg)
                    : new StyleColor(Color.clear);

                var txtLabel = kvp.Value.Q<Label>();
                if (txtLabel != null)
                {
                    txtLabel.style.color = active
                        ? new StyleColor(NexusEditorStyles.AccentBlue)
                        : new StyleColor(NexusEditorStyles.TextPrimary);
                }
            }
        }

        // ─── Data refresh ──────────────────────────────────────
        private void OnScheduledRefresh()
        {
            if (!Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefreshTime > RefreshInterval)
            {
                _lastRefreshTime = now;
                RefreshSnapshot();
                if (_content != null && _content.childCount > 0)
                {
                    if (_activeSection == Section.Live && _sigFill != null)
                    {
                        UpdateLiveMetricsChartOnly();
                    }
                    else
                    {
                        RenderActiveSection();
                    }
                }
            }
        }

        private void RefreshSnapshot()
        {
            var s = new Snapshot();

            // Roots in scene
            var roots = UnityEngine.Object.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            s.RootCount = roots.Length;

            // Active contexts
            var contexts = NexusRuntime.ActiveContexts;
            s.ContextCount = contexts?.Count ?? 0;
            if (contexts != null)
            {
                foreach (var ctx in contexts)
                    s.ContextTags.Add(ctx.ScopeTag ?? "(no tag)");
            }

            if (!s_staticScanValid)
            {
                s_cachedSignals = new HashSet<string>();
                s_cachedCommands = new HashSet<(string cmd, string sig, string mode)>();
                s_cachedModels = new HashSet<string>();
                s_cachedViews = new HashSet<string>();
                s_cachedServices = new HashSet<string>();

                foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
                {
                    var name = assembly.GetName().Name;
                    if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("mscorlib") || name.StartsWith("Mono") || name.StartsWith("nunit"))
                        continue;

                    try
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            if (type.IsValueType && !type.IsPrimitive && !type.IsEnum && type.Name.EndsWith("Signal"))
                                s_cachedSignals.Add(type.Name);

                            if (type.IsClass && !type.IsAbstract)
                            {
                                var attrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                                foreach (var attr in attrs)
                                    s_cachedCommands.Add((type.Name, attr.SignalType.Name, attr.Mode.ToString()));

                                var compAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                                if (compAttr != null)
                                {
                                    var sigs = string.Join("+", compAttr.SignalTypes.Select(t => t.Name));
                                    s_cachedCommands.Add((type.Name, sigs, "Composite"));
                                }

                                if (typeof(IReactiveModel).IsAssignableFrom(type))
                                    s_cachedModels.Add(type.Name);

                                if (typeof(View).IsAssignableFrom(type))
                                {
                                    var mediatorAttr = type.GetCustomAttribute<MediatorAttribute>();
                                    string mediatorName = mediatorAttr?.MediatorType?.Name ?? "—";
                                    s_cachedViews.Add($"{type.Name} → {mediatorName}");
                                }

                                if (typeof(INexusService).IsAssignableFrom(type))
                                {
                                    var ifaces = type.GetInterfaces().Where(i => i != typeof(INexusService) && typeof(INexusService).IsAssignableFrom(i));
                                    string ifaceName = ifaces.FirstOrDefault()?.Name ?? "—";
                                    s_cachedServices.Add($"{type.Name}  :  {ifaceName}");
                                }
                            }
                        }
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        foreach (var le in ex.LoaderExceptions)
                        {
                            if (le != null) Debug.LogWarning($"[Nexus GameManager] Type load warning in {name}: {le.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Nexus GameManager] Assembly scan warning for {name}: {ex.Message}");
                    }
                }
                s_staticScanValid = true;
            }

            var scannedSignals = new HashSet<string>(s_cachedSignals);
            var scannedCommands = new HashSet<(string cmd, string sig, string mode)>(s_cachedCommands);
            var scannedModels = new HashSet<string>(s_cachedModels);

            if (contexts != null)
            {
                foreach (var ctx in contexts)
                {
                    if (ctx.SignalBus == null) continue;
                    var handlers = ctx.SignalBus.RegisteredHandlers;
                    if (handlers == null) continue;
                    foreach (var kvp in handlers)
                    {
                        var signalName = kvp.Key.Name;
                        foreach (var info in kvp.Value)
                        {
                            var cmdName = info.CommandType.Name;
                            scannedCommands.Add((cmdName, signalName, info.Mode.ToString()));
                        }
                    }
                }
            }

            s.SignalCount = scannedSignals.Count;
            s.SignalNames = scannedSignals.OrderBy(x => x).ToList();
            s.CommandCount = scannedCommands.Count;
            s.CommandEntries = scannedCommands.OrderBy(x => x.cmd).ToList();
            s.ModelCount = scannedModels.Count;
            s.ModelNames = scannedModels.OrderBy(x => x).ToList();
            s.ViewCount = s_cachedViews.Count;
            s.ViewNames = s_cachedViews.OrderBy(x => x).ToList();
            s.ServiceCount = s_cachedServices.Count;
            s.ServiceNames = s_cachedServices.OrderBy(x => x).ToList();

            _snapshot = s;
        }

        // ─── Rendering ─────────────────────────────────────────
        private void RenderActiveSection()
        {
            if (_content == null) return;
            _content.Clear();
            _sigFill = null;
            _cmdFill = null;

            switch (_activeSection)
            {
                case Section.Overview: RenderOverview(); break;
                case Section.Contexts: RenderContexts(); break;
                case Section.Models: RenderModels(); break;
                case Section.Signals: RenderSignals(); break;
                case Section.Commands: RenderCommands(); break;
                case Section.Views: RenderViews(); break;
                case Section.Services: RenderServices(); break;
                case Section.Live: RenderLive(); break;
                case Section.SignalTest: RenderSignalTest(); break;
            }
        }

        // ─── Overview ──────────────────────────────────────────
        private void RenderOverview()
        {
            var s = _snapshot;
            bool playing = Application.isPlaying;

            // Header status
            var statusRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 15, marginTop = 10, marginBottom = 10 } };
            var dot = NexusEditorStyles.CreateStatusDot(playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.DimText, 10);
            statusRow.Add(dot);
            var statusText = new Label(playing ? NexusLang.Get("gamemanager_active") : NexusLang.Get("gamemanager_standby"))
            {
                style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.TextSecondary) }
            };
            statusRow.Add(statusText);
            _content.Add(statusRow);

            // Stats grid
            var grid = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginLeft = 15,
                    marginRight = 15
                }
            };

            AddStatCard(grid, NexusLang.Get("gamemanager_stat_contexts"), s.ContextCount.ToString(), NexusEditorStyles.AccentGreen, NexusLang.Get("gamemanager_desc_contexts"));
            AddStatCard(grid, NexusLang.Get("gamemanager_stat_models"), s.ModelCount.ToString(), NexusEditorStyles.AccentYellow, NexusLang.Get("gamemanager_desc_models"));
            AddStatCard(grid, NexusLang.Get("gamemanager_stat_signals"), s.SignalCount.ToString(), NexusEditorStyles.AccentPurple, NexusLang.Get("gamemanager_desc_signals"));
            AddStatCard(grid, NexusLang.Get("gamemanager_stat_commands"), s.CommandCount.ToString(), NexusEditorStyles.AccentOrange, NexusLang.Get("gamemanager_desc_commands"));
            AddStatCard(grid, NexusLang.Get("gamemanager_stat_views"), s.ViewCount.ToString(), NexusEditorStyles.AccentBlue, NexusLang.Get("gamemanager_desc_views"));
            AddStatCard(grid, NexusLang.Get("gamemanager_stat_services"), s.ServiceCount.ToString(), NexusEditorStyles.AccentGreen, NexusLang.Get("gamemanager_desc_services"));
            AddStatCard(grid, NexusLang.Get("gamemanager_stat_roots"), s.RootCount.ToString(), NexusEditorStyles.DimText, NexusLang.Get("gamemanager_desc_roots"));

            _content.Add(grid);

            // Quick actions (G-6: Consolidated single actions row)
            var actionsLabel = NexusEditorStyles.CreateSectionTitle(NexusLang.Get("gamemanager_quick_actions"));
            actionsLabel.style.marginLeft = 15;
            actionsLabel.style.marginTop = 15;
            _content.Add(actionsLabel);

            var actionsRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 15, marginTop = 5, flexWrap = Wrap.Wrap } };
            actionsRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_open_wizard"), () => Window?.SwitchToPlugin("Wizard"), NexusEditorStyles.BtnBlue));
            actionsRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_open_tracer"), () => Window?.SwitchToPlugin("Tracer"), NexusEditorStyles.BtnTeal));
            actionsRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_open_graph"), () => Window?.SwitchToPlugin("Graph"), NexusEditorStyles.BtnPurple));
            actionsRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("tab_hierarchy"), () => Window?.SwitchToPlugin("Hierarchy"), NexusEditorStyles.BtnGreen));
            actionsRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("tab_explorer"), () => Window?.SwitchToPlugin("Explorer"), NexusEditorStyles.BtnPurple));
            actionsRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("tab_typeanalyzer"), () => Window?.SwitchToPlugin("TypeAnalyzer"), NexusEditorStyles.BtnBlue));
            actionsRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_refresh"), () => { RefreshSnapshot(); RenderActiveSection(); }, NexusEditorStyles.BtnGray));

            _content.Add(actionsRow);
        }

        private void AddStatCard(VisualElement parent, string label, string value, Color accent, string description)
        {
            var card = new VisualElement
            {
                style =
                {
                    width = 140,
                    height = 80,
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    borderTopLeftRadius = 6, borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6, borderBottomRightRadius = 6,
                    marginLeft = 5, marginRight = 5, marginTop = 5, marginBottom = 5,
                    paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 8,
                    borderLeftWidth = 3, borderLeftColor = new StyleColor(accent),
                }
            };

            MakeDoubleClickToOpen(card, label.Contains("Context") ? "Hierarchy" : "GameManager");

            var valLabel = new Label(value)
            {
                style = { fontSize = 22, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(accent) }
            };
            card.Add(valLabel);

            var nameLabel = new Label(label)
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), unityFontStyleAndWeight = FontStyle.Bold, marginTop = 2 }
            };
            card.Add(nameLabel);

            if (!string.IsNullOrEmpty(description))
            {
                var desc = new Label(description)
                {
                    style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 2, whiteSpace = WhiteSpace.Normal }
                };
                card.Add(desc);
            }

            parent.Add(card);
        }

        private void MakeDoubleClickToOpen(VisualElement el, string pluginId)
        {
            el.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2)
                    Window?.SwitchToPlugin(pluginId);
            });
        }

        // ─── Contexts ──────────────────────────────────────────
        private void RenderContexts()
        {
            var s = _snapshot;
            AddSectionHeader(string.Format(NexusLang.Get("gm_contexts_header"), s.ContextCount), NexusEditorStyles.AccentGreen);

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_contexts")));
                return;
            }

            foreach (var ctx in contexts)
            {
                var card = new VisualElement
                {
                    style =
                    {
                        backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                        marginLeft = 15, marginRight = 15, marginTop = 5, marginBottom = 5,
                        paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 8,
                        borderTopLeftRadius = 4, borderTopRightRadius = 4,
                        borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    }
                };

                MakeDoubleClickToOpen(card, "Hierarchy");

                var rowActionRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 6 } };
                rowActionRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("nav_open_hierarchy"), () => Window?.SwitchToPlugin("Hierarchy"), NexusEditorStyles.BtnGreen));
                rowActionRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("nav_open_explorer"), () => Window?.SwitchToPlugin("Explorer"), NexusEditorStyles.BtnPurple));
                rowActionRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("nav_open_tracer"), () => Window?.SwitchToPlugin("Tracer"), NexusEditorStyles.BtnTeal));
                card.Add(rowActionRow);

                var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
                header.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentGreen));

                string displayName = ctx.ScopeTag;
                if (string.IsNullOrEmpty(displayName) && ctx is Context c && c.ContextData != null)
                    displayName = c.ContextData.name.Replace("ContextData", "");
                if (string.IsNullOrEmpty(displayName))
                    displayName = NexusLang.Get("gamemanager_unnamed");

                header.Add(new Label(displayName)
                {
                    style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary) }
                });
                header.Add(NexusEditorStyles.CreatePill(ctx.Parent != null ? NexusLang.Get("gamemanager_pill_child") : NexusLang.Get("gamemanager_pill_root"), NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
                card.Add(header);

                var meta = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4, flexWrap = Wrap.Wrap } };
                if (!string.IsNullOrEmpty(ctx.ScopeTag))
                    meta.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gamemanager_pill_tag"), ctx.ScopeTag), NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
                if (ctx is Context concreteCtx2 && concreteCtx2.ContextData != null)
                    meta.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gamemanager_pill_cfg"), concreteCtx2.ContextData.name), NexusEditorStyles.BtnGray, NexusEditorStyles.DimText));
                if (ctx.Parent != null)
                    meta.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gamemanager_pill_parent"), ctx.Parent.ScopeTag ?? NexusLang.Get("gamemanager_unnamed")), NexusEditorStyles.BtnGray, NexusEditorStyles.DimText));

                if (ctx.SignalBus != null && ctx.SignalBus.RegisteredHandlers != null)
                {
                    int cmdCount = 0;
                    foreach (var kvp in ctx.SignalBus.RegisteredHandlers)
                        cmdCount += kvp.Value.Count;
                    if (cmdCount > 0)
                        meta.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gm_count_commands"), cmdCount), NexusEditorStyles.BtnGray, NexusEditorStyles.AccentOrange));
                }

                card.Add(meta);

                if (ctx is Context concreteCtx)
                {
                    var bindings = concreteCtx.Container.GetRegisteredSingletons();
                    int modelCount = 0, serviceCount = 0, otherCount = 0;
                    foreach (var kvp in bindings)
                    {
                        if (typeof(IReactiveModel).IsAssignableFrom(kvp.Key)) modelCount++;
                        else if (typeof(INexusService).IsAssignableFrom(kvp.Key)) serviceCount++;
                        else otherCount++;
                    }
                    var stats = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
                    stats.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gm_count_models"), modelCount), NexusEditorStyles.BtnGray, NexusEditorStyles.AccentBlue));
                    stats.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gm_count_services"), serviceCount), NexusEditorStyles.BtnGray, NexusEditorStyles.AccentGreen));
                    stats.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gm_count_others"), otherCount), NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
                    card.Add(stats);
                }

                _content.Add(card);
            }

            _content.Add(NexusEditorStyles.CreateHint(string.Format(NexusLang.Get("gm_roots_hint"), s.RootCount)));
        }

        // ─── Models ────────────────────────────────────────────
        private void RenderModels()
        {
            var s = _snapshot;
            AddSectionHeader($"MODELS ({s.ModelCount} registered)", NexusEditorStyles.AccentYellow);

            if (s.ModelNames.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_reactive_models")));
                _content.Add(NexusEditorStyles.CreateHint(NexusLang.Get("gm_tip_reactive")));
                return;
            }

            foreach (var name in s.ModelNames)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 3 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentYellow, 6));
                var lbl = new Label(name) { style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.TextPrimary) } };
                row.Add(lbl);
                row.Add(NexusEditorStyles.CreatePill("IReactiveModel", NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
                MakeDoubleClickToOpen(row, "Hierarchy");
                _content.Add(row);
            }
        }

        // ─── Signals ───────────────────────────────────────────
        private void RenderSignals()
        {
            var s = _snapshot;
            AddSectionHeader($"SIGNALS ({s.SignalCount} defined)", NexusEditorStyles.AccentPurple);

            if (s.SignalNames.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_signals")));
                return;
            }

            foreach (var sig in s.SignalNames)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 3 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentPurple, 6));
                var lbl = new Label(sig) { style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.SignalBlue) } };
                row.Add(lbl);

                int handlerCount = s.CommandEntries.Count(e => e.sig == sig || e.sig.Contains(sig));
                if (handlerCount > 0)
                {
                    row.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gm_count_handlers"), handlerCount), NexusEditorStyles.BtnGray, NexusEditorStyles.DimText));
                }
                else
                {
                    row.Add(NexusEditorStyles.CreatePill(NexusLang.Get("gm_pill_unhandled"), new Color(0.3f, 0.15f, 0.15f), new Color(1f, 0.4f, 0.4f)));
                }

                MakeDoubleClickToOpen(row, "Explorer");
                _content.Add(row);
            }
        }

        // ─── Commands ──────────────────────────────────────────
        private void RenderCommands()
        {
            var s = _snapshot;
            AddSectionHeader(string.Format(NexusLang.Get("gm_commands_header"), s.CommandCount), NexusEditorStyles.AccentOrange);

            if (s.CommandEntries.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_commands")));
                return;
            }

            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginLeft = 15, marginRight = 15,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 4, paddingBottom = 4,
                    backgroundColor = new StyleColor(NexusEditorStyles.TableHeaderBg),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4, marginTop = 5
                }
            };
            header.Add(new Label(NexusLang.Get("gamemanager_command_col")) { style = { width = 180, fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
            header.Add(new Label(NexusLang.Get("gamemanager_signal_col")) { style = { width = 180, fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
            header.Add(new Label(NexusLang.Get("gamemanager_mode_col")) { style = { width = 100, fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
            _content.Add(header);

            for (int i = 0; i < s.CommandEntries.Count; i++)
            {
                var (cmd, sig, mode) = s.CommandEntries[i];
                var row = new VisualElement
                {
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        marginLeft = 15, marginRight = 15,
                        paddingLeft = 8, paddingRight = 8, paddingTop = 4, paddingBottom = 4,
                        backgroundColor = new StyleColor(i % 2 == 0 ? NexusEditorStyles.RowBase : NexusEditorStyles.RowAlt)
                    }
                };
                row.Add(new Label(cmd) { style = { width = 180, fontSize = 10, color = new StyleColor(NexusEditorStyles.TextPrimary) } });
                row.Add(new Label(sig) { style = { width = 180, fontSize = 10, color = new StyleColor(NexusEditorStyles.SignalBlue) } });

                Color modeColor = mode switch
                {
                    "Concurrent" => NexusEditorStyles.AccentGreen,
                    "Exclusive" => NexusEditorStyles.AccentRed,
                    "Composite" => NexusEditorStyles.AccentPurple,
                    _ => NexusEditorStyles.DimText
                };
                row.Add(new Label(mode) { style = { width = 100, fontSize = 10, color = new StyleColor(modeColor), unityFontStyleAndWeight = FontStyle.Bold } });

                _content.Add(row);
            }
        }

        // ─── Views ─────────────────────────────────────────────
        private void RenderViews()
        {
            var s = _snapshot;
            AddSectionHeader(string.Format(NexusLang.Get("gm_views_header"), s.ViewCount), NexusEditorStyles.AccentBlue);

            if (s.ViewNames.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_views")));
                return;
            }

            foreach (var entry in s.ViewNames)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 3 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentBlue, 6));
                row.Add(new Label(entry) { style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.TextPrimary) } });
                _content.Add(row);
            }
        }

        // ─── Services ──────────────────────────────────────────
        private void RenderServices()
        {
            var s = _snapshot;
            AddSectionHeader(string.Format(NexusLang.Get("gm_services_header"), s.ServiceCount), NexusEditorStyles.AccentGreen);

            if (s.ServiceNames.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_services")));
                _content.Add(NexusEditorStyles.CreateHint(NexusLang.Get("gm_tip_services")));
                return;
            }

            foreach (var entry in s.ServiceNames)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 3 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentGreen, 6));
                var parts = entry.Split("  :  ");
                row.Add(new Label(parts[0]) { style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.TextPrimary), width = 200 } });
                if (parts.Length > 1)
                    row.Add(new Label(parts[1]) { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
                MakeDoubleClickToOpen(row, "Hierarchy");
                _content.Add(row);
            }
        }

        // ─── Helpers ───────────────────────────────────────────
        private void AddSectionHeader(string text, Color accentColor)
        {
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 10, marginBottom = 5 } };
            header.Add(NexusEditorStyles.CreateStatusDot(accentColor, 8));
            header.Add(new Label(text)
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary), marginLeft = 5 }
            });
            _content.Add(header);
        }

        // ─── Live Model Inspector ─────────────────────────────
        private void RenderLive()
        {
            bool playing = Application.isPlaying;
            AddSectionHeader(NexusLang.Get("gamemanager_live_title") + (playing ? "" : NexusLang.Get("gm_playmode_only")), new Color(1f, 0.5f, 0.8f));

            if (!playing)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gamemanager_live_playmode")));
                return;
            }

            NexusRuntime.Metrics.UpdateRates();

            var perfCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            perfCard.style.marginBottom = 10;
            perfCard.style.marginLeft = 10;
            perfCard.style.marginRight = 10;

            var perfTitle = new Label(NexusLang.Get("gamemanager_performance"))
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentYellow), marginBottom = 8 }
            };
            perfCard.Add(perfTitle);

            var perfRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            perfRow.Add(CreateMetricBox(NexusLang.Get("gamemanager_metric_signals_s"), $"{NexusRuntime.Metrics.SignalsPerSecond:F1}", NexusEditorStyles.AccentBlue));
            perfRow.Add(CreateMetricBox(NexusLang.Get("gamemanager_metric_commands_s"), $"{NexusRuntime.Metrics.CommandsPerSecond:F1}", NexusEditorStyles.AccentGreen));
            perfRow.Add(CreateMetricBox(NexusLang.Get("gamemanager_metric_total_signals"), $"{NexusRuntime.Metrics.TotalSignalsDispatched:N0}", NexusEditorStyles.AccentPurple));
            perfRow.Add(CreateMetricBox(NexusLang.Get("gamemanager_metric_total_cmds"), $"{NexusRuntime.Metrics.TotalCommandsExecuted:N0}", NexusEditorStyles.AccentOrange));
            perfCard.Add(perfRow);

            var sysRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };
            sysRow.Add(CreateMetricBox(NexusLang.Get("gm_gc_alloc"), $"{System.GC.GetTotalMemory(false) / 1024 / 1024:N1} MB", NexusEditorStyles.TextSecondary));
            sysRow.Add(CreateMetricBox(NexusLang.Get("gm_contexts_metric"), $"{NexusRuntime.Metrics.ActiveContextCount}", NexusEditorStyles.TextSecondary));
            perfCard.Add(sysRow);

            _content.Add(perfCard);

            // Real-time bar chart (G-3: reusable elements)
            float sigRate = NexusRuntime.Metrics.SignalsPerSecond;
            float cmdRate = NexusRuntime.Metrics.CommandsPerSecond;
            float maxRate = Mathf.Max(sigRate, cmdRate, 1f);

            var chartCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            chartCard.style.marginBottom = 10;
            chartCard.style.marginLeft = 10;
            chartCard.style.marginRight = 10;

            var chartTitle = new Label(NexusLang.Get("gamemanager_rate_graph"))
            {
                style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 6 }
            };
            chartCard.Add(chartTitle);

            // Signals/s bar
            var sigBarRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            sigBarRow.Add(new Label(NexusLang.Get("gm_sig_per_sec")) { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary), width = 45 } });
            var sigBg = new VisualElement { style = { flexGrow = 1, height = 14, backgroundColor = new StyleColor(NexusEditorStyles.RowAlt), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            _sigFill = new VisualElement { style = { width = new Length(Mathf.Clamp(sigRate / maxRate * 100f, 1f, 100f), LengthUnit.Percent), height = 14, backgroundColor = new StyleColor(NexusEditorStyles.AccentBlue), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            sigBg.Add(_sigFill);
            sigBarRow.Add(sigBg);
            _sigRateLabel = new Label($"{sigRate:F1}") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.AccentBlue), width = 40, unityFontStyleAndWeight = FontStyle.Bold } };
            sigBarRow.Add(_sigRateLabel);
            chartCard.Add(sigBarRow);

            // Commands/s bar
            var cmdBarRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            cmdBarRow.Add(new Label(NexusLang.Get("gm_cmd_per_sec")) { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary), width = 45 } });
            var cmdBg = new VisualElement { style = { flexGrow = 1, height = 14, backgroundColor = new StyleColor(NexusEditorStyles.RowAlt), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            _cmdFill = new VisualElement { style = { width = new Length(Mathf.Clamp(cmdRate / maxRate * 100f, 1f, 100f), LengthUnit.Percent), height = 14, backgroundColor = new StyleColor(NexusEditorStyles.AccentGreen), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            cmdBg.Add(_cmdFill);
            cmdBarRow.Add(cmdBg);
            _cmdRateLabel = new Label($"{cmdRate:F1}") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.AccentGreen), width = 40, unityFontStyleAndWeight = FontStyle.Bold } };
            cmdBarRow.Add(_cmdRateLabel);
            chartCard.Add(cmdBarRow);

            _content.Add(chartCard);

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_active_contexts")));
                return;
            }

            foreach (var ctx in contexts)
            {
                var ctxLabel = new Label(string.Format(NexusLang.Get("gamemanager_context_label"), ctx.ScopeTag ?? "(no tag)"))
                {
                    style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentGreen), marginLeft = 15, marginTop = 8 }
                };
                _content.Add(ctxLabel);

                var hint = NexusEditorStyles.CreateHint(NexusLang.Get("gm_hint_live_model"));
                hint.style.marginLeft = 15;
                _content.Add(hint);
            }
        }

        private void UpdateLiveMetricsChartOnly()
        {
            if (!Application.isPlaying) return;
            NexusRuntime.Metrics.UpdateRates();
            float sigRate = NexusRuntime.Metrics.SignalsPerSecond;
            float cmdRate = NexusRuntime.Metrics.CommandsPerSecond;
            float maxRate = Mathf.Max(sigRate, cmdRate, 1f);

            if (_sigFill != null)
                _sigFill.style.width = new Length(Mathf.Clamp(sigRate / maxRate * 100f, 1f, 100f), LengthUnit.Percent);
            if (_cmdFill != null)
                _cmdFill.style.width = new Length(Mathf.Clamp(cmdRate / maxRate * 100f, 1f, 100f), LengthUnit.Percent);
            if (_sigRateLabel != null)
                _sigRateLabel.text = $"{sigRate:F1}";
            if (_cmdRateLabel != null)
                _cmdRateLabel.text = $"{cmdRate:F1}";
        }

        private VisualElement CreateMetricBox(string label, string value, Color accent)
        {
            var box = new VisualElement
            {
                style =
                {
                    flexGrow = 1, alignItems = Align.Center, paddingLeft = 6, paddingRight = 6,
                    paddingTop = 4, paddingBottom = 4, marginRight = 4,
                    borderRightWidth = 1, borderRightColor = new StyleColor(NexusEditorStyles.BorderColor)
                }
            };
            var valLabel = new Label(value) { style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(accent) } };
            var descLabel = new Label(label) { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary) } };
            box.Add(valLabel);
            box.Add(descLabel);
            return box;
        }

        // ─── Signal Test Panel ─────────────────────────────────
        private string _testResult = "";
        private int _signalTestContextIndex = -1;

        private void RenderSignalTest()
        {
            bool playing = Application.isPlaying;
            AddSectionHeader(NexusLang.Get("gm_signal_test_panel") + (playing ? "" : NexusLang.Get("gm_playmode_only")), new Color(1f, 0.4f, 0.4f));

            if (!playing)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_enter_playmode_fire")));
                return;
            }

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_contexts_fire")));
                return;
            }

            var signalTypes = new Dictionary<string, Type>();
            foreach (var ctx in contexts)
            {
                if (ctx.SignalBus?.RegisteredHandlers == null) continue;
                foreach (var kvp in ctx.SignalBus.RegisteredHandlers)
                {
                    if (!signalTypes.ContainsKey(kvp.Key.Name))
                        signalTypes[kvp.Key.Name] = kvp.Key;
                }
            }

            if (signalTypes.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gamemanager_empty_signals")));
                return;
            }

            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            card.style.marginLeft = 10;
            card.style.marginRight = 10;
            card.style.marginTop = 8;
            card.style.marginBottom = 8;

            card.Add(new Label(NexusLang.Get("gamemanager_quick_fire"))
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary), marginBottom = 8 }
            });

            var ctxChoices = new List<string> { NexusLang.Get("gm_all_matching") };
            foreach (var c in contexts) ctxChoices.Add(c.ScopeTag ?? NexusLang.Get("fsm_fallback_context"));
            int dropdownIndex = _signalTestContextIndex < 0 ? 0 : Mathf.Min(_signalTestContextIndex + 1, ctxChoices.Count - 1);
            var ctxDropdown = new DropdownField("Target Context", ctxChoices, dropdownIndex);
            ctxDropdown.RegisterValueChangedCallback(evt =>
            {
                int idx = ctxChoices.IndexOf(evt.newValue);
                _signalTestContextIndex = idx <= 0 ? -1 : idx - 1;
                RenderActiveSection();
            });
            ctxDropdown.style.marginBottom = 8;
            card.Add(ctxDropdown);

            if (!string.IsNullOrEmpty(_testResult))
            {
                var resultLabel = new Label(_testResult)
                {
                    style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.AccentGreen), marginBottom = 8, whiteSpace = WhiteSpace.Normal }
                };
                card.Add(resultLabel);
            }

            var buttonRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            foreach (var kvp in signalTypes.OrderBy(k => k.Key))
            {
                var signalName = kvp.Key;
                var signalType = kvp.Value;

                var fireBtn = new Button(() =>
                {
                    FireTestSignal(signalName, signalType, contexts);
                    RenderActiveSection();
                })
                {
                    text = signalName,
                    style =
                    {
                        fontSize = 9,
                        paddingLeft = 8, paddingRight = 8, paddingTop = 4, paddingBottom = 4,
                        marginRight = 4, marginBottom = 4,
                        backgroundColor = new StyleColor(NexusEditorStyles.BtnPurple),
                        color = Color.white,
                        borderTopLeftRadius = 3, borderTopRightRadius = 3,
                        borderBottomLeftRadius = 3, borderBottomRightRadius = 3
                    }
                };

                int handlerCount = 0;
                string mode = "Sequential";
                foreach (var c in contexts)
                {
                    if (c.SignalBus?.RegisteredHandlers != null &&
                        c.SignalBus.RegisteredHandlers.TryGetValue(signalType, out var hs) && hs.Count > 0)
                    {
                        handlerCount = hs.Count;
                        mode = hs[0].Mode.ToString();
                        break;
                    }
                }
                fireBtn.tooltip = $"{signalType.FullName}\n{handlerCount} handler(s), {mode} mode";
                buttonRow.Add(fireBtn);
            }

            card.Add(buttonRow);
            _content.Add(card);

            var hint = NexusEditorStyles.CreateHint(NexusLang.Get("gm_hint_fire"));
            hint.style.marginLeft = 10;
            _content.Add(hint);
        }

        private void FireTestSignal(string signalName, Type signalType, IReadOnlyList<IContext> contexts)
        {
            var targets = new List<IContext>();
            for (int i = 0; i < contexts.Count; i++)
            {
                var ctx = contexts[i];
                bool hasHandler = ctx.SignalBus?.RegisteredHandlers != null &&
                                  ctx.SignalBus.RegisteredHandlers.ContainsKey(signalType);
                if (_signalTestContextIndex < 0)
                {
                    if (hasHandler) targets.Add(ctx);
                }
                else if (i == _signalTestContextIndex)
                {
                    targets.Add(ctx);
                }
            }

            if (targets.Count == 0)
            {
                _testResult = string.Format(NexusLang.Get("gm_result_error"), signalName);
                Debug.LogWarning($"[Nexus Test] No target context for '{signalName}'.");
                return;
            }

            int fired = 0;
            foreach (var ctx in targets)
            {
                try
                {
                    if (ctx.SignalBus == null) continue;
                    var instance = Activator.CreateInstance(signalType);

                    if (!s_fireMethodCache.TryGetValue(signalType, out var fireMethod))
                    {
                        var rawMethod = ctx.SignalBus.GetType().GetMethod("Fire");
                        if (rawMethod != null)
                        {
                            fireMethod = rawMethod.MakeGenericMethod(signalType);
                            s_fireMethodCache[signalType] = fireMethod;
                        }
                    }

                    if (fireMethod != null)
                    {
                        fireMethod.Invoke(ctx.SignalBus, new[] { instance });
                        fired++;
                        Debug.Log($"[Nexus Test] Fired '{signalName}' via context '{ctx.ScopeTag}'.");
                    }
                }
                catch (Exception ex)
                {
                    _testResult = $"✘ {signalName} @ {ctx.ScopeTag}: {ex.InnerException?.Message ?? ex.Message}";
                    Debug.LogError($"[Nexus Test] Failed to fire '{signalName}' in '{ctx.ScopeTag}': {ex.Message}");
                    return;
                }
            }
            _testResult = string.Format(NexusLang.Get("gm_result_success"), signalName, fired, System.DateTime.Now);
        }
    }
}
