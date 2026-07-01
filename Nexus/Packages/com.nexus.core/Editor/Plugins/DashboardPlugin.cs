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

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("NEXUS DASHBOARD");
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

            _view.schedule.Execute(RefreshStats).Every(1000);

            return _view;
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

            var statusLabel = new Label(playing ? "  ● SYSTEM ACTIVE" : "  ○ SYSTEM STANDBY")
            {
                style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(titleColor) }
            };
            statusRow.Add(statusLabel);
            statusCard.Add(statusRow);

            var statRow1 = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            _contextStat = CreateStatBox(statRow1, contextCount.ToString(), "Contexts", NexusEditorStyles.AccentBlue);
            _handlerStat = CreateStatBox(statRow1, handlerCount.ToString(), "Handlers", NexusEditorStyles.AccentPurple);
            _rootStat = CreateStatBox(statRow1, rootCount.ToString(), "Roots", NexusEditorStyles.AccentYellow);
            statusCard.Add(statRow1);

            var hintText = "";
            if (!playing && rootCount == 0)
                hintText = "Create a Root via Context Wizard to get started.";
            else if (!playing)
                hintText = "Ready. Enter Play Mode to activate the system.";
            else
                hintText = $"Live — {contextCount} context(s) active.";

            var hint = NexusEditorStyles.CreateHint(hintText);
            hint.style.marginTop = 8;
            statusCard.Add(hint);

            parent.Add(statusCard);
        }

        private void BuildOverviewSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 12;

            var title = new Label("PROJECT OVERVIEW")
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentYellow), marginBottom = 8 }
            };
            card.Add(title);

            // Static scan for models, views, services
            int modelCount = 0, serviceCount = 0, commandCount = 0, viewCount = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("Unity") || name.StartsWith("mscorlib") || name.StartsWith("Mono") || name.IndexOf("Tests", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract) continue;
                        if (typeof(IReactiveModel).IsAssignableFrom(type)) modelCount++;
                        if (typeof(INexusService).IsAssignableFrom(type)) serviceCount++;
                        if (typeof(ICommand).IsAssignableFrom(type) || typeof(IAsyncCommand).IsAssignableFrom(type)) commandCount++;
                        if (typeof(View).IsAssignableFrom(type)) viewCount++;
                    }
                }
                catch { }
            }

            var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _modelStat = CreateStatBox(statRow, modelCount.ToString(), "Models", NexusEditorStyles.AccentYellow);
            _serviceStat = CreateStatBox(statRow, serviceCount.ToString(), "Services", NexusEditorStyles.AccentGreen);
            _commandStat = CreateStatBox(statRow, commandCount.ToString(), "Commands", NexusEditorStyles.AccentOrange);
            _viewStat = CreateStatBox(statRow, viewCount.ToString(), "Views", NexusEditorStyles.AccentBlue);
            card.Add(statRow);

            parent.Add(card);
        }

        private void BuildRuntimeSection(VisualElement parent)
        {
            if (!Application.isPlaying) return;

            NexusRuntime.Metrics.UpdateRates();

            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;

            var title = new Label("RUNTIME METRICS")
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentGreen), marginBottom = 8 }
            };
            card.Add(title);

            var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _perfStat = CreateStatBox(statRow, $"{NexusRuntime.Metrics.SignalsPerSecond:F1}/s", "Signals", NexusEditorStyles.AccentBlue);
            CreateStatBox(statRow, $"{NexusRuntime.Metrics.CommandsPerSecond:F1}/s", "Commands", NexusEditorStyles.AccentGreen);
            CreateStatBox(statRow, $"{NexusRuntime.Metrics.TotalSignalsDispatched:N0}", "Total Sigs", NexusEditorStyles.AccentPurple);
            CreateStatBox(statRow, $"{System.GC.GetTotalMemory(false) / 1024 / 1024:N0}M", "GC Memory", NexusEditorStyles.TextSecondary);
            card.Add(statRow);

            parent.Add(card);

            // Live Model Inspector — show IReactiveModel singletons in Play Mode
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts != null && contexts.Count > 0 && contexts[0] is Context ctx)
            {
                var singletons = ctx.Container.GetActiveSingletons();
                var modelCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
                modelCard.style.marginTop = 8;

                var modelTitle = new Label("LIVE MODELS")
                {
                    style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentPurple), marginBottom = 8 }
                };
                modelCard.Add(modelTitle);

                int shown = 0;
                foreach (var obj in singletons)
                {
                    if (obj is IReactiveModel && shown < 8)
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
                        shown++;
                    }
                }
                if (shown == 0)
                    modelCard.Add(new Label("No IReactiveModel instances found.")
                        { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary) } });

                parent.Add(modelCard);
            }
        }

        private void BuildQuickActions(VisualElement parent)
        {
            var groupCard = NexusEditorStyles.CreateActionGroup(parent, "ALL TOOLS");

            var actions = new (string title, string desc, string pluginId)[] {
                ("Context Wizard",     "Create Root contexts & generate code",         "Wizard"),
                ("Hierarchy & Data",  "Inspect DI container & context tree (live)",    "Hierarchy"),
                ("Signal Explorer",   "View signal/command mappings & test fire",      "Explorer"),
                ("Live Tracer",       "Monitor signal chains in real-time",            "Tracer"),
                ("Signal Graph",      "Visual graph of signal→command flow",           "Graph"),
                ("Game Manager",      "Model/signal/command overview & performance",   "GameManager"),
                ("Type Analyzer",     "Analyze type coupling & [Inject] dependencies", "TypeAnalyzer"),
                ("Help & Docs",       "Quick start guides, API reference, samples",    "Help"),
            };

            var colors = new[] { NexusEditorStyles.BtnBlue, NexusEditorStyles.BtnTeal,
                NexusEditorStyles.BtnPurple, new Color(0.5f,0.5f,0.6f),
                new Color(0.8f,0.3f,0.3f), new Color(0.3f,0.7f,0.6f),
                new Color(0.6f,0.5f,0.5f), new Color(0.5f,0.5f,0.8f) };

            var buttonRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            for (int i = 0; i < actions.Length; i++)
            {
                var a = actions[i];
                AddActionCard(buttonRow, a.title, a.desc, colors[i], () => Window.SwitchToPlugin(a.pluginId));
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

            var btn = NexusEditorStyles.CreateButton("Open", onClick, btnColor);
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
            titleRow.Add(new Label("BUILD VALIDATION")
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentOrange) }
            });

            if (BuildValidation.HasRun)
            {
                var statusPill = BuildValidation.LastRunPassed
                    ? NexusEditorStyles.CreatePill("PASS", new Color(0.1f, 0.3f, 0.1f), NexusEditorStyles.AccentGreen)
                    : NexusEditorStyles.CreatePill("FAIL", new Color(0.3f, 0.1f, 0.1f), NexusEditorStyles.AccentRed);
                titleRow.Add(statusPill);
            }
            card.Add(titleRow);

            if (BuildValidation.HasRun)
            {
                var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 4 } };
                statRow.Add(new Label($"Errors: {BuildValidation.LastErrorCount}")
                    { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.AccentRed), marginRight = 12 } });
                statRow.Add(new Label($"Warnings: {BuildValidation.LastWarningCount}")
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
                    card.Add(new Label($"+ {results.Count - 5} more...")
                        { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 2 } });
                }
            }
            else
            {
                card.Add(new Label("Not run yet. Click below to validate.")
                    { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary) } });
            }

            var runBtn = new Button(() => { BuildValidation.RunSilent(); RefreshValidationCard(_validationCard); })
            {
                text = BuildValidation.HasRun ? "Re-run Validation" : "Run Build Validation",
                style = { fontSize = 10, marginTop = 6, backgroundColor = new StyleColor(NexusEditorStyles.AccentOrange), color = Color.white }
            };
            card.Add(runBtn);
        }

        private void BuildFrameworkInfo(VisualElement parent)
        {
            var infoCard = NexusEditorStyles.CreateInfoCard(parent, "FRAMEWORK", NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBgAlt,
                "Nexus Observable Architecture v0.3.0\n" +
                "Unity 6 • UI Toolkit • MIT License\n\n" +
                "Built on a 0-GC, JIT-free generic observable framework with:\n" +
                "• Causal Tracing — zero-allocation causality tracking\n" +
                "• 4 Execution Modes — Sequential, Concurrent, Exclusive, Composite\n" +
                "• Build Validation — catches priority conflicts before compile\n" +
                "• Auto-Discovery — Lifecycle, Commands, Views and Mediators\n" +
                "• Command Pooling — automatic pooling for 0-GC steady-state\n\n" +
                "Editor Suite: 9 plugins, Code Generator, Live Tracer, Graph Viewer, Type Analyzer");
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
