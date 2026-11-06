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
    /// The Live section's context view. The left pane lists the running contexts as a parent/child tree;
    /// the right pane shows the selected context: overview, its singletons with live, editable fields,
    /// bindings, services, signal handlers, extensions and a fire form. Play Mode only: the window
    /// rebuilds the view when Play Mode starts or ends.
    /// </summary>
    public class ContextInspectorPlugin : NexusEditorPlugin
    {
        public override string Id => "ContextInspector";
        public override string DisplayName => NexusLang.Get("tab_contextinspector");
        public override int Order => 2;
        public override string Category => "cat_live";

        private enum InspectorTab { Overview, Singletons, Bindings, Services, Signals, Extensions, FireSignal }

        private IContext _selectedContext;
        private InspectorTab _activeTab = InspectorTab.Overview;
        private string _searchFilter = "";
        private Type _fireSignalType;
        private readonly Dictionary<InspectorTab, Button> _tabButtons = new();
        private readonly HashSet<string> _expandedSingletons = new();
        private readonly List<ValueTracker> _trackers = new();

        private VisualElement _view;
        private ScrollView _tree;
        private VisualElement _content;
        private double _lastRefresh;

        // A shown member value; the view re-reads it on each update and flashes the row when it changed.
        private sealed class ValueTracker
        {
            public object Instance;
            public MemberInfo Member;
            public VisualElement Element;
            public object LastValue;
            public float FlashTimeRemaining;
            public bool Failed; // its getter threw: warned once, no longer read, so the 200 ms update does not warn again (#315)
        }

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };
            _view.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("ci_title")));
            _tabButtons.Clear();
            _trackers.Clear();
            _tree = null;
            _content = null;

            if (!Application.isPlaying)
            {
                var offline = new VisualElement { style = { paddingLeft = 12, paddingRight = 12, paddingTop = 10 } };
                offline.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("ci_playmode_prompt")));
                int roots = NexusEditorDataProvider.GetSceneRoots().Length;
                if (roots > 0)
                    NexusEditorStyles.CreateInfoCard(offline, string.Format(NexusLang.Get("hierarchy_roots_detected"), roots),
                        NexusEditorStyles.AccentYellow, NexusEditorStyles.CardBgYellow, string.Format(NexusLang.Get("hierarchy_roots_desc"), roots));
                _view.Add(offline);
                return _view;
            }

            var split = new TwoPaneSplitView(0, 240, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };

            _tree = new ScrollView { style = { paddingLeft = 8, paddingRight = 8, paddingTop = 8, paddingBottom = 8 } };
            _tree.style.borderRightWidth = 1;
            _tree.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            split.Add(_tree);

            var right = new VisualElement { style = { flexGrow = 1 } };
            var tabBar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    backgroundColor = new StyleColor(NexusEditorStyles.ToolbarBg),
                    borderBottomWidth = 1,
                    borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor)
                }
            };
            foreach (InspectorTab tab in Enum.GetValues(typeof(InspectorTab)))
                tabBar.Add(BuildTabButton(tab));
            right.Add(tabBar);

            var searchField = new TextField { value = _searchFilter, style = { marginLeft = 12, marginRight = 12, marginTop = 6, marginBottom = 6 } };
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue?.Trim() ?? "";
                RenderContent();
            });
            right.Add(searchField);

            var contentScroll = new ScrollView { style = { flexGrow = 1 } };
            _content = new VisualElement { style = { paddingLeft = 16, paddingRight = 16, paddingTop = 8, paddingBottom = 12 } };
            contentScroll.Add(_content);
            right.Add(contentScroll);
            split.Add(right);
            _view.Add(split);

            NexusRuntime.OnContextRegistered -= OnContextsChanged;
            NexusRuntime.OnContextUnregistered -= OnContextsChanged;
            NexusRuntime.OnContextRegistered += OnContextsChanged;
            NexusRuntime.OnContextUnregistered += OnContextsChanged;
            OnContextsChanged(null);

            return _view;
        }

        public override void OnDisable()
        {
            NexusRuntime.OnContextRegistered -= OnContextsChanged;
            NexusRuntime.OnContextUnregistered -= OnContextsChanged;
            _trackers.Clear();
            base.OnDisable();
        }

        public override void OnUpdate()
        {
            if (_content == null || !Application.isPlaying) return;
            UpdateTrackers();

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefresh < 0.5) return;
            _lastRefresh = now;
            // the overview's counts and queue depths change while the game runs; the other tabs change with the context set
            if (_activeTab == InspectorTab.Overview) RenderContent();
        }

        public override IReadOnlyList<(string Label, Action Action, Color Color)> GetContextActions()
            => new List<(string, Action, Color)>
            {
                (NexusLang.Get("ci_action_copy_report"), CopyContextReport, NexusEditorStyles.BtnBlue),
            };

        /// <summary>
        /// The running contexts in tree order: each context follows its parent, one level deeper. A context
        /// whose parent is not running (or not in the list) is listed at the top level, so none is left out.
        /// </summary>
        internal static List<(IContext Context, int Depth)> BuildTree(IReadOnlyList<IContext> contexts)
        {
            var result = new List<(IContext Context, int Depth)>();
            if (contexts == null) return result;

            var running = new HashSet<IContext>(contexts);
            var children = new Dictionary<IContext, List<IContext>>();
            foreach (var ctx in contexts)
            {
                if (ctx.Parent == null || !running.Contains(ctx.Parent)) continue;
                if (!children.TryGetValue(ctx.Parent, out var list)) children[ctx.Parent] = list = new List<IContext>();
                list.Add(ctx);
            }

            void Add(IContext ctx, int depth)
            {
                result.Add((ctx, depth));
                if (children.TryGetValue(ctx, out var list))
                    foreach (var child in list) Add(child, depth + 1);
            }

            foreach (var ctx in contexts)
                if (ctx.Parent == null || !running.Contains(ctx.Parent))
                    Add(ctx, 0);
            return result;
        }

        // ── Tree ──────────────────────────────────────────────────

        private void OnContextsChanged(IContext _)
        {
            if (_tree == null) return;
            var tree = BuildTree(NexusRuntime.ActiveContexts);
            if (_selectedContext == null || !tree.Exists(e => e.Context == _selectedContext))
                _selectedContext = tree.Count > 0 ? tree[0].Context : null;
            RebuildTree(tree);
            RenderContent();
        }

        private void Select(IContext ctx)
        {
            _selectedContext = ctx;
            RebuildTree(BuildTree(NexusRuntime.ActiveContexts));
            RenderContent();
        }

        private void RebuildTree(List<(IContext Context, int Depth)> tree)
        {
            _tree.Clear();
            if (tree.Count == 0)
            {
                _tree.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("gm_no_active_contexts")));
                return;
            }

            foreach (var (ctx, depth) in tree)
            {
                bool selected = ctx == _selectedContext;
                var row = new VisualElement
                {
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        alignItems = Align.Center,
                        marginLeft = depth * 14,
                        marginBottom = 2,
                        paddingLeft = 6, paddingRight = 6, paddingTop = 4, paddingBottom = 4,
                        backgroundColor = new StyleColor(selected ? NexusEditorStyles.HighlightBg : NexusEditorStyles.RowAlt),
                        borderTopLeftRadius = 4, borderTopRightRadius = 4, borderBottomLeftRadius = 4, borderBottomRightRadius = 4
                    }
                };
                row.RegisterCallback<MouseDownEvent>(evt =>
                {
                    Select(ctx);
                    evt.StopPropagation();
                });

                row.Add(new Label(FormatContextLabel(ctx))
                {
                    style = { flexGrow = 1, fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue) }
                });
                row.Add(NexusEditorStyles.CreatePill(string.Format(NexusLang.Get("hierarchy_handlers_pill"), CountHandlers(ctx)),
                    NexusEditorStyles.CardBgGreen, NexusEditorStyles.AccentGreenText));

                if (ctx is Context concrete && concrete.ContextData != null)
                {
                    var data = concrete.ContextData;
                    var pingBtn = new Button(() =>
                    {
                        Selection.activeObject = data;
                        EditorGUIUtility.PingObject(data);
                    }) { text = NexusLang.Get("hierarchy_config_so") };
                    pingBtn.style.fontSize = 8;
                    pingBtn.style.marginLeft = 4;
                    row.Add(pingBtn);
                }
                _tree.Add(row);
            }
        }

        private static int CountHandlers(IContext ctx)
        {
            int count = 0;
            if (ctx.SignalBus?.RegisteredHandlers is { } handlers)
                foreach (var kv in handlers) count += kv.Value.Count;
            return count;
        }

        private static string FormatContextLabel(IContext ctx)
        {
            if (ctx == null) return NexusLang.Get("ci_null_label");
            if (!string.IsNullOrEmpty(ctx.ScopeTag)) return ctx.ScopeTag;
            if (ctx is Context c && c.ContextData != null) return c.ContextData.name;
            return ctx.GetType().Name;
        }

        // ── Tabs ──────────────────────────────────────────────────

        private Button BuildTabButton(InspectorTab tab)
        {
            string label = tab switch
            {
                InspectorTab.Overview => NexusLang.Get("ci_tab_overview"),
                InspectorTab.Singletons => NexusLang.Get("ci_tab_singletons"),
                InspectorTab.Bindings => NexusLang.Get("ci_tab_bindings"),
                InspectorTab.Services => NexusLang.Get("ci_tab_services"),
                InspectorTab.Signals => NexusLang.Get("ci_tab_signals"),
                InspectorTab.Extensions => NexusLang.Get("ci_tab_extensions"),
                _ => NexusLang.Get("ci_tab_firesignal"),
            };

            var btn = new Button(() =>
            {
                _activeTab = tab;
                foreach (var kv in _tabButtons) StyleTabBtn(kv.Value, kv.Key == _activeTab);
                RenderContent();
            }) { text = label };

            btn.style.fontSize = 10;
            btn.style.paddingLeft = btn.style.paddingRight = 12;
            btn.style.paddingTop = btn.style.paddingBottom = 6;
            btn.style.borderTopWidth = btn.style.borderBottomWidth =
            btn.style.borderLeftWidth = btn.style.borderRightWidth = 0;
            StyleTabBtn(btn, tab == _activeTab);

            _tabButtons[tab] = btn;
            return btn;
        }

        private static void StyleTabBtn(Button btn, bool active)
        {
            btn.style.backgroundColor = new StyleColor(active ? NexusEditorStyles.HighlightBg : Color.clear);
            btn.style.color = new StyleColor(active ? NexusEditorStyles.AccentBlue : NexusEditorStyles.TextPrimary);
            btn.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        private void RenderContent()
        {
            if (_content == null) return;
            _content.Clear();
            _trackers.Clear();
            if (_selectedContext == null) return;

            switch (_activeTab)
            {
                case InspectorTab.Overview:    RenderOverview();   break;
                case InspectorTab.Singletons:  RenderSingletons(); break;
                case InspectorTab.Bindings:    RenderBindings();   break;
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
            var concrete   = ctx as Context;
            var bindings   = NexusEditorDataProvider.GetAllBindings(ctx);
            var singletons = NexusEditorDataProvider.GetResolvedSingletons(ctx);
            var handlers   = ctx.SignalBus?.RegisteredHandlers;
            var plugins    = concrete?.PluginsReadOnlyCopy;
            int models = singletons.Count(s => s is IReactiveModel);
            int services = singletons.Count(s => s is INexusService);

            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_tag"),          ctx.ScopeTag ?? NexusLang.Get("ci_no_tag"), NexusEditorStyles.AccentBlue));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_type"),         ctx.GetType().Name, NexusEditorStyles.TextPrimary));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_parent"),       ctx.Parent != null ? FormatContextLabel(ctx.Parent) : NexusLang.Get("ci_none")));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_di_bindings"),  $"{bindings.Count}", NexusEditorStyles.AccentGreen));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_singletons"),   $"{singletons.Count}", NexusEditorStyles.AccentBlue));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("models"),               $"{models}", NexusEditorStyles.AccentBlue));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("services"),             $"{services}", NexusEditorStyles.AccentGreen));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_signals"),      $"{handlers?.Count ?? 0}", NexusEditorStyles.AccentPurple));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_plugins"),      $"{plugins?.Count ?? 0}", NexusEditorStyles.AccentOrange));
            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_has_interceptors"), (concrete?.HasInterceptors ?? false).ToString(), NexusEditorStyles.TextSecondary));

            if (plugins?.Count > 0)
            {
                _content.Add(MakeSpacer(8));
                AddSectionTitle(NexusLang.Get("ci_runtime_plugins"));
                foreach (var (plugin, _) in plugins)
                    _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_plugin"), plugin.GetType().Name, NexusEditorStyles.AccentOrange));
            }

            // Signal queues (HybridQueue): live depth and cumulative throughput.
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

            // Command pools (CommandPoolManager): live utilization and reuse ratio.
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

                for (int i = 0; i < poolStats.Count; i++)
                {
                    var s = poolStats[i];
                    string typeName = s.CommandType != null ? s.CommandType.Name : "(unknown)";
                    _content.Add(NexusVisualization.CreateStatRow($"  {typeName}", $"{s.Available}/{s.MaxSize}  ·  reuse {s.ReuseRatio:P0}", NexusEditorStyles.TextSecondary));
                }
            }
        }

        // ── Singletons tab (live, editable) ───────────────────────

        private void RenderSingletons()
        {
            AddSectionTitle(NexusLang.Get("ci_resolved_singletons_title"));

            var singletons = NexusEditorDataProvider.GetResolvedSingletons(_selectedContext)
                .Where(s => s != null && !IsFrameworkPart(s))
                .ToList();
            if (singletons.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_no_singletons"));
                return;
            }

            foreach (var instance in singletons
                .Where(s => string.IsNullOrEmpty(_searchFilter) || s.GetType().Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(s => s.GetType().Name))
            {
                var type = instance.GetType();
                string key = $"{FormatContextLabel(_selectedContext)}|{type.FullName}";
                var foldout = new Foldout { text = type.Name, value = _expandedSingletons.Contains(key) };
                foldout.style.marginTop = 3;
                var body = new VisualElement { style = { paddingLeft = 15, paddingBottom = 5 } };
                foldout.Add(body);
                foldout.RegisterValueChangedCallback(evt =>
                {
                    if (evt.target != foldout) return;
                    if (evt.newValue)
                    {
                        _expandedSingletons.Add(key);
                        BuildInstanceInspector(body, instance);
                    }
                    else
                    {
                        _expandedSingletons.Remove(key);
                        body.Clear();
                        _trackers.RemoveAll(t => t.Instance == instance);
                    }
                });
                if (foldout.value) BuildInstanceInspector(body, instance);
                _content.Add(foldout);
            }

            _content.Add(new Label(string.Format(NexusLang.Get("ci_total_singletons"), singletons.Count))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
        }

        private static bool IsFrameworkPart(object instance)
            => instance is NexusDI || instance is IContext || instance is ISignalBus || instance is HybridQueue
               || instance is CommandPoolManager || instance is ViewBinder;

        private void BuildInstanceInspector(VisualElement container, object instance)
        {
            container.Clear();
            _trackers.RemoveAll(t => t.Instance == instance);

            Type type = instance.GetType();
            string interfaces = GetInterfaceSummary(type);
            if (interfaces.Length > 0)
                container.Add(new Label(string.Format(NexusLang.Get("ci_implements"), interfaces)) { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary) } });

            var members = NexusFieldInspector.EnumerateMembers(type).ToList();
            if (members.Count == 0)
            {
                container.Add(new Label(NexusLang.Get("hierarchy_no_fields")) { style = { color = Color.gray, fontSize = 9, unityFontStyleAndWeight = FontStyle.Italic } });
                return;
            }

            container.Add(new Label(NexusLang.Get("hierarchy_fields")) { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = NexusEditorStyles.TextSecondary, marginTop = 4 } });
            foreach (var (member, memberType) in members)
                if (member is FieldInfo field)
                    container.Add(CreateMemberRow(instance, field, memberType, () => field.GetValue(instance), val => field.SetValue(instance, val)));

            container.Add(new Label(NexusLang.Get("hierarchy_properties")) { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = NexusEditorStyles.TextSecondary, marginTop = 8 } });
            foreach (var (member, memberType) in members)
                if (member is PropertyInfo prop)
                    container.Add(CreateMemberRow(instance, prop, memberType, () => prop.GetValue(instance),
                        prop.CanWrite && prop.GetSetMethod(true) != null ? val => prop.SetValue(instance, val) : (Action<object>)null));
        }

        private VisualElement CreateMemberRow(object instance, MemberInfo member, Type type, Func<object> getter, Action<object> setter)
        {
            object value = null;
            try { value = getter(); }
            catch (Exception ex) { Debug.LogWarning($"[Nexus ContextInspector] Reading '{member.Name}' failed: {ex.Message}"); }

            VisualElement element = NexusFieldInspector.CreateField(member.Name, type, getter, setter, _ =>
            {
                if (instance is UnityEngine.Object unityObj) Undo.RecordObject(unityObj, "Modify Model Member");
            });
            // a type the field inspector does not map (a ReactiveProperty, a custom class) shows its text, kept current too
            object shown = value;
            if (element == null)
            {
                shown = FormatValue(value);
                element = new Label($"{member.Name}: {shown}") { style = { color = Color.white, fontSize = 10 } };
            }

            _trackers.Add(new ValueTracker { Instance = instance, Member = member, Element = element, LastValue = shown });
            return element;
        }

        private static string FormatValue(object value) => value?.ToString() ?? NexusLang.Get("hier_null_value");

        private void UpdateTrackers()
        {
            foreach (var tracker in _trackers)
            {
                if (tracker.Failed) continue;
                try
                {
                    object current = tracker.Member is FieldInfo field ? field.GetValue(tracker.Instance)
                        : ((PropertyInfo)tracker.Member).GetValue(tracker.Instance);
                    var label = tracker.Element as Label;
                    object shown = label != null ? FormatValue(current) : current;

                    if (!Equals(tracker.LastValue, shown))
                    {
                        tracker.LastValue = shown;
                        tracker.FlashTimeRemaining = 0.6f;
                        tracker.Element.style.backgroundColor = new StyleColor(new Color(0.18f, 0.45f, 0.18f, 0.7f));
                        if (label != null) label.text = $"{tracker.Member.Name}: {shown}";
                        else NexusFieldInspector.SetValueWithoutNotify(tracker.Element, current);
                    }
                    else if (tracker.FlashTimeRemaining > 0f)
                    {
                        // the window updates every 200 ms
                        tracker.FlashTimeRemaining -= 0.2f;
                        if (tracker.FlashTimeRemaining <= 0f)
                            tracker.Element.style.backgroundColor = new StyleColor(StyleKeyword.Null);
                    }
                }
                catch (Exception ex)
                {
                    tracker.Failed = true;
                    Debug.LogWarning($"[Nexus ContextInspector] Updating '{tracker.Member.Name}' failed: {ex.Message}");
                }
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

            _content.Add(NexusVisualization.CreateDataTable(
                new[] { (NexusLang.Get("ci_col_interface_key"), 0.5f), (NexusLang.Get("ci_col_concrete_type"), 0.5f) },
                filtered.Select(kv => new[] { kv.Key.Name, kv.Value.Name })));

            _content.Add(new Label(string.Format(NexusLang.Get("ci_total_bindings"), bindings.Count))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
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
                rows));

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
                .SelectMany(kv => kv.Value.Select(h => new[] { kv.Key.Name, h.CommandType?.Name ?? "—", h.Mode.ToString() }));

            _content.Add(NexusVisualization.CreateDataTable(
                new[] { (NexusLang.Get("ci_col_signal"), 0.4f), (NexusLang.Get("ci_col_command_handler"), 0.4f), (NexusLang.Get("ci_col_mode"), 0.2f) },
                rows));

            _content.Add(new Label(string.Format(NexusLang.Get("ci_total_signals_count"), handlers.Count))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
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

            int totalInterceptors = 0, totalDecorators = 0, totalSerializers = 0, totalSinks = 0;
            foreach (var (_, pctx) in plugins)
            {
                if (pctx == null) continue;
                totalInterceptors += pctx.Interceptors?.Count ?? 0;
                totalDecorators   += pctx.Decorators?.Count ?? 0;
                totalSerializers  += pctx.Serializers?.Count ?? 0;
                totalSinks        += pctx.TraceSinks?.Count ?? 0;
            }

            _content.Add(NexusVisualization.CreateStatRow(NexusLang.Get("ci_stat_plugins"),        $"{plugins.Count}", NexusEditorStyles.AccentOrange));
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

        private static void AddPipelineList<T>(VisualElement card, string label, IReadOnlyList<T> items, Color accent, bool ordered)
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

        // ── Fire Signal tab ───────────────────────────────────────

        private void RenderFireSignal()
        {
            AddSectionTitle(NexusLang.Get("ci_fire_test_signal_title"));

            var signalTypes = _selectedContext.SignalBus?.RegisteredHandlers?.Keys.OrderBy(t => t.Name).ToList() ?? new List<Type>();
            if (signalTypes.Count == 0)
            {
                AddEmpty(NexusLang.Get("ci_no_signal_types"));
                return;
            }

            // a signal chosen in another context that this one does not bind falls back to the first
            if (_fireSignalType == null || !signalTypes.Contains(_fireSignalType))
                _fireSignalType = signalTypes[0];

            var dropdown = new DropdownField(NexusLang.Get("ci_signal_type"), signalTypes.Select(t => t.FullName ?? t.Name).ToList(), signalTypes.IndexOf(_fireSignalType));
            dropdown.style.marginBottom = 8;
            var form = new VisualElement();
            var context = _selectedContext;
            dropdown.RegisterValueChangedCallback(_ =>
            {
                _fireSignalType = signalTypes[dropdown.index];
                form.Clear();
                form.Add(SignalFireForm.Create(_fireSignalType, context));
            });
            _content.Add(dropdown);
            form.Add(SignalFireForm.Create(_fireSignalType, context));
            _content.Add(form);
        }

        // ── Helpers ───────────────────────────────────────────────

        // the interface summary is type-stable, so it is built once per type
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> s_ifaceSummaryCache = new();

        private static string GetInterfaceSummary(Type type)
        {
            return s_ifaceSummaryCache.GetOrAdd(type, static t =>
            {
                var names = new List<string>(4);
                foreach (var i in t.GetInterfaces())
                {
                    if (i.Namespace?.StartsWith("Nexus") == true || i.Namespace?.StartsWith("System") != true)
                    {
                        names.Add(i.Name);
                        if (names.Count == 4) break;
                    }
                }
                return names.Count > 0 ? string.Join(", ", names) : string.Empty;
            });
        }

        private void AddSectionTitle(string text)
        {
            _content.Add(new Label(text)
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue), marginBottom = 8 }
            });
        }

        private void AddEmpty(string msg)
        {
            _content.Add(new Label(msg)
            {
                style = { color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 16, unityTextAlign = TextAnchor.MiddleCenter, fontSize = 10 }
            });
        }

        private static VisualElement MakeSpacer(float h) => new() { style = { height = h } };

        private void CopyContextReport()
        {
            if (_selectedContext == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Nexus Context Report: {FormatContextLabel(_selectedContext)} ===");
            sb.AppendLine($"Type: {_selectedContext.GetType().Name}");
            sb.AppendLine($"Parent: {(_selectedContext.Parent != null ? FormatContextLabel(_selectedContext.Parent) : "none")}");

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
