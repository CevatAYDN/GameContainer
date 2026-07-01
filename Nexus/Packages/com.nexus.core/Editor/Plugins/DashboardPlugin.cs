using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;
using System;

namespace Nexus.Editor
{
    public class DashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "Dashboard";
        public override string DisplayName => "Dashboard";
        public override int Order => 0;

        private VisualElement _view;
        private Label _contextStat;
        private Label _handlerStat;
        private Label _rootStat;
        private Label _modelStat;
        private Label _serviceStat;
        private Label _commandStat;
        private Label _viewStat;
        private Label _perfStat;
        private VisualElement _validationCard;
        private IVisualElementScheduledItem _refreshSchedule;
        private static int s_cachedModelCount = -1, s_cachedServiceCount = -1, s_cachedCommandCount = -1, s_cachedViewCount = -1;
        private static bool s_overviewCacheValid = false;

        private static void RefreshOverviewCache()
        {
            int mc = 0, sc = 0, cc = 0, vc = 0;
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("mscorlib") || name.StartsWith("Mono") || name.IndexOf("Tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract) continue;
                        if (typeof(IReactiveModel).IsAssignableFrom(type)) mc++;
                        if (typeof(INexusService).IsAssignableFrom(type)) sc++;
                        if (typeof(ICommand).IsAssignableFrom(type) || typeof(IAsyncCommand).IsAssignableFrom(type)) cc++;
                        if (typeof(View).IsAssignableFrom(type)) vc++;
                    }
                }
                catch { }
            }
            s_cachedModelCount = mc;
            s_cachedServiceCount = sc;
            s_cachedCommandCount = cc;
            s_cachedViewCount = vc;
            s_overviewCacheValid = true;
        }

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("dashboard").ToUpper());
            _view.Add(toolbar);

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.style.paddingLeft = 20;
            scroll.style.paddingRight = 20;
            scroll.style.paddingTop = 20;
            scroll.style.paddingBottom = 20;

            BuildStatusSection(scroll);
            BuildOverviewSection(scroll);
            BuildQuickActions(scroll);
            BuildRuntimeSection(scroll);
            BuildValidationSection(scroll);
            BuildFrameworkInfo(scroll);

            _view.Add(scroll);

            _refreshSchedule = _view.schedule.Execute(RefreshStats).Every(1000);

            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            base.OnDisable();
        }

        private void BuildStatusSection(VisualElement parent)
        {
            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            var cardBg = playing ? NexusEditorStyles.CardBgGreen : NexusEditorStyles.CardBgBlue;
            var titleColor = playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentBlue;

            var statusCard = NexusEditorStyles.CreateCard(cardBg);
            var statusRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };

            var statusDot = NexusEditorStyles.CreateStatusDot(titleColor, 12);
            statusRow.Add(statusDot);

                var statusLabel = new Label(playing ? "  ● " + NexusLang.Get("system_active") : "  ○ " + NexusLang.Get("system_standby"))
            {
                style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(titleColor) }
            };
            statusRow.Add(statusLabel);
            statusCard.Add(statusRow);

            var statRow1 = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            _contextStat = CreateStatBox(statRow1, contextCount.ToString(), NexusLang.Get("contexts"), NexusEditorStyles.AccentBlue);
            _handlerStat = CreateStatBox(statRow1, handlerCount.ToString(), NexusLang.Get("handlers"), NexusEditorStyles.AccentPurple);
            _rootStat = CreateStatBox(statRow1, rootCount.ToString(), NexusLang.Get("roots"), NexusEditorStyles.AccentYellow);
            statusCard.Add(statRow1);

            var hintText = "";
            if (!playing && rootCount == 0)
                hintText = NexusLang.Get("no_roots");
            else if (!playing)
                hintText = NexusLang.Get("ready");
            else
                hintText = string.Format(NexusLang.Get("live_hint"), contextCount);

            var hint = NexusEditorStyles.CreateHint(hintText);
            hint.style.marginTop = 8;
            statusCard.Add(hint);

            parent.Add(statusCard);
        }

        private void BuildOverviewSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 12;

            var title = new Label(NexusLang.Get("project_overview"))
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentYellow), marginBottom = 8 }
            };
            card.Add(title);

            if (!s_overviewCacheValid) RefreshOverviewCache();
            int modelCount = s_cachedModelCount, serviceCount = s_cachedServiceCount, commandCount = s_cachedCommandCount, viewCount = s_cachedViewCount;

            var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _modelStat = CreateStatBox(statRow, modelCount.ToString(), NexusLang.Get("models"), NexusEditorStyles.AccentYellow);
            _serviceStat = CreateStatBox(statRow, serviceCount.ToString(), NexusLang.Get("services"), NexusEditorStyles.AccentGreen);
            _commandStat = CreateStatBox(statRow, commandCount.ToString(), NexusLang.Get("commands"), NexusEditorStyles.AccentOrange);
            _viewStat = CreateStatBox(statRow, viewCount.ToString(), NexusLang.Get("views"), NexusEditorStyles.AccentBlue);
            card.Add(statRow);

            parent.Add(card);
        }

        private void BuildRuntimeSection(VisualElement parent)
        {
            if (!Application.isPlaying) return;

            NexusRuntime.Metrics.UpdateRates();

            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;

            var title = new Label(NexusLang.Get("runtime_metrics"))
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentGreen), marginBottom = 8 }
            };
            card.Add(title);

            var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _perfStat = CreateStatBox(statRow, $"{NexusRuntime.Metrics.SignalsPerSecond:F1}/s", NexusLang.Get("perf_signals"), NexusEditorStyles.AccentBlue);
            CreateStatBox(statRow, $"{NexusRuntime.Metrics.CommandsPerSecond:F1}/s", NexusLang.Get("perf_commands"), NexusEditorStyles.AccentGreen);
            CreateStatBox(statRow, $"{NexusRuntime.Metrics.TotalSignalsDispatched:N0}", NexusLang.Get("total_sigs"), NexusEditorStyles.AccentPurple);
            CreateStatBox(statRow, $"{System.GC.GetTotalMemory(false) / 1024 / 1024:N0}M", NexusLang.Get("gc_memory"), NexusEditorStyles.TextSecondary);
            card.Add(statRow);

            parent.Add(card);

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts != null && contexts.Count > 0)
            {
                var modelCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
                modelCard.style.marginTop = 8;
                var shownTotal = 0;

                var modelTitle = new Label(NexusLang.Get("live_models"))
                {
                    style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentPurple), marginBottom = 8 }
                };
                modelCard.Add(modelTitle);

                foreach (var ctxObj in contexts)
                {
                    if (ctxObj is not Context ctx || shownTotal >= 8) continue;
                    var singletons = ctx.Container.GetActiveSingletons();
                    foreach (var obj in singletons)
                    {
                        if (obj is IReactiveModel && shownTotal < 8)
                        {
                            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 3, alignItems = Align.Center } };
                            row.Add(new Label(obj.GetType().Name)
                                { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextPrimary), width = 140 } });

                            var t = obj.GetType();
                            int props = 0;
                            foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                if (props >= 3) break;
                                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                                try
                                {
                                    var val = prop.GetValue(obj);
                                    var valStr = val?.ToString() ?? "null";
                                    if (valStr.Length > 20) valStr = valStr.Substring(0, 17) + "...";
                                    row.Add(new Label($"{prop.Name}={valStr}")
                                        { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.AccentBlue), marginLeft = 6 } });
                                    props++;
                                }
                                catch { }
                            }
                            modelCard.Add(row);
                            shownTotal++;
                        }
                    }
                }
                if (shownTotal == 0)
                    modelCard.Add(new Label(NexusLang.Get("no_reactive_models"))
                        { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary) } });

                parent.Add(modelCard);
            }
        }

        private void BuildQuickActions(VisualElement parent)
        {
            var groupCard = NexusEditorStyles.CreateActionGroup(parent, NexusLang.Get("quick_actions"));

            var actions = new (string titleKey, string descKey, string pluginId)[] {
                ("action_wizard_title",      "action_wizard_desc",      "Wizard"),
                ("action_hierarchy_title",   "action_hierarchy_desc",   "Hierarchy"),
                ("action_explorer_title",    "action_explorer_desc",    "Explorer"),
                ("action_tracer_title",      "action_tracer_desc",      "Tracer"),
                ("action_graph_title",       "action_graph_desc",       "Graph"),
                ("action_gamemanager_title", "action_gamemanager_desc", "GameManager"),
                ("action_typeanalyzer_title","action_typeanalyzer_desc","TypeAnalyzer"),
                ("action_help_title",        "action_help_desc",        "Help"),
            };

            var colors = new[] { NexusEditorStyles.BtnBlue, NexusEditorStyles.BtnTeal,
                NexusEditorStyles.BtnPurple, new Color(0.5f,0.5f,0.6f),
                new Color(0.8f,0.3f,0.3f), new Color(0.3f,0.7f,0.6f),
                new Color(0.6f,0.5f,0.5f), new Color(0.5f,0.5f,0.8f) };

            var buttonRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            for (int i = 0; i < actions.Length; i++)
            {
                var a = actions[i];
                AddActionCard(buttonRow, NexusLang.Get(a.titleKey), NexusLang.Get(a.descKey), colors[i], () => Window.SwitchToPlugin(a.pluginId));
            }

            groupCard.Add(buttonRow);
        }

        private Label CreateStatBox(VisualElement parent, string value, string label, Color accentColor)
        {
            var box = new VisualElement { style = { flexGrow = 1, alignItems = Align.Center, paddingLeft = 4, paddingRight = 4 } };

            var valLabel = new Label(value)
            {
                style = { fontSize = 24, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(accentColor) }
            };
            box.Add(valLabel);

            var descLabel = new Label(label)
            {
                style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 2 }
            };
            box.Add(descLabel);

            parent.Add(box);
            return valLabel;
        }

        private void AddActionCard(VisualElement parent, string title, string description, Color btnColor, System.Action onClick)
        {
            var card = new VisualElement();
            card.AddToClassList(NexusEditorStyles.ClassDashboardActionCard);

            var titleLabel = new Label(title)
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue), marginBottom = 4 }
            };
            card.Add(titleLabel);

            var descLabel = new Label(description)
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 8, whiteSpace = WhiteSpace.Normal }
            };
            card.Add(descLabel);

            var btn = NexusEditorStyles.CreateButton(NexusLang.Get("open"), onClick, btnColor);
            btn.style.marginTop = 0;
            btn.style.marginBottom = 0;
            btn.style.alignSelf = Align.FlexStart;
            card.Add(btn);

            card.RegisterCallback<MouseDownEvent>(evt => onClick());
            parent.Add(card);
        }

        private void BuildValidationSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;
            _validationCard = card;
            PopulateValidationCard(card);
            parent.Add(card);
        }

        private void RefreshValidationCard(VisualElement card)
        {
            if (card == null) return;
            card.Clear();
            PopulateValidationCard(card);
        }

        private void PopulateValidationCard(VisualElement card)
        {
            var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
            titleRow.Add(new Label(NexusLang.Get("build_validation"))
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentOrange) }
            });

            if (BuildValidation.HasRun)
            {
                var statusPill = BuildValidation.LastRunPassed
                    ? NexusEditorStyles.CreatePill(NexusLang.Get("pass"), new Color(0.1f, 0.3f, 0.1f), NexusEditorStyles.AccentGreen)
                    : NexusEditorStyles.CreatePill(NexusLang.Get("fail"), new Color(0.3f, 0.1f, 0.1f), NexusEditorStyles.AccentRed);
                titleRow.Add(statusPill);
            }
            card.Add(titleRow);

            if (BuildValidation.HasRun)
            {
                var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 4 } };
                statRow.Add(new Label($"{NexusLang.Get("errors")}: {BuildValidation.LastErrorCount}")
                    { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.AccentRed), marginRight = 12 } });
                statRow.Add(new Label($"{NexusLang.Get("warnings")}: {BuildValidation.LastWarningCount}")
                    { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.AccentYellow) } });
                card.Add(statRow);

                var results = BuildValidation.LastResults;
                int shown = 0;
                foreach (var entry in results)
                {
                    if (shown >= 5) break;
                    var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 2 } };
                    row.Add(NexusEditorStyles.CreateStatusDot(entry.IsError ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentYellow, 5));
                    row.Add(new Label(entry.Message)
                    {
                        style = { fontSize = 8, color = new StyleColor(entry.IsError ? NexusEditorStyles.AccentRed : NexusEditorStyles.TextSecondary), whiteSpace = WhiteSpace.Normal }
                    });
                    card.Add(row);
                    shown++;
                }
                if (results.Count > 5)
                {
                    card.Add(new Label($"+ {results.Count - 5} {NexusLang.Get("more")}")
                        { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 2 } });
                }
            }
            else
            {
                card.Add(new Label(NexusLang.Get("not_run_yet"))
                    { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
            }

            var runBtn = new Button(() => { BuildValidation.RunSilent(); RefreshValidationCard(_validationCard); })
            {
                text = BuildValidation.HasRun ? NexusLang.Get("rerun_validation") : NexusLang.Get("run_validation"),
                style = { fontSize = 10, marginTop = 6, backgroundColor = new StyleColor(NexusEditorStyles.AccentOrange), color = Color.white }
            };
            card.Add(runBtn);
        }

        private void BuildFrameworkInfo(VisualElement parent)
        {
            var infoCard = NexusEditorStyles.CreateInfoCard(parent, NexusLang.Get("framework"), NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBgAlt,
                NexusLang.Get("framework_desc"));
        }

        private void RefreshStats()
        {
            if (_contextStat == null) return;

            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            _contextStat.text = contextCount.ToString();
            _handlerStat.text = handlerCount.ToString();
            _rootStat.text = rootCount.ToString();

            if (_perfStat != null && Application.isPlaying)
            {
                NexusRuntime.Metrics.UpdateRates();
                _perfStat.text = $"{NexusRuntime.Metrics.SignalsPerSecond:F1}/s";
            }
        }
    }
}
