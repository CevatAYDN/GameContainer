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
    /// Context Inspector — Play Mode live inspector for all Nexus systems in any game.
    /// Shows the full DI container, resolved singletons, registered services, signal handlers,
    /// and allows firing test signals into any active context.
    /// </summary>
    public class ContextInspectorPlugin : NexusEditorPlugin
    {
        public override string Id => "ContextInspector";
        public override string DisplayName => NexusLang.Get("tab_contextinspector");
        public override int Order => 7;

        // ── State ─────────────────────────────────────────────────
        private IContext _selectedContext;
        private string _searchFilter = "";
        private enum InspectorTab { Overview, Bindings, Singletons, Services, Signals, Extensions, FireSignal }
        private InspectorTab _activeTab = InspectorTab.Overview;
        private readonly Dictionary<InspectorTab, Button> _tabButtons = new();

        // Signal fire state
        private Type _fireSignalType;
        private object _fireSignalInstance;
        private FieldInfo[] _fireSignalFields = Array.Empty<FieldInfo>();

        // ── UI refs ───────────────────────────────────────────────
        private VisualElement _view;
        private DropdownField _contextDropdown;
        private VisualElement _tabBar;
        private ScrollView _contentScroll;
        private VisualElement _content;
        private Label _playModeWarning;
        private IVisualElementScheduledItem _refreshSchedule;
        private double _lastRefresh;

        // ─────────────────────────────────────────────────────────
        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("ci_title"));
            _view.Add(toolbar);

            // Play mode warning banner
            _playModeWarning = new Label(NexusLang.Get("ci_playmode_warning"))
            {
                style =
                {
                    fontSize = 11,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.AccentYellow),
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBgYellow),
                    paddingTop = 8, paddingBottom = 8, paddingLeft = 12,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    display = Application.isPlaying ? DisplayStyle.None : DisplayStyle.Flex
                }
            };
            _view.Add(_playModeWarning);

            // Context selector bar
            BuildContextSelector();

            // Tab bar
            _tabBar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    backgroundColor = new StyleColor(NexusEditorStyles.ToolbarBg),
                    borderBottomWidth = 1,
                    borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor),
                    flexWrap = Wrap.Wrap
                }
            };
            foreach (InspectorTab tab in Enum.GetValues(typeof(InspectorTab)))
                _tabBar.Add(BuildTabButton(tab));
            _view.Add(_tabBar);

            // Search bar
            var searchBar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    paddingLeft = 12, paddingRight = 12,
                    paddingTop = 6, paddingBottom = 6,
                    borderBottomWidth = 1,
                    borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor),
                    alignItems = Align.Center
                }
            };
            var searchLabel = new Label("🔍") { style = { marginRight = 4 } };
            searchBar.Add(searchLabel);
            var searchField = new TextField { value = _searchFilter, style = { flexGrow = 1 } };
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue?.Trim() ?? "";
                RenderContent();
            });
            searchBar.Add(searchField);
            _view.Add(searchBar);

            // Content
            _contentScroll = new ScrollView { style = { flexGrow = 1 } };
            _content = new VisualElement
            {
                style = { paddingLeft = 16, paddingRight = 16, paddingTop = 12, paddingBottom = 12 }
            };
            _contentScroll.Add(_content);
            _view.Add(_contentScroll);

            // Subscribe to context events
            NexusRuntime.OnContextRegistered   -= OnContextsChanged;
            NexusRuntime.OnContextUnregistered -= OnContextsChanged;
            NexusRuntime.OnContextRegistered   += OnContextsChanged;
            NexusRuntime.OnContextUnregistered += OnContextsChanged;

            RefreshContextDropdown();
            RenderContent();

            _refreshSchedule?.Pause();
            _refreshSchedule = _view.schedule.Execute(OnScheduled).Every(500);

            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            NexusRuntime.OnContextRegistered   -= OnContextsChanged;
            NexusRuntime.OnContextUnregistered -= OnContextsChanged;
            base.OnDisable();
        }

        public override void OnUpdate()
        {
            // Update play mode warning visibility
            if (_playModeWarning == null) return;
            _playModeWarning.style.display = Application.isPlaying
                ? DisplayStyle.None : DisplayStyle.Flex;

            if (!Application.isPlaying) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefresh > 0.5)
            {
                _lastRefresh = now;
                RefreshContextDropdown();
                if (_activeTab == InspectorTab.Overview || _activeTab == InspectorTab.Singletons)
                    RenderContent();
            }
        }

        public override IReadOnlyList<(string Label, Action Action, Color Color)> GetContextActions()
            => new List<(string, Action, Color)>
            {
                (NexusLang.Get("ci_action_refresh"),     () => { RefreshContextDropdown(); RenderContent(); }, NexusEditorStyles.BtnGray),
                (NexusLang.Get("ci_action_copy_report"), CopyContextReport, NexusEditorStyles.BtnBlue),
            };

        // ── Context selector ──────────────────────────────────────

        private void BuildContextSelector()
        {
            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    paddingLeft = 12, paddingRight = 12,
                    paddingTop = 6, paddingBottom = 6,
                    borderBottomWidth = 1,
                    borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor)
                }
            };

            bar.Add(new Label(NexusLang.Get("ci_context_label"))
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), marginRight = 8, minWidth = 60 }
            });

            _contextDropdown = new DropdownField { choices = new List<string> { NexusLang.Get("ci_dropdown_none") }, value = NexusLang.Get("ci_dropdown_none") };
            _contextDropdown.style.flexGrow = 1;
            _contextDropdown.RegisterValueChangedCallback(evt =>
            {
                SelectContextByTag(evt.newValue);
                RenderContent();
            });
            bar.Add(_contextDropdown);

            var refreshBtn = NexusEditorStyles.CreateButton("↺", () =>
            {
                RefreshContextDropdown();
                RenderContent();
            }, NexusEditorStyles.BtnGray);
            refreshBtn.style.width = 24;
            refreshBtn.style.height = 20;
            refreshBtn.style.marginLeft = 4;
            bar.Add(refreshBtn);

            _view.Add(bar);
        }

        private void RefreshContextDropdown()
        {
            var contexts = NexusRuntime.ActiveContexts;
            var choices = new List<string> { NexusLang.Get("ci_none_editmode") };

            if (contexts != null)
                foreach (var ctx in contexts)
                    choices.Add(FormatContextLabel(ctx));

            _contextDropdown.choices = choices;

            // Reselect if still valid
            if (_selectedContext != null)
            {
                string lbl = FormatContextLabel(_selectedContext);
                if (choices.Contains(lbl))
                    _contextDropdown.SetValueWithoutNotify(lbl);
                else
                {
                    _selectedContext = null;
                    _contextDropdown.SetValueWithoutNotify(choices[0]);
                }
            }
            else if (choices.Count > 1)
            {
                // Auto-select first real context
                _selectedContext = NexusRuntime.ActiveContexts?.FirstOrDefault();
                _contextDropdown.SetValueWithoutNotify(choices.Count > 1 ? choices[1] : choices[0]);
            }
        }

        private void SelectContextByTag(string label)
        {
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null) { _selectedContext = null; return; }
            _selectedContext = contexts.FirstOrDefault(c => FormatContextLabel(c) == label);
        }

        private static string FormatContextLabel(IContext ctx)
        {
            if (ctx == null) return NexusLang.Get("ci_null_label");
            return ctx.ScopeTag ?? ctx.GetType().Name;
        }

        // ── Tab buttons ───────────────────────────────────────────

        private Button BuildTabButton(InspectorTab tab)
        {
            var labels = new Dictionary<InspectorTab, string>
            {
                { InspectorTab.Overview,   NexusLang.Get("ci_tab_overview") },
                { InspectorTab.Bindings,   NexusLang.Get("ci_tab_bindings") },
                { InspectorTab.Singletons, NexusLang.Get("ci_tab_singletons") },
                { InspectorTab.Services,   NexusLang.Get("ci_tab_services") },
                { InspectorTab.Signals,    NexusLang.Get("ci_tab_signals") },
                { InspectorTab.Extensions, NexusLang.Get("ci_tab_extensions") },
                { InspectorTab.FireSignal, NexusLang.Get("ci_tab_firesignal") },
            };

            var btn = new Button(() =>
            {
                _activeTab = tab;
                HighlightTabs();
                RenderContent();
            }) { text = labels.GetValueOrDefault(tab, tab.ToString()) };

            btn.style.fontSize = 10;
            btn.style.paddingLeft = btn.style.paddingRight = 12;
            btn.style.paddingTop = btn.style.paddingBottom = 6;
            btn.style.borderTopWidth = btn.style.borderBottomWidth =
            btn.style.borderLeftWidth = btn.style.borderRightWidth = 0;
            StyleTabBtn(btn, tab == _activeTab);

            _tabButtons[tab] = btn;
            return btn;
        }

        private void HighlightTabs()
        {
            foreach (var kv in _tabButtons)
                StyleTabBtn(kv.Value, kv.Key == _activeTab);
        }

        private static void StyleTabBtn(Button btn, bool active)
        {
            btn.style.backgroundColor = new StyleColor(active ? NexusEditorStyles.HighlightBg : Color.clear);
            btn.style.color           = new StyleColor(active ? NexusEditorStyles.AccentBlue : NexusEditorStyles.TextPrimary);
            btn.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        // ── Content rendering ─────────────────────────────────────

        private void RenderContent()
        {
            _content.Clear();

            if (!Application.isPlaying)
            {
                _content.Add(new Label(NexusLang.Get("ci_playmode_prompt"))
                {
                    style = { color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 20, unityTextAlign = TextAnchor.MiddleCenter }
                });
                return;
            }

            if (_selectedContext == null)
            {
                _content.Add(new Label(NexusLang.Get("ci_select_context"))
                {
                    style = { color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 20, unityTextAlign = TextAnchor.MiddleCenter }
                });
                return;
            }

            switch (_activeTab)
            {
                case InspectorTab.Overview:    RenderOverview();   break;
                case InspectorTab.Bindings:    RenderBindings();   break;
                case InspectorTab.Singletons:  RenderSingletons(); break;
                case InspectorTab.Services:    RenderServices();   break;
                case InspectorTab.Signals:     RenderSignals();    break;
                case InspectorTab.Extensions:  RenderExtensions(); break;
                case InspectorTab.FireSignal:  RenderFireSignal(); break;
            }
        }

        // ── Overview tab ──────────────────────────────────────────

        private void RenderOverview()
        {
            AddSectionTitle(NexusLang.Get("ci_overview_title"));

            var ctx = _selectedContext;
            var concrete   = _selectedContext as Context;
            var bindings   = NexusEditorDataProvider.GetAllBindings(ctx);
            var singletons = NexusEditorDataProvider.GetResolvedSingletons(ctx);
            var handlers   = ctx.SignalBus?.RegisteredHandlers;
            var plugins    = concrete?.PluginsReadOnlyCopy;

            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_tag"),          ctx.ScopeTag ?? NexusLang.Get("ci_no_tag"), NexusEditorStyles.AccentBlue));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_type"),         ctx.GetType().Name, NexusEditorStyles.TextPrimary));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_parent"),       ctx.Parent != null ? (ctx.Parent.ScopeTag ?? ctx.Parent.GetType().Name) : NexusLang.Get("ci_none")));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_di_bindings"),  $"{bindings.Count}", NexusEditorStyles.AccentGreen));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_singletons"),   $"{singletons.Count}", NexusEditorStyles.AccentBlue));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_signals"),      $"{handlers?.Count ?? 0}", NexusEditorStyles.AccentPurple));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_plugins"),      $"{plugins?.Count ?? 0}", NexusEditorStyles.AccentOrange));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_has_interceptors"), (concrete?.HasInterceptors ?? false).ToString(), NexusEditorStyles.TextSecondary));

            // Child contexts
            var allContexts = NexusRuntime.ActiveContexts;
            var children = allContexts?.Where(c => c.Parent == ctx).ToList();
            if (children?.Count > 0)
            {
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_child_contexts"), $"{children.Count}", NexusEditorStyles.AccentGreen));
                foreach (var child in children)
                {
                    _content.Add(NexusVisualization.CreateStatRow("  ↳", child.ScopeTag ?? child.GetType().Name, NexusEditorStyles.TextSecondary));
                }
            }

            // Plugins list
            if (plugins?.Count > 0)
            {
                _content.Add(MakeSpacer(8));
                AddSectionTitle(NexusLang.Get("ci_runtime_plugins"));
                foreach (var (plugin, _) in plugins)
                {
                    _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_plugin"), plugin.GetType().Name, NexusEditorStyles.AccentOrange));
                }
            }

            // Signal queues (HybridQueue) — live depth + cumulative throughput.
            var queue = concrete?.HybridQueue;
            if (queue != null)
            {
                _content.Add(MakeSpacer(8));
                AddSectionTitle(NexusLang.Get("ci_signal_queues"));
                int tsDepth = queue.ThreadSafeQueueDepth;
                int nfDepth = queue.NextFrameQueueDepth;
                long enq = queue.TotalEnqueued;
                long drn = queue.TotalDrained;
                long pending = enq - drn;
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_ts_depth"), $"{tsDepth}", tsDepth > 0 ? NexusEditorStyles.AccentYellow : NexusEditorStyles.TextSecondary));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_nf_depth"),  $"{nfDepth}", nfDepth > 0 ? NexusEditorStyles.AccentYellow : NexusEditorStyles.TextSecondary));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_total_enqueued"),    $"{enq}", NexusEditorStyles.AccentBlue));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_total_drained"),     $"{drn}", NexusEditorStyles.AccentGreen));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_pending"), $"{pending}", pending > 0 ? NexusEditorStyles.AccentOrange : NexusEditorStyles.TextSecondary));
            }

            // Command pools (CommandPoolManager) — live utilization + reuse ratio (G-4).
            var poolStats = concrete?.PoolManager?.GetPoolStatsSnapshot();
            if (poolStats != null && poolStats.Count > 0)
            {
                _content.Add(MakeSpacer(8));
                AddSectionTitle(NexusLang.Get("ci_command_pools"));

                int available = 0;
                long totalGets = 0, totalCreated = 0, totalReturns = 0, totalDiscarded = 0;
                for (int i = 0; i < poolStats.Count; i++)
                {
                    var s = poolStats[i];
                    available     += s.Available;
                    totalGets     += s.TotalGets;
                    totalCreated  += s.TotalCreated;
                    totalReturns  += s.TotalReturns;
                    totalDiscarded += s.TotalDiscarded;
                }
                float reuseRatio = totalGets > 0 ? (float)(totalGets - totalCreated) / totalGets : 0f;

                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_pooled_types"),     $"{poolStats.Count}", NexusEditorStyles.AccentBlue));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_available_now"),    $"{available}", available > 0 ? NexusEditorStyles.AccentGreen : NexusEditorStyles.TextSecondary));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_total_gets"),       $"{totalGets}", NexusEditorStyles.TextPrimary));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_total_created"),    $"{totalCreated}", NexusEditorStyles.AccentOrange));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_total_returns"),    $"{totalReturns}", NexusEditorStyles.AccentGreen));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_total_discarded"),  $"{totalDiscarded}", totalDiscarded > 0 ? NexusEditorStyles.AccentYellow : NexusEditorStyles.TextSecondary));
                _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_reuse_ratio"),      $"{reuseRatio:P0}", reuseRatio >= 0.5f ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentOrange));

                // Per-type breakdown (compact).
                for (int i = 0; i < poolStats.Count; i++)
                {
                    var s = poolStats[i];
                    string typeName = s.CommandType != null ? s.CommandType.Name : "(unknown)";
                    _content.Add(NexusVisualization.CreateStatRow($"  {typeName}", $"{s.Available}/{s.MaxSize}  ·  reuse {s.ReuseRatio:P0}", NexusEditorStyles.TextSecondary));
                }
            }
        }

        // ── Extensions tab (interceptor / decorator pipeline) ─────

        private void RenderExtensions()
        {
            AddSectionTitle(NexusLang.Get("ci_ext_pipeline"));

            var concrete = _selectedContext as Context;
            var plugins = concrete?.PluginsReadOnlyCopy;

            if (concrete == null || plugins == null || plugins.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_ext_empty"));
                return;
            }

            // Aggregate pipeline summary across all plugins on this context.
            int totalInterceptors = 0, totalDecorators = 0, totalSerializers = 0, totalSinks = 0;
            foreach (var (_, pctx) in plugins)
            {
                if (pctx == null) continue;
                totalInterceptors += pctx.Interceptors?.Count ?? 0;
                totalDecorators   += pctx.Decorators?.Count ?? 0;
                totalSerializers  += pctx.Serializers?.Count ?? 0;
                totalSinks        += pctx.TraceSinks?.Count ?? 0;
            }

            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_plugins"),             $"{plugins.Count}", NexusEditorStyles.AccentOrange));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_signal_interceptors"), $"{totalInterceptors}", totalInterceptors > 0 ? NexusEditorStyles.AccentGreen : NexusEditorStyles.TextSecondary));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_command_decorators"),  $"{totalDecorators}", totalDecorators > 0 ? NexusEditorStyles.AccentGreen : NexusEditorStyles.TextSecondary));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_model_serializers"),   $"{totalSerializers}", NexusEditorStyles.TextSecondary));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_trace_sinks"),         $"{totalSinks}", NexusEditorStyles.TextSecondary));

            _content.Add(MakeSpacer(10));

            foreach (var (plugin, pctx) in plugins)
            {
                if (plugin == null) continue;
                _content.Add(BuildPluginCard(plugin, pctx));
                _content.Add(MakeSpacer(8));
            }
        }

        private VisualElement BuildPluginCard(INexusPlugin plugin, PluginContext pctx)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);

            // Header: plugin name + version + declared capability pills.
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, marginBottom = 6 } };
            var manifest = plugin.Manifest;
            header.Add(new Label(manifest?.Name ?? plugin.GetType().Name)
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentOrange) }
            });
            if (!string.IsNullOrEmpty(manifest?.Version))
                header.Add(NexusEditorStyles.CreatePill($"v{manifest.Version}", NexusEditorStyles.CardBgAlt, NexusEditorStyles.TextSecondary));
            if (manifest != null)
            {
                foreach (PluginCapabilities cap in Enum.GetValues(typeof(PluginCapabilities)))
                {
                    if (cap == PluginCapabilities.None) continue;
                    if ((manifest.Capabilities & cap) != 0)
                        header.Add(NexusEditorStyles.CreatePill(cap.ToString(), NexusEditorStyles.CardBgBlue, NexusEditorStyles.AccentBlueText));
                }
            }
            card.Add(header);

            card.Add(new Label($"Type: {plugin.GetType().FullName}")
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginBottom = 4, whiteSpace = WhiteSpace.Normal }
            });

            if (pctx == null)
            {
                card.Add(new Label(NexusLang.Get("ci_no_plugin_context")) { style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
                return card;
            }

            // Decorators are numbered because their list order IS the execution order.
            AddPipelineList(card, NexusLang.Get("ci_signal_interceptors"), pctx.Interceptors, NexusEditorStyles.AccentGreen, ordered: false);
            AddPipelineList(card, NexusLang.Get("ci_command_decorators_order"), pctx.Decorators, NexusEditorStyles.AccentPurple, ordered: true);
            AddPipelineList(card, NexusLang.Get("ci_model_serializers"), pctx.Serializers, NexusEditorStyles.AccentBlue, ordered: false);
            AddPipelineList(card, NexusLang.Get("ci_trace_sinks"), pctx.TraceSinks, NexusEditorStyles.TextSecondary, ordered: false);

            return card;
        }

        private void AddPipelineList<T>(VisualElement card, string label, IReadOnlyList<T> items, Color accent, bool ordered)
        {
            int count = items?.Count ?? 0;
            if (count == 0) return;

            card.Add(new Label($"{label} ({count})")
            {
                style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(accent), marginTop = 4, marginBottom = 2 }
            });

            for (int i = 0; i < count; i++)
            {
                var prefix = ordered ? $"  {i + 1}. " : "  • ";
                card.Add(new Label($"{prefix}{items[i].GetType().Name}")
                {
                    style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextPrimary), whiteSpace = WhiteSpace.Normal, paddingLeft = 4 }
                });
            }
        }

        // ── Bindings tab ──────────────────────────────────────────

        private void RenderBindings()
        {
            AddSectionTitle(NexusLang.Get("ci_di_bindings_title"));

            var bindings = NexusEditorDataProvider.GetAllBindings(_selectedContext);
            if (bindings.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_no_bindings"));
                return;
            }

            var filtered = bindings
                .Where(kv => string.IsNullOrEmpty(_searchFilter)
                    || kv.Key.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    || kv.Value.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(kv => kv.Key.Name)
                .ToList();

            var table = NexusVisualization.CreateDataTable(
                new[] { (NexusLang.Get("ci_col_interface_key"), 0.5f), (NexusLang.Get("ci_col_concrete_type"), 0.5f) },
                filtered.Select(kv => new[] { kv.Key.Name, kv.Value.Name })
            );
            _content.Add(table);

            _content.Add(new Label(string.Format(NexusLang.Get("ci_total_bindings"), bindings.Count))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
        }

        // ── Singletons tab ────────────────────────────────────────

        private void RenderSingletons()
        {
            AddSectionTitle(NexusLang.Get("ci_resolved_singletons_title"));

            var singletons = NexusEditorDataProvider.GetResolvedSingletons(_selectedContext);
            if (singletons.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_no_singletons"));
                return;
            }

            var filtered = singletons
                .Where(s => string.IsNullOrEmpty(_searchFilter)
                    || s.GetType().Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(s => s.GetType().Name)
                .ToList();

            foreach (var singleton in filtered)
            {
                var type = singleton.GetType();
                var card = new VisualElement
                {
                    style =
                    {
                        backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                        borderTopLeftRadius = 4, borderTopRightRadius = 4,
                        borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                        paddingTop = 8, paddingBottom = 8, paddingLeft = 8, paddingRight = 8,
                        marginBottom = 6
                    }
                };

                var hdr = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
                hdr.Add(new Label(type.Name)
                {
                    style =
                    {
                        fontSize = 11,
                        unityFontStyleAndWeight = FontStyle.Bold,
                        color = new StyleColor(NexusEditorStyles.AccentBlue),
                        flexGrow = 1
                    }
                });

                string ns = type.Namespace ?? "";
                if (!string.IsNullOrEmpty(ns))
                    hdr.Add(new Label(ns) { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText) } });

                card.Add(hdr);

                // Show interfaces implemented
                var interfaces = type.GetInterfaces()
                    .Where(i => i.Namespace?.StartsWith("Nexus") == true || i.Namespace?.StartsWith("System") != true)
                    .Select(i => i.Name)
                    .Take(4)
                    .ToList();
                if (interfaces.Count > 0)
                {
                    card.Add(new Label(string.Format(NexusLang.Get("ci_implements"), string.Join(", ", interfaces)))
                    {
                        style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary) }
                    });
                }

                // Show public property values (read-only)
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .Take(6);
                foreach (var prop in props)
                {
                    try
                    {
                        var val = prop.GetValue(singleton);
                        var valStr = val?.ToString() ?? "null";
                        if (valStr.Length > 60) valStr = valStr[..57] + "...";
                        card.Add(NexusVisualization.CreateStatRow($"  .{prop.Name}", valStr));
                    }
                    catch { }
                }

                _content.Add(card);
            }

            _content.Add(new Label(string.Format(NexusLang.Get("ci_total_singletons"), singletons.Count))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 4 }
            });
        }

        // ── Services tab ──────────────────────────────────────────

        private void RenderServices()
        {
            AddSectionTitle(NexusLang.Get("ci_registered_services_title"));

            var serviceTypes = NexusEditorDataProvider.GetLiveServiceTypes(_selectedContext);
            if (serviceTypes.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_no_services"));
                return;
            }

            var rows = serviceTypes
                .Where(t => string.IsNullOrEmpty(_searchFilter)
                    || t.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.Name)
                .Select(t =>
                {
                    var inst = NexusEditorDataProvider.TryGetServiceInstance(_selectedContext, t);
                    return new[] { t.Name, inst?.GetType().Name ?? NexusLang.Get("ci_not_resolved"), inst != null ? "✓" : "—" };
                });

            _content.Add(NexusVisualization.CreateDataTable(
                new[] { (NexusLang.Get("ci_col_service_type"), 0.45f), (NexusLang.Get("ci_col_concrete"), 0.4f), (NexusLang.Get("ci_col_resolved"), 0.15f) },
                rows
            ));

            _content.Add(new Label(string.Format(NexusLang.Get("ci_total_services"), serviceTypes.Count))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
        }

        // ── Signals tab ───────────────────────────────────────────

        private void RenderSignals()
        {
            AddSectionTitle(NexusLang.Get("ci_signal_handlers_title"));

            var handlers = _selectedContext.SignalBus?.RegisteredHandlers;
            if (handlers == null || handlers.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_no_signal_handlers"));
                return;
            }

            var rows = handlers
                .Where(kv => string.IsNullOrEmpty(_searchFilter)
                    || kv.Key.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(kv => kv.Key.Name)
                .SelectMany(kv => kv.Value.Select(h => new[]
                {
                    kv.Key.Name,
                    h.CommandType?.Name ?? "—",
                    h.Mode.ToString()
                }));

            _content.Add(NexusVisualization.CreateDataTable(
                new[] { (NexusLang.Get("ci_col_signal"), 0.4f), (NexusLang.Get("ci_col_command_handler"), 0.4f), (NexusLang.Get("ci_col_mode"), 0.2f) },
                rows
            ));

            _content.Add(new Label(string.Format(NexusLang.Get("ci_total_signals_count"), handlers.Count))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
        }

        // ── Fire Signal tab ───────────────────────────────────────

        private void RenderFireSignal()
        {
            AddSectionTitle(NexusLang.Get("ci_fire_test_signal_title"));

            if (_selectedContext == null)
            {
                AddEmpty(NexusLang.Get("ci_select_context_first"));
                return;
            }

            // Signal type dropdown
            var allSignalTypes = GetAvailableSignalTypes();
            if (allSignalTypes.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_no_signal_types"));
                return;
            }

            var typeNames = allSignalTypes.Select(t => t.FullName ?? t.Name).ToList();
            var dropdown = new DropdownField(NexusLang.Get("ci_signal_type"), typeNames, 0);
            dropdown.style.marginBottom = 8;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                _fireSignalType = allSignalTypes.FirstOrDefault(t =>
                    (t.FullName ?? t.Name) == evt.newValue);
                BuildSignalForm();
            });
            _content.Add(dropdown);

            if (_fireSignalType == null && allSignalTypes.Count > 0)
                _fireSignalType = allSignalTypes[0];

            BuildSignalForm();
        }

        private VisualElement _signalFormContainer;

        private void BuildSignalForm()
        {
            // Remove old form if present
            if (_signalFormContainer != null && _content.Contains(_signalFormContainer))
                _content.Remove(_signalFormContainer);

            _signalFormContainer = new VisualElement();

            if (_fireSignalType == null)
            {
                _signalFormContainer.Add(new Label(NexusLang.Get("ci_select_signal_type")));
                _content.Add(_signalFormContainer);
                return;
            }

            try
            {
                _fireSignalInstance = Activator.CreateInstance(_fireSignalType);
            }
            catch
            {
                _signalFormContainer.Add(new Label(NexusLang.Get("ci_cannot_instantiate"))
                {
                    style = { color = new StyleColor(NexusEditorStyles.AccentRed) }
                });
                _content.Add(_signalFormContainer);
                return;
            }

            _fireSignalFields = _fireSignalType.GetFields(BindingFlags.Public | BindingFlags.Instance);

            if (_fireSignalFields.Length == 0)
            {
                _signalFormContainer.Add(new Label(NexusLang.Get("ci_no_public_fields"))
                {
                    style = { color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 8, fontSize = 9 }
                });
            }
            else
            {
                _signalFormContainer.Add(new Label(NexusLang.Get("ci_fill_fields"))
                {
                    style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 4 }
                });

                foreach (var field in _fireSignalFields)
                {
                    var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
                    row.Add(new Label(field.Name + ":")
                    {
                        style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), minWidth = 100 }
                    });

                    var fieldRef = field; // capture
                    if (field.FieldType == typeof(string))
                    {
                        var tf = new TextField { value = (string)field.GetValue(_fireSignalInstance) ?? "" };
                        tf.style.flexGrow = 1;
                        tf.RegisterValueChangedCallback(evt => fieldRef.SetValue(_fireSignalInstance, evt.newValue));
                        row.Add(tf);
                    }
                    else if (field.FieldType == typeof(int))
                    {
                        var tf = new IntegerField { value = (int)field.GetValue(_fireSignalInstance) };
                        tf.style.flexGrow = 1;
                        tf.RegisterValueChangedCallback(evt => fieldRef.SetValue(_fireSignalInstance, evt.newValue));
                        row.Add(tf);
                    }
                    else if (field.FieldType == typeof(float))
                    {
                        var tf = new FloatField { value = (float)field.GetValue(_fireSignalInstance) };
                        tf.style.flexGrow = 1;
                        tf.RegisterValueChangedCallback(evt => fieldRef.SetValue(_fireSignalInstance, evt.newValue));
                        row.Add(tf);
                    }
                    else if (field.FieldType == typeof(bool))
                    {
                        var tf = new Toggle { value = (bool)field.GetValue(_fireSignalInstance) };
                        tf.RegisterValueChangedCallback(evt => fieldRef.SetValue(_fireSignalInstance, evt.newValue));
                        row.Add(tf);
                    }
                    else
                    {
                        row.Add(new Label(string.Format(NexusLang.Get("ci_not_editable"), field.FieldType.Name))
                        {
                            style = { color = new StyleColor(NexusEditorStyles.DimText), fontSize = 9 }
                        });
                    }

                    _signalFormContainer.Add(row);
                }
            }

            // Result label
            var resultLabel = new Label("")
            {
                style =
                {
                    fontSize = 10, marginTop = 8, marginBottom = 8,
                    whiteSpace = WhiteSpace.Normal
                }
            };
            _signalFormContainer.Add(resultLabel);

            // Fire button
            var fireBtn = new Button(() =>
            {
                if (_selectedContext?.SignalBus == null || _fireSignalInstance == null)
                {
                    resultLabel.text = NexusLang.Get("ci_err_no_context_signal");
                    resultLabel.style.color = new StyleColor(NexusEditorStyles.AccentRed);
                    return;
                }
                try
                {
                    // Use reflection to call SignalBus.Fire<T>(T signal)
                    var fireMethod = _selectedContext.SignalBus.GetType()
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Fire" && m.IsGenericMethod &&
                                             m.GetParameters().Length == 1);
                    if (fireMethod != null)
                    {
                        var generic = fireMethod.MakeGenericMethod(_fireSignalType);
                        generic.Invoke(_selectedContext.SignalBus, new[] { _fireSignalInstance });
                        resultLabel.text = string.Format(NexusLang.Get("ci_fired_ok"), _fireSignalType.Name, DateTime.Now.ToString("HH:mm:ss.fff"));
                        resultLabel.style.color = new StyleColor(NexusEditorStyles.AccentGreen);
                    }
                    else
                    {
                        resultLabel.text = NexusLang.Get("ci_err_fire_notfound");
                        resultLabel.style.color = new StyleColor(NexusEditorStyles.AccentRed);
                    }
                }
                catch (Exception ex)
                {
                    resultLabel.text = string.Format(NexusLang.Get("ci_err_generic"), ex.InnerException?.Message ?? ex.Message);
                    resultLabel.style.color = new StyleColor(NexusEditorStyles.AccentRed);
                    Debug.LogException(ex);
                }
            })
            {
                text = string.Format(NexusLang.Get("ci_fire_btn"), _fireSignalType?.Name ?? "Signal"),
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.BtnRed),
                    color = Color.white,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    paddingTop = 6, paddingBottom = 6,
                    paddingLeft = 16, paddingRight = 16,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4
                }
            };
            _signalFormContainer.Add(fireBtn);

            _content.Add(_signalFormContainer);
        }

        // ── Helpers ───────────────────────────────────────────────

        private List<Type> GetAvailableSignalTypes()
        {
            var result = new List<Type>();
            if (_selectedContext?.SignalBus?.RegisteredHandlers is { } handlers)
            {
                foreach (var signalType in handlers.Keys)
                    result.Add(signalType);
            }
            return result.OrderBy(t => t.Name).ToList();
        }

        private void AddSectionTitle(string text)
        {
            _content.Add(new Label(text)
            {
                style =
                {
                    fontSize = 12,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.AccentBlue),
                    marginBottom = 8
                }
            });
        }

        private void AddEmpty(string msg)
        {
            _content.Add(new Label(msg)
            {
                style =
                {
                    color = new StyleColor(NexusEditorStyles.TextSecondary),
                    marginTop = 16,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    fontSize = 10
                }
            });
        }

        private static VisualElement MakeSpacer(float h)
            => new() { style = { height = h } };

        private void OnContextsChanged(IContext _)
        {
            RefreshContextDropdown();
            RenderContent();
        }

        private void OnScheduled()
        {
            if (Application.isPlaying && (_activeTab == InspectorTab.Singletons || _activeTab == InspectorTab.Extensions || _activeTab == InspectorTab.Overview))
                RenderContent();
        }

        private void CopyContextReport()
        {
            if (_selectedContext == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Nexus Context Report: {_selectedContext.ScopeTag} ===");
            sb.AppendLine($"Type: {_selectedContext.GetType().Name}");
            sb.AppendLine($"Parent: {_selectedContext.Parent?.ScopeTag ?? "none"}");

            var bindings = NexusEditorDataProvider.GetAllBindings(_selectedContext);
            sb.AppendLine($"\n── DI Bindings ({bindings.Count}) ──");
            foreach (var kv in bindings.OrderBy(k => k.Key.Name))
                sb.AppendLine($"  {kv.Key.Name} → {kv.Value.Name}");

            var singletons = NexusEditorDataProvider.GetResolvedSingletons(_selectedContext);
            sb.AppendLine($"\n── Singletons ({singletons.Count}) ──");
            foreach (var s in singletons.OrderBy(s => s.GetType().Name))
                sb.AppendLine($"  {s.GetType().Name}");

            var handlers = _selectedContext.SignalBus?.RegisteredHandlers;
            sb.AppendLine($"\n── Signal Handlers ({handlers?.Count ?? 0}) ──");
            if (handlers != null)
                foreach (var kv in handlers.OrderBy(k => k.Key.Name))
                    foreach (var h in kv.Value)
                        sb.AppendLine($"  {kv.Key.Name} → {h.CommandType?.Name} ({h.Mode})");

            GUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log("[Nexus] Context report copied to clipboard.");
        }
    }
}
