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
        public override string DisplayName => "Game Manager";
        public override int Order => 6;

        // ─── Categories ────────────────────────────────────────
        private enum Section { Overview, Contexts, Models, Signals, Commands, Views, Services, Live, SignalTest }
        private Section _activeSection = Section.Overview;
        private readonly Dictionary<Section, Button> _sectionButtons = new();

        // ─── UI ────────────────────────────────────────────────
        private VisualElement _root;
        private ScrollView _content;
        private double _lastRefreshTime;
        private const double RefreshInterval = 0.5;

        private VisualElement _breadcrumb;

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

            // Scheduled refresh while in Play Mode
            _refreshSchedule = _root.schedule.Execute(OnScheduledRefresh).Every(250);

            // Ensure play-mode subscription is active (survives tab switch cycles)
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
        private IVisualElementScheduledItem _refreshSchedule;

        public override void OnEnable()
        {
            base.OnEnable();
            // Registration handled in CreateView to survive tab switches
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            base.OnDisable();
            UnsubscribePlayMode();
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

                // Colored dot — do NOT use btn.text = label; add explicit Label child
                // so dot and text share the same flex layout (Button + text property
                // uses a separate rendering path from child elements, causing misalignment).
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

                // Find the explicit Label child (we no longer use btn.text = label)
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
                    RenderActiveSection();
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

            // Scan assemblies for signal/command/models (static analysis)
            var scannedSignals = new HashSet<string>();
            var scannedCommands = new HashSet<(string cmd, string sig, string mode)>();
            var scannedModels = new HashSet<string>();

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
                            scannedSignals.Add(type.Name);

                        if (type.IsClass && !type.IsAbstract)
                        {
                            var attrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                            foreach (var attr in attrs)
                                scannedCommands.Add((type.Name, attr.SignalType.Name, attr.Mode.ToString()));

                            var compAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                            if (compAttr != null)
                            {
                                var sigs = string.Join("+", compAttr.SignalTypes.Select(t => t.Name));
                                scannedCommands.Add((type.Name, sigs, "Composite"));
                            }

                            if (typeof(IReactiveModel).IsAssignableFrom(type))
                                scannedModels.Add(type.Name);
                        }
                    }
                }
                catch { }
            }

            // Also collect commands from live runtime SignalBus (fluent API registrations)
            if (contexts != null)
            {
                foreach (var ctx in contexts)
                {
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

            // Views are harder to count statically — approximating
            var viewTypes = new HashSet<string>();
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var an = assembly.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Unity") || an.StartsWith("mscorlib")) continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && typeof(View).IsAssignableFrom(type))
                            viewTypes.Add(type.Name);
                    }
                }
                catch { }
            }
            s.ViewCount = viewTypes.Count;

            // Services: check for registered INexusService types
            var serviceTypes = new HashSet<string>();
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var an = assembly.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Unity") || an.StartsWith("mscorlib")) continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && typeof(INexusService).IsAssignableFrom(type))
                            serviceTypes.Add(type.Name);
                    }
                }
                catch { }
            }
            s.ServiceCount = serviceTypes.Count;

            _snapshot = s;
        }

        // ─── Rendering ─────────────────────────────────────────
        private void RenderActiveSection()
        {
            if (_content == null) return;
            _content.Clear();

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

            // Quick actions
            var actionsLabel = NexusEditorStyles.CreateSectionTitle(NexusLang.Get("gamemanager_quick_actions"));
            actionsLabel.style.marginLeft = 15;
            actionsLabel.style.marginTop = 15;
            _content.Add(actionsLabel);

            var actionsRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 15, marginTop = 5, flexWrap = Wrap.Wrap } };
            var openWizard = NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_open_wizard"), () => Window?.SwitchToPlugin("Wizard"), NexusEditorStyles.BtnBlue);
            actionsRow.Add(openWizard);

            var openTracer = NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_open_tracer"), () => Window?.SwitchToPlugin("Tracer"), NexusEditorStyles.BtnTeal);
            openTracer.style.marginLeft = 5;
            actionsRow.Add(openTracer);

            var openGraph = NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_open_graph"), () => Window?.SwitchToPlugin("Graph"), NexusEditorStyles.BtnPurple);
            openGraph.style.marginLeft = 5;
            actionsRow.Add(openGraph);

            var refreshBtn = NexusEditorStyles.CreateButton(NexusLang.Get("gamemanager_refresh"), () => { RefreshSnapshot(); RenderActiveSection(); }, NexusEditorStyles.BtnGray);
            refreshBtn.style.marginLeft = 5;
            actionsRow.Add(refreshBtn);

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
                    borderTopLeftRadius = 6,
                    borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6,
                    borderBottomRightRadius = 6,
                    marginLeft = 5,
                    marginRight = 5,
                    marginTop = 5,
                    marginBottom = 5,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 8,
                    paddingBottom = 8,
                    borderLeftWidth = 3,
                    borderLeftColor = new StyleColor(accent),
                }
            };

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

        // ─── Contexts ──────────────────────────────────────────
        private void RenderContexts()
        {
            var s = _snapshot;
            AddSectionHeader($"CONTEXTS ({s.ContextCount} active)", NexusEditorStyles.AccentGreen);

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No active contexts. Enter Play Mode to activate."));
                return;
            }

            foreach (var ctx in contexts)
            {
                var card = new VisualElement
                {
                    style =
                    {
                        backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                        marginLeft = 15,
                        marginRight = 15,
                        marginTop = 5,
                        marginBottom = 5,
                        paddingLeft = 10,
                        paddingRight = 10,
                        paddingTop = 8,
                        paddingBottom = 8,
                        borderTopLeftRadius = 4,
                        borderTopRightRadius = 4,
                        borderBottomLeftRadius = 4,
                        borderBottomRightRadius = 4,
                    }
                };

                var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
                header.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentGreen));

                // Determine display name: ScopeTag > ContextData name > "(unnamed)"
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

                // Show context metadata
                var meta = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4, flexWrap = Wrap.Wrap } };
                if (!string.IsNullOrEmpty(ctx.ScopeTag))
                    meta.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gamemanager_pill_tag"), ctx.ScopeTag), NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
                if (ctx is Context concreteCtx2 && concreteCtx2.ContextData != null)
                    meta.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gamemanager_pill_cfg"), concreteCtx2.ContextData.name), NexusEditorStyles.BtnGray, NexusEditorStyles.DimText));
                if (ctx.Parent != null)
                    meta.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("gamemanager_pill_parent"), ctx.Parent.ScopeTag ?? NexusLang.Get("gamemanager_unnamed")), NexusEditorStyles.BtnGray, NexusEditorStyles.DimText));

                // Show registered command count if available
                if (ctx.SignalBus.RegisteredHandlers != null)
                {
                    int cmdCount = 0;
                    foreach (var kvp in ctx.SignalBus.RegisteredHandlers)
                        cmdCount += kvp.Value.Count;
                    if (cmdCount > 0)
                        meta.Add(NexusEditorStyles.CreatePill($"{cmdCount} commands", NexusEditorStyles.BtnGray, NexusEditorStyles.AccentOrange));
                }

                card.Add(meta);

                // Show DI bindings from this context
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
                    stats.Add(NexusEditorStyles.CreatePill($"{modelCount} models", NexusEditorStyles.BtnGray, NexusEditorStyles.AccentBlue));
                    stats.Add(NexusEditorStyles.CreatePill($"{serviceCount} services", NexusEditorStyles.BtnGray, NexusEditorStyles.AccentGreen));
                    stats.Add(NexusEditorStyles.CreatePill($"{otherCount} others", NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
                    card.Add(stats);
                }

                _content.Add(card);
            }

            _content.Add(NexusEditorStyles.CreateHint("\nScene Roots: " + s.RootCount + " Root GameObject(s) in scene."));
        }

        // ─── Models ────────────────────────────────────────────
        private void RenderModels()
        {
            var s = _snapshot;
            AddSectionHeader($"MODELS ({s.ModelCount} registered)", NexusEditorStyles.AccentYellow);

            if (s.ModelNames.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No IReactiveModel implementations found."));
                _content.Add(NexusEditorStyles.CreateHint("Tip: Implement IReactiveModel on your models to enable live inspection here."));
                return;
            }

            foreach (var name in s.ModelNames)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 3 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentYellow, 6));
                var lbl = new Label(name) { style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.TextPrimary) } };
                row.Add(lbl);
                row.Add(NexusEditorStyles.CreatePill("IReactiveModel", NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
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
                _content.Add(NexusEditorStyles.CreateEmptyState("No signal structs found."));
                return;
            }

            foreach (var sig in s.SignalNames)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 3 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentPurple, 6));
                var lbl = new Label(sig) { style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.SignalBlue) } };
                row.Add(lbl);

                // Count command handlers for this signal
                int handlerCount = s.CommandEntries.Count(e => e.sig == sig || e.sig.Contains(sig));
                if (handlerCount > 0)
                {
                    row.Add(NexusEditorStyles.CreatePill($"{handlerCount} handler(s)", NexusEditorStyles.BtnGray, NexusEditorStyles.DimText));
                }
                else
                {
                    row.Add(NexusEditorStyles.CreatePill("unhandled", new Color(0.3f, 0.15f, 0.15f), new Color(1f, 0.4f, 0.4f)));
                }

                _content.Add(row);
            }
        }

        // ─── Commands ──────────────────────────────────────────
        private void RenderCommands()
        {
            var s = _snapshot;
            AddSectionHeader($"COMMANDS ({s.CommandCount} bound)", NexusEditorStyles.AccentOrange);

            if (s.CommandEntries.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No command bindings found."));
                return;
            }

            // Table header
            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginLeft = 15,
                    marginRight = 15,
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 4,
                    paddingBottom = 4,
                    backgroundColor = new StyleColor(NexusEditorStyles.TableHeaderBg),
                    borderTopLeftRadius = 4,
                    borderTopRightRadius = 4,
                    marginTop = 5
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
                        marginLeft = 15,
                        marginRight = 15,
                        paddingLeft = 8,
                        paddingRight = 8,
                        paddingTop = 4,
                        paddingBottom = 4,
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
            AddSectionHeader($"VIEWS ({s.ViewCount} defined)", NexusEditorStyles.AccentBlue);

            var viewTypes = new HashSet<string>();
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var an = assembly.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Unity") || an.StartsWith("mscorlib")) continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && typeof(View).IsAssignableFrom(type))
                        {
                            var mediatorAttr = type.GetCustomAttribute<MediatorAttribute>();
                            string mediatorName = mediatorAttr?.MediatorType?.Name ?? "—";
                            viewTypes.Add($"{type.Name} → {mediatorName}");
                        }
                    }
                }
                catch { }
            }

            if (viewTypes.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No View subclasses found."));
                return;
            }

            foreach (var entry in viewTypes.OrderBy(x => x))
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
            AddSectionHeader($"SERVICES ({s.ServiceCount} registered)", NexusEditorStyles.AccentGreen);

            var serviceTypes = new HashSet<string>();
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var an = assembly.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Unity") || an.StartsWith("mscorlib")) continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && typeof(INexusService).IsAssignableFrom(type))
                        {
                            var ifaces = type.GetInterfaces().Where(i => i != typeof(INexusService) && typeof(INexusService).IsAssignableFrom(i));
                            string ifaceName = ifaces.FirstOrDefault()?.Name ?? "—";
                            serviceTypes.Add($"{type.Name}  :  {ifaceName}");
                        }
                    }
                }
                catch { }
            }

            if (serviceTypes.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No INexusService implementations found."));
                _content.Add(NexusEditorStyles.CreateHint("Tip: Use builder.BindService<TInterface, TImpl>() to register services in your lifecycle."));
                return;
            }

            foreach (var entry in serviceTypes.OrderBy(x => x))
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginLeft = 15, marginTop = 3 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentGreen, 6));
                var parts = entry.Split("  :  ");
                row.Add(new Label(parts[0]) { style = { fontSize = 11, color = new StyleColor(NexusEditorStyles.TextPrimary), width = 200 } });
                if (parts.Length > 1)
                    row.Add(new Label(parts[1]) { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
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
            AddSectionHeader(NexusLang.Get("gamemanager_live_title") + (playing ? "" : " (Play Mode only)"), new Color(1f, 0.5f, 0.8f));

            if (!playing)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gamemanager_live_playmode")));
                return;
            }

            NexusRuntime.Metrics.UpdateRates();

            // Performance Metrics Panel
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
            sysRow.Add(CreateMetricBox("GC Alloc", $"{System.GC.GetTotalMemory(false) / 1024 / 1024:N1} MB", NexusEditorStyles.TextSecondary));
            sysRow.Add(CreateMetricBox("Contexts", $"{NexusRuntime.Metrics.ActiveContextCount}", NexusEditorStyles.TextSecondary));
            perfCard.Add(sysRow);

            _content.Add(perfCard);

            // Real-time bar chart
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
            sigBarRow.Add(new Label("Sig/s") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary), width = 45 } });
            var sigBg = new VisualElement { style = { flexGrow = 1, height = 14, backgroundColor = new StyleColor(NexusEditorStyles.RowAlt), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            var sigFill = new VisualElement { style = { width = new Length(Mathf.Clamp(sigRate / maxRate * 100f, 1f, 100f), LengthUnit.Percent), height = 14, backgroundColor = new StyleColor(NexusEditorStyles.AccentBlue), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            sigBg.Add(sigFill);
            sigBarRow.Add(sigBg);
            sigBarRow.Add(new Label($"{sigRate:F1}") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.AccentBlue), width = 40, unityFontStyleAndWeight = FontStyle.Bold } });
            chartCard.Add(sigBarRow);

            // Commands/s bar
            var cmdBarRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            cmdBarRow.Add(new Label("Cmd/s") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary), width = 45 } });
            var cmdBg = new VisualElement { style = { flexGrow = 1, height = 14, backgroundColor = new StyleColor(NexusEditorStyles.RowAlt), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            var cmdFill = new VisualElement { style = { width = new Length(Mathf.Clamp(cmdRate / maxRate * 100f, 1f, 100f), LengthUnit.Percent), height = 14, backgroundColor = new StyleColor(NexusEditorStyles.AccentGreen), borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3 } };
            cmdBg.Add(cmdFill);
            cmdBarRow.Add(cmdBg);
            cmdBarRow.Add(new Label($"{cmdRate:F1}") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.AccentGreen), width = 40, unityFontStyleAndWeight = FontStyle.Bold } });
            chartCard.Add(cmdBarRow);

            _content.Add(chartCard);

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No active contexts."));
                return;
            }

            foreach (var ctx in contexts)
            {
                var ctxLabel = new Label(string.Format(NexusLang.Get("gamemanager_context_label"), ctx.ScopeTag ?? "(no tag)"))
                {
                    style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentGreen), marginLeft = 15, marginTop = 8 }
                };
                _content.Add(ctxLabel);

                var hint = NexusEditorStyles.CreateHint("Live model inspection resolves IReactiveModel instances. Open the Live Tracer for real-time signal monitoring.");
                hint.style.marginLeft = 15;
                _content.Add(hint);
            }
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

        private void RenderSignalTest()
        {
            bool playing = Application.isPlaying;
            AddSectionHeader("SIGNAL TEST PANEL" + (playing ? "" : " (Play Mode only)"), new Color(1f, 0.4f, 0.4f));

            if (!playing)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("Enter Play Mode to fire test signals."));
                return;
            }

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No active contexts to fire signals into."));
                return;
            }

            // Collect registered signal types from runtime SignalBus
            var signalTypes = new Dictionary<string, Type>();
            foreach (var ctx in contexts)
            {
                if (ctx.SignalBus.RegisteredHandlers == null) continue;
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

            // Quick-fire buttons for each signal type
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            card.style.marginLeft = 10;
            card.style.marginRight = 10;
            card.style.marginTop = 8;
            card.style.marginBottom = 8;

            card.Add(new Label(NexusLang.Get("gamemanager_quick_fire"))
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary), marginBottom = 8 }
            });

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
                    try
                    {
                        var ctx = contexts[0]; // Fire into first active context
                        var instance = Activator.CreateInstance(signalType);
                        var fireMethod = ctx.SignalBus.GetType().GetMethod("Fire");
                        if (fireMethod != null)
                        {
                            var genericMethod = fireMethod.MakeGenericMethod(signalType);
                            genericMethod.Invoke(ctx.SignalBus, new[] { instance });
                            _testResult = $"✔ Fired {signalName} @ {System.DateTime.Now:HH:mm:ss}";
                            Debug.Log($"[Nexus Test] Successfully fired signal '{signalName}' via context '{ctx.ScopeTag}'.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _testResult = $"✘ {signalName}: {ex.InnerException?.Message ?? ex.Message}";
                        Debug.LogError($"[Nexus Test] Failed to fire '{signalName}': {ex.Message}");
                    }
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

                // Tooltip: show command count and mode
                int handlerCount = 0;
                string mode = "Sequential";
                if (contexts[0].SignalBus.RegisteredHandlers.TryGetValue(signalType, out var handlers) && handlers.Count > 0)
                {
                    handlerCount = handlers.Count;
                    mode = handlers[0].Mode.ToString();
                }
                fireBtn.tooltip = $"{signalType.FullName}\n{handlerCount} handler(s), {mode} mode";
                buttonRow.Add(fireBtn);
            }

            card.Add(buttonRow);
            _content.Add(card);

            var hint = NexusEditorStyles.CreateHint("Click any signal above to fire it into the first active context. Use the Explorer tab for signals with custom payloads.");
            hint.style.marginLeft = 10;
            _content.Add(hint);
        }
    }
}
