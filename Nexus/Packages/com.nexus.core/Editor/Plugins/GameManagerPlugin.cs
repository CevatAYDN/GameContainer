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
        public override int Order => 2;

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

            var toolbar = NexusEditorStyles.CreateToolbar("GAME MANAGER");
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
            _root.schedule.Execute(OnScheduledRefresh).Every(250);

            RefreshSnapshot();
            RenderActiveSection();
            return _root;
        }

        public override void OnEnable()
        {
            base.OnEnable();
            EditorApplication.playModeStateChanged += OnPlayModeChange;
        }

        public override void OnDisable()
        {
            base.OnDisable();
            EditorApplication.playModeStateChanged -= OnPlayModeChange;
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
                (Section.Overview,  "Overview",  NexusEditorStyles.AccentBlue),
                (Section.Contexts,  "Contexts",  NexusEditorStyles.AccentGreen),
                (Section.Models,    "Models",    NexusEditorStyles.AccentYellow),
                (Section.Signals,   "Signals",   NexusEditorStyles.AccentPurple),
                (Section.Commands,  "Commands",  NexusEditorStyles.AccentOrange),
                (Section.Views,     "Views",     NexusEditorStyles.AccentBlue),
                (Section.Services,  "Services",  NexusEditorStyles.AccentGreen),
                (Section.Live,      "Live",      new Color(1f, 0.5f, 0.8f)),
                (Section.SignalTest,"Test",      new Color(1f, 0.4f, 0.4f)),
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

            // Scan assemblies for signal/command/models
            var scannedSignals = new HashSet<string>();
            var scannedCommands = new HashSet<(string cmd, string sig, string mode)>();
            var scannedModels = new HashSet<string>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

            s.SignalCount = scannedSignals.Count;
            s.SignalNames = scannedSignals.OrderBy(x => x).ToList();
            s.CommandCount = scannedCommands.Count;
            s.CommandEntries = scannedCommands.OrderBy(x => x.cmd).ToList();
            s.ModelCount = scannedModels.Count;
            s.ModelNames = scannedModels.OrderBy(x => x).ToList();

            // Views are harder to count statically — approximating
            var viewTypes = new HashSet<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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
            var statusText = new Label(playing ? "● ACTIVE — Play Mode" : "○ STANDBY — Editor Mode")
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

            AddStatCard(grid, "Contexts", s.ContextCount.ToString(), NexusEditorStyles.AccentGreen, "Active runtime contexts");
            AddStatCard(grid, "Models", s.ModelCount.ToString(), NexusEditorStyles.AccentYellow, "IReactiveModel implementations");
            AddStatCard(grid, "Signals", s.SignalCount.ToString(), NexusEditorStyles.AccentPurple, "Signal structs (ending in 'Signal')");
            AddStatCard(grid, "Commands", s.CommandCount.ToString(), NexusEditorStyles.AccentOrange, "Command bindings (attribute + fluent)");
            AddStatCard(grid, "Views", s.ViewCount.ToString(), NexusEditorStyles.AccentBlue, "View subclasses");
            AddStatCard(grid, "Services", s.ServiceCount.ToString(), NexusEditorStyles.AccentGreen, "INexusService implementations");
            AddStatCard(grid, "Scene Roots", s.RootCount.ToString(), NexusEditorStyles.DimText, "Root GameObjects in scene");

            _content.Add(grid);

            // Quick actions
            var actionsLabel = NexusEditorStyles.CreateSectionTitle("QUICK ACTIONS");
            actionsLabel.style.marginLeft = 15;
            actionsLabel.style.marginTop = 15;
            _content.Add(actionsLabel);

            var actionsRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 15, marginTop = 5, flexWrap = Wrap.Wrap } };
            var openWizard = NexusEditorStyles.CreateButton("Open Context Wizard", () => Window?.SwitchToPlugin("Wizard"), NexusEditorStyles.BtnBlue);
            actionsRow.Add(openWizard);

            var openTracer = NexusEditorStyles.CreateButton("Open Live Tracer", () => Window?.SwitchToPlugin("Tracer"), NexusEditorStyles.BtnTeal);
            openTracer.style.marginLeft = 5;
            actionsRow.Add(openTracer);

            var openGraph = NexusEditorStyles.CreateButton("Open Signal Graph", () => Window?.SwitchToPlugin("Graph"), NexusEditorStyles.BtnPurple);
            openGraph.style.marginLeft = 5;
            actionsRow.Add(openGraph);

            var refreshBtn = NexusEditorStyles.CreateButton("Refresh Now", () => { RefreshSnapshot(); RenderActiveSection(); }, NexusEditorStyles.BtnGray);
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
                header.Add(new Label(ctx.ScopeTag ?? "(no tag)")
                {
                    style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary) }
                });
                header.Add(NexusEditorStyles.CreatePill(ctx.Parent != null ? "Child" : "Root", NexusEditorStyles.BtnGray, NexusEditorStyles.TextSecondary));
                card.Add(header);

                card.Add(new Label($"Scope: {ctx.ScopeTag ?? "—"} | Parent: {ctx.Parent?.ScopeTag ?? "—"}")
                {
                    style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 4 }
                });

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
            header.Add(new Label("Command") { style = { width = 180, fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
            header.Add(new Label("Signal") { style = { width = 180, fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
            header.Add(new Label("Mode") { style = { width = 100, fontSize = 9, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
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
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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
            AddSectionHeader("LIVE MODEL INSPECTOR" + (playing ? "" : " (Play Mode only)"), new Color(1f, 0.5f, 0.8f));

            if (!playing)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("Enter Play Mode to inspect live model values."));
                return;
            }

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState("No active contexts."));
                return;
            }

            foreach (var ctx in contexts)
            {
                var ctxLabel = new Label($"Context: {ctx.ScopeTag ?? "(no tag)"}")
                {
                    style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentGreen), marginLeft = 15, marginTop = 8 }
                };
                _content.Add(ctxLabel);

                // Try to resolve IReactiveModel instances — we can't enumerate DI easily,
                // but we can show the context info and suggest using the Tracer
                var hint = NexusEditorStyles.CreateHint("Live model inspection resolves IReactiveModel instances. Open the Live Tracer for real-time signal monitoring.");
                hint.style.marginLeft = 15;
                _content.Add(hint);
            }
        }

        // ─── Signal Test Panel ─────────────────────────────────
        private string _testSignalName = "MySignal";
        private string _testSignalPayload = "test";

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

            var contextChoices = new List<string>();
            foreach (var ctx in contexts)
                contextChoices.Add(ctx.ScopeTag ?? "(no tag)");
            int defaultIdx = contextChoices.Count > 0 ? 0 : -1;

            var form = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    marginLeft = 15,
                    marginRight = 15,
                    marginTop = 10,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 10,
                    paddingBottom = 10,
                    borderTopLeftRadius = 6,
                    borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6,
                    borderBottomRightRadius = 6,
                }
            };

            form.Add(new Label("Manual Signal Fire") { style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary), marginBottom = 8 } });

            // Target context dropdown
            var ctxDropdown = new DropdownField("Target Context", contextChoices, defaultIdx >= 0 ? defaultIdx : 0);
            form.Add(ctxDropdown);

            // Signal name input
            var signalField = new TextField("Signal Name") { value = _testSignalName };
            signalField.RegisterValueChangedCallback(evt => _testSignalName = evt.newValue);
            form.Add(signalField);

            // Payload input
            var payloadField = new TextField("Payload (int)") { value = _testSignalPayload };
            payloadField.RegisterValueChangedCallback(evt => _testSignalPayload = evt.newValue);
            form.Add(payloadField);

            // Fire button
            var fireBtn = NexusEditorStyles.CreateButton("Fire Signal (FireAsync)", () =>
            {
                var scopeTag = ctxDropdown.value;
                var ctx = contexts.FirstOrDefault(c => c.ScopeTag == scopeTag);
                if (ctx == null) return;

                if (int.TryParse(_testSignalPayload, out int intVal))
                {
                    // This is a generic test — we fire a signal that matches common patterns
                    Debug.Log($"[Nexus Test] Would fire '{_testSignalName}' with payload {intVal} to context '{scopeTag}'. Use FireAsync for production.");
                }
                else
                {
                    Debug.Log($"[Nexus Test] Would fire '{_testSignalName}' with payload '{_testSignalPayload}' to context '{scopeTag}'.");
                }
            }, NexusEditorStyles.BtnPurple);
            fireBtn.style.marginTop = 8;
            form.Add(fireBtn);

            var hint = NexusEditorStyles.CreateHint("Tip: The Signal Explorer plugin (Window > Nexus > Dashboard > Explorer) supports full signal inspection.");
            hint.style.marginTop = 8;
            form.Add(hint);

            _content.Add(form);

            // Show available signals from snapshot
            AddSectionHeader("Available Signals", NexusEditorStyles.AccentPurple);
            foreach (var sig in _snapshot.SignalNames)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 15, marginTop = 2 } };
                row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentPurple, 5));
                row.Add(new Label(sig) { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.SignalBlue) } });
                _content.Add(row);
            }
        }
    }
}
