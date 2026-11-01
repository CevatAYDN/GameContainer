using Nexus.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace Nexus.Editor
{
    /// <summary>
    /// The running contexts and the framework's live rates. Its lists of the game's models, signals, commands, views
    /// and services and its signal test moved to the Architecture tab (#193 phase 3); phase 4 merges what is left
    /// into the Live section's context view.
    /// </summary>
    public class GameManagerPlugin : NexusEditorPlugin
    {
        public override string Id => "GameManager";
        public override string DisplayName => NexusLang.Get("action_gamemanager_title");
        public override int Order => 6;
        public override string Category => "cat_live";

        private enum Section { Contexts, Live }
        private Section _activeSection = Section.Contexts;
        private readonly Dictionary<Section, Button> _sectionButtons = new();

        private VisualElement _root;
        private ScrollView _content;
        private VisualElement _breadcrumb;
        private double _lastRefreshTime;
        private const double RefreshInterval = 0.5;
        private int _rootCount;

        // Live section elements updated in place
        private VisualElement _sigFill;
        private VisualElement _cmdFill;
        private Label _sigRateLabel;
        private Label _cmdRateLabel;

        public override VisualElement CreateView()
        {
            _root = new VisualElement { style = { flexGrow = 1 } };
            _root.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("gamemanager_title")));

            _breadcrumb = new VisualElement();
            _breadcrumb.AddToClassList(NexusEditorStyles.ClassBreadcrumb);
            _root.Add(_breadcrumb);
            BuildBreadcrumb();

            _content = new ScrollView { style = { flexGrow = 1 } };
            _root.Add(_content);

            var quickBar = new VisualElement();
            quickBar.AddToClassList(NexusEditorStyles.ClassQuickbar);
            quickBar.Add(NexusEditorStyles.CreateButton(NexusLang.Get("gm_refresh_all"), () => { RefreshRootCount(); RenderActiveSection(); }, NexusEditorStyles.BtnGray));
            _root.Add(quickBar);

            RefreshRootCount();
            RenderActiveSection();
            return _root;
        }

        public override void OnDisable()
        {
            _lastRefreshTime = 0d;
            _sectionButtons.Clear();
            _breadcrumb = null;
            _content = null;
            _sigFill = null;
            _cmdFill = null;
            _sigRateLabel = null;
            _cmdRateLabel = null;
            base.OnDisable();
        }

        public override void OnUpdate()
        {
            if (!Application.isPlaying || _content == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefreshTime <= RefreshInterval) return;
            _lastRefreshTime = now;
            if (_activeSection == Section.Live && _sigFill != null) UpdateLiveMetricsChartOnly();
            else
            {
                RefreshRootCount();
                RenderActiveSection();
            }
        }

        private void BuildBreadcrumb()
        {
            _breadcrumb.Clear();
            _sectionButtons.Clear();

            var sections = new (Section s, string label, Color color)[]
            {
                (Section.Contexts, NexusLang.Get("gamemanager_section_contexts"), NexusEditorStyles.AccentGreen),
                (Section.Live,     NexusLang.Get("gamemanager_section_live"),     new Color(1f, 0.5f, 0.8f)),
            };

            foreach (var (s, label, color) in sections)
            {
                var btn = new Button(() => SelectSection(s));
                btn.name = $"gm_{s}";
                btn.AddToClassList(NexusEditorStyles.ClassSectionTab);
                btn.Add(NexusEditorStyles.CreateStatusDot(color, 6));
                var txtLabel = new Label(label);
                txtLabel.AddToClassList(NexusEditorStyles.ClassSectionTabText);
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
            RefreshRootCount();
            RenderActiveSection();
        }

        private void HighlightSection()
        {
            foreach (var kvp in _sectionButtons)
                kvp.Value.EnableInClassList("active", kvp.Key == _activeSection);
        }

        private void RefreshRootCount() =>
            _rootCount = Object.FindObjectsByType<Root>(FindObjectsInactive.Exclude).Length;

        private void RenderActiveSection()
        {
            if (_content == null) return;
            _content.Clear();
            _sigFill = null;
            _cmdFill = null;

            if (_activeSection == Section.Contexts) RenderContexts();
            else RenderLive();
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
            AddSectionHeader(string.Format(NexusLang.Get("gm_contexts_header"), NexusRuntime.ActiveContexts.Count), NexusEditorStyles.AccentGreen);

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
                rowActionRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("nav_open_architecture"), () => Window?.SwitchToPlugin("Architecture"), NexusEditorStyles.BtnPurple));
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

            _content.Add(NexusEditorStyles.CreateHint(string.Format(NexusLang.Get("gm_roots_hint"), _rootCount)));
        }

        // ─── Helpers ───────────────────────────────────────────
        private void AddSectionHeader(string text, Color accentColor)
        {
            var header = new VisualElement();
            header.AddToClassList("nexus-section-header");
            header.Add(NexusEditorStyles.CreateStatusDot(accentColor, 8));
            var lbl = new Label(text);
            lbl.AddToClassList("nexus-section-header-text");
            header.Add(lbl);
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
            var box = new VisualElement();
            box.AddToClassList(NexusEditorStyles.ClassMetricBox);
            var valLabel = new Label(value);
            valLabel.AddToClassList(NexusEditorStyles.ClassMetricValue);
            valLabel.style.color = new StyleColor(accent);
            var descLabel = new Label(label);
            descLabel.AddToClassList(NexusEditorStyles.ClassMetricLabel);
            box.Add(valLabel);
            box.Add(descLabel);
            return box;
        }
    }
}
