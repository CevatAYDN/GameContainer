using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class DashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "Dashboard";
        public override string DisplayName => NexusLang.Get("tab_dashboard");
        public override int Order => 0;

        private VisualElement _view;
        private Label _statusLabel;
        private Label _statusHint;
        private Label _contextStat;
        private Label _handlerStat;
        private Label _rootStat;
        private Label _modelStat;
        private Label _serviceStat;
        private Label _commandStat;
        private Label _viewStat;
        private Label _perfStat;
        private Label _validationSummary;
        private Label _healthSummary;
        private VisualElement _validationCard;
        private VisualElement _runtimeCardContainer;

        private static int s_cachedModelCount = -1, s_cachedServiceCount = -1, s_cachedCommandCount = -1, s_cachedViewCount = -1;
        private static bool s_overviewCacheValid = false;

        private static List<(Type type, string category, Color color)> s_typedCatalog;
        private static bool s_catalogValid = false;

        private bool _subscribedPlayMode;
        private IVisualElementScheduledItem _quickFindDebounce;

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_overviewCacheValid = false;
            s_catalogValid = false;
        }

        private static void RefreshOverviewCache()
        {
            int mc = 0, sc = 0, cc = 0, vc = 0;
            var catalog = new List<(Type type, string category, Color color)>();

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

                        string category = "CLASS";
                        Color color = NexusEditorStyles.TextSecondary;

                        if (typeof(IReactiveModel).IsAssignableFrom(type))
                        {
                            mc++;
                            category = "MODEL";
                            color = NexusEditorStyles.AccentYellow;
                        }
                        else if (typeof(INexusService).IsAssignableFrom(type))
                        {
                            sc++;
                            category = "SERVICE";
                            color = NexusEditorStyles.AccentGreen;
                        }
                        else if (typeof(ICommand).IsAssignableFrom(type) || typeof(IAsyncCommand).IsAssignableFrom(type))
                        {
                            cc++;
                            category = "COMMAND";
                            color = NexusEditorStyles.AccentOrange;
                        }
                        else if (typeof(View).IsAssignableFrom(type))
                        {
                            vc++;
                            category = "VIEW";
                            color = NexusEditorStyles.AccentBlue;
                        }

                        catalog.Add((type, category, color));
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (var le in ex.LoaderExceptions)
                    {
                        if (le != null) Debug.LogWarning($"[Nexus Dashboard] Type load warning in {name}: {le.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Nexus Dashboard] Assembly scan warning for {name}: {ex.Message}");
                }
            }

            s_cachedModelCount = mc;
            s_cachedServiceCount = sc;
            s_cachedCommandCount = cc;
            s_cachedViewCount = vc;
            s_typedCatalog = catalog;
            s_overviewCacheValid = true;
            s_catalogValid = true;
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
            BuildQuickFindSection(scroll);
            BuildOverviewSection(scroll);
            BuildQuickActions(scroll);

            _runtimeCardContainer = new VisualElement();
            scroll.Add(_runtimeCardContainer);
            BuildRuntimeSection(_runtimeCardContainer);

            BuildHealthSection(scroll);
            BuildValidationSection(scroll);
            BuildFrameworkInfo(scroll);

            _view.Add(scroll);

            if (!_subscribedPlayMode)
            {
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                _subscribedPlayMode = true;
            }

            return _view;
        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_subscribedPlayMode)
            {
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                _subscribedPlayMode = true;
            }
        }

        public override void OnDisable()
        {
            _quickFindDebounce?.Pause();
            if (_subscribedPlayMode)
            {
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                _subscribedPlayMode = false;
            }
            base.OnDisable();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            RefreshStats();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            RefreshStats();
            if (_runtimeCardContainer != null)
            {
                _runtimeCardContainer.Clear();
                BuildRuntimeSection(_runtimeCardContainer);
            }
        }

        public override IReadOnlyList<(string Label, Action Action, Color Color)> GetContextActions()
            => new List<(string, Action, Color)>
            {
                (NexusLang.Get("dash_action_codegen"),      () => NexusCodeGenerator.GenerateBinder(), NexusEditorStyles.BtnBlue),
                (NexusLang.Get("dash_action_create_root"),  () => {
                    var go = new GameObject("NexusRoot");
                    go.AddComponent<Root>();
                    Undo.RegisterCreatedObjectUndo(go, "Create Nexus Root");
                    Selection.activeObject = go;
                }, NexusEditorStyles.BtnTeal),
                (NexusLang.Get("dash_action_inspector"),   () => Window?.SwitchToPlugin("ContextInspector"), NexusEditorStyles.BtnPurple),
                (NexusLang.Get("dash_action_gamemanager"), () => Window?.SwitchToPlugin("GameManager"),       NexusEditorStyles.BtnGray),
            };

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

            _statusLabel = new Label(playing ? NexusLang.Get("system_active") : NexusLang.Get("system_standby"))
            {
                style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(titleColor) }
            };
            statusRow.Add(_statusLabel);
            statusCard.Add(statusRow);

            var statRow1 = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            _contextStat = CreateStatBox(statRow1, contextCount.ToString(), NexusLang.Get("contexts"), NexusEditorStyles.AccentBlue);
            _handlerStat = CreateStatBox(statRow1, handlerCount.ToString(), NexusLang.Get("handlers"), NexusEditorStyles.AccentPurple);
            _rootStat = CreateStatBox(statRow1, rootCount.ToString(), NexusLang.Get("roots"), NexusEditorStyles.AccentYellow);
            statusCard.Add(statRow1);

            var actionRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 6 } };
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("contexts"), NexusLang.Get("dash_tip_contexts"), NexusEditorStyles.AccentBlue, () => Window?.SwitchToPlugin("Hierarchy")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("handlers"), NexusLang.Get("dash_tip_handlers"), NexusEditorStyles.AccentPurple, () => Window?.SwitchToPlugin("Explorer")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("roots"), NexusLang.Get("dash_tip_roots"), NexusEditorStyles.AccentYellow, () => Window?.SwitchToPlugin("GameManager")));
            statusCard.Add(actionRow);

            string hintText = GetStatusHintText(playing, rootCount, contextCount);
            _statusHint = NexusEditorStyles.CreateHint(hintText);
            _statusHint.style.marginTop = 8;
            statusCard.Add(_statusHint);

            parent.Add(statusCard);
        }

        private string GetStatusHintText(bool playing, int rootCount, int contextCount)
        {
            if (!playing && rootCount == 0) return NexusLang.Get("no_roots");
            if (!playing) return NexusLang.Get("ready");
            return string.Format(NexusLang.Get("live_hint"), contextCount);
        }

        private string _quickSearchQuery = "";
        private VisualElement _quickFindResultsContainer;

        private void BuildQuickFindSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            card.style.marginTop = 12;

            card.Add(CreateSectionTitle(NexusLang.Get("db_quick_find"), NexusEditorStyles.AccentBlue));
            card.Add(CreateQuickFindRow());

            _quickFindResultsContainer = new VisualElement { style = { marginTop = 8 } };
            card.Add(_quickFindResultsContainer);

            parent.Add(card);

            if (!string.IsNullOrEmpty(_quickSearchQuery))
            {
                UpdateQuickFindResults();
            }
        }

        private VisualElement CreateSectionTitle(string titleText, Color accentColor)
        {
            return new Label(titleText)
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(accentColor), marginBottom = 8 }
            };
        }

        private VisualElement CreateQuickFindRow()
        {
            var searchRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var searchInput = new TextField
            {
                value = _quickSearchQuery,
                tooltip = NexusLang.Get("dash_quickfind_tooltip"),
                style = { flexGrow = 1, height = 24, fontSize = 11 }
            };

            searchInput.RegisterValueChangedCallback(evt =>
            {
                _quickSearchQuery = evt.newValue;
                ScheduleQuickFindUpdate();
            });

            searchRow.Add(searchInput);

            var clearBtn = new Button(() =>
            {
                searchInput.value = "";
                _quickSearchQuery = "";
                UpdateQuickFindResults();
            })
            {
                text = NexusLang.Get("clear"),
                style =
                {
                    marginLeft = 6,
                    height = 24,
                    fontSize = 9,
                    backgroundColor = new StyleColor(NexusEditorStyles.BtnGray),
                    color = Color.white
                }
            };
            searchRow.Add(clearBtn);
            return searchRow;
        }

        private void ScheduleQuickFindUpdate()
        {
            _quickFindDebounce?.Pause();
            if (_view != null)
            {
                _quickFindDebounce = _view.schedule.Execute(UpdateQuickFindResults).StartingIn(200);
            }
        }

        private void UpdateQuickFindResults()
        {
            if (_quickFindResultsContainer == null) return;
            _quickFindResultsContainer.Clear();

            if (string.IsNullOrWhiteSpace(_quickSearchQuery)) return;

            string query = _quickSearchQuery.Trim().ToLowerInvariant();

            if (!s_catalogValid || s_typedCatalog == null)
            {
                RefreshOverviewCache();
            }

            int matchCount = 0;
            foreach (var (type, category, color) in s_typedCatalog)
            {
                if (matchCount >= 10) break;

                if (type.Name.ToLowerInvariant().Contains(query))
                {
                    var resultRow = new VisualElement
                    {
                        style =
                        {
                            flexDirection = FlexDirection.Row,
                            alignItems = Align.Center,
                            paddingLeft = 6, paddingRight = 6, paddingTop = 3, paddingBottom = 3,
                            marginBottom = 2,
                            backgroundColor = new StyleColor(NexusEditorStyles.RowBase),
                            borderTopLeftRadius = 3, borderTopRightRadius = 3,
                            borderBottomLeftRadius = 3, borderBottomRightRadius = 3
                        }
                    };

                    var catPill = NexusEditorStyles.CreatePill(category, new Color(color.r, color.g, color.b, 0.2f), color);
                    catPill.style.marginRight = 8;
                    resultRow.Add(catPill);

                    var nameLabel = new Label(type.Name)
                    {
                        style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = Color.white, flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft }
                    };
                    resultRow.Add(nameLabel);

                    var targetType = type;

                    var copyBtn = new Button(() =>
                    {
                        EditorGUIUtility.systemCopyBuffer = targetType.FullName;
                    })
                    {
                        text = NexusLang.Get("dash_qf_copy"),
                        style = { fontSize = 8, marginRight = 4, height = 18, backgroundColor = new StyleColor(NexusEditorStyles.BtnGray), color = Color.white, paddingLeft = 6, paddingRight = 6 }
                    };
                    resultRow.Add(copyBtn);

                    var openBtn = new Button(() =>
                    {
                        var guids = AssetDatabase.FindAssets($"{targetType.Name} t:Script");
                        if (guids.Length > 0)
                        {
                            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                            if (obj != null) AssetDatabase.OpenAsset(obj);
                        }
                    })
                    {
                        text = NexusLang.Get("open"),
                        style = { fontSize = 8, height = 18, backgroundColor = new StyleColor(NexusEditorStyles.BtnBlue), color = Color.white, paddingLeft = 6, paddingRight = 6 }
                    };
                    resultRow.Add(openBtn);

                    _quickFindResultsContainer.Add(resultRow);
                    matchCount++;
                }
            }

            if (matchCount == 0)
            {
                _quickFindResultsContainer.Add(new Label(string.Format(NexusLang.Get("dash_qf_no_matches"), _quickSearchQuery))
                {
                    style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 4 }
                });
            }
        }

        private void BuildOverviewSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 12;

            card.Add(CreateSectionTitle(NexusLang.Get("project_overview"), NexusEditorStyles.AccentYellow));

            if (!s_overviewCacheValid) RefreshOverviewCache();
            int modelCount = s_cachedModelCount, serviceCount = s_cachedServiceCount, commandCount = s_cachedCommandCount, viewCount = s_cachedViewCount;

            var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _modelStat = CreateStatBox(statRow, modelCount.ToString(), NexusLang.Get("models"), NexusEditorStyles.AccentYellow);
            _serviceStat = CreateStatBox(statRow, serviceCount.ToString(), NexusLang.Get("services"), NexusEditorStyles.AccentGreen);
            _commandStat = CreateStatBox(statRow, commandCount.ToString(), NexusLang.Get("commands"), NexusEditorStyles.AccentOrange);
            _viewStat = CreateStatBox(statRow, viewCount.ToString(), NexusLang.Get("views"), NexusEditorStyles.AccentBlue);
            card.Add(statRow);

            var actionRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 6 } };
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("models"), NexusLang.Get("dash_tip_models"), NexusEditorStyles.AccentYellow, () => Window?.SwitchToPlugin("GameManager")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("services"), NexusLang.Get("dash_tip_services"), NexusEditorStyles.AccentGreen, () => Window?.SwitchToPlugin("GameManager")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("commands"), NexusLang.Get("dash_tip_commands"), NexusEditorStyles.AccentOrange, () => Window?.SwitchToPlugin("GameManager")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("views"), NexusLang.Get("dash_tip_views"), NexusEditorStyles.AccentBlue, () => Window?.SwitchToPlugin("GameManager")));
            card.Add(actionRow);

            parent.Add(card);
        }

        private void BuildRuntimeSection(VisualElement parent)
        {
            if (!Application.isPlaying) return;

            NexusRuntime.Metrics.UpdateRates();

            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;

            card.Add(CreateSectionTitle(NexusLang.Get("runtime_metrics"), NexusEditorStyles.AccentGreen));

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
                                { style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextPrimary), width = 132, unityTextAlign = TextAnchor.MiddleLeft } });

                            var t = obj.GetType();
                            int props = 0;
                            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (props >= 3) break;
                                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                                try
                                {
                                    var val = prop.GetValue(obj);
                                    var valStr = val?.ToString() ?? "null";
                                    if (valStr.Length > 16) valStr = valStr.Substring(0, 13) + "...";
                                    row.Add(new Label($"{prop.Name}={valStr}")
                                        { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.AccentBlue), marginLeft = 5 } });
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

            var actions = new[]
            {
                ("action_wizard_title", "action_wizard_desc", "Wizard", NexusEditorStyles.BtnBlue),
                ("action_hierarchy_title", "action_hierarchy_desc", "Hierarchy", NexusEditorStyles.BtnTeal),
                ("action_explorer_title", "action_explorer_desc", "Explorer", NexusEditorStyles.BtnPurple),
                ("action_tracer_title", "action_tracer_desc", "Tracer", new Color(0.5f, 0.5f, 0.6f)),
                ("action_graph_title", "action_graph_desc", "Graph", new Color(0.8f, 0.3f, 0.3f)),
                ("action_gamemanager_title", "action_gamemanager_desc", "GameManager", new Color(0.3f, 0.7f, 0.6f)),
                ("action_typeanalyzer_title", "action_typeanalyzer_desc", "TypeAnalyzer", new Color(0.6f, 0.5f, 0.5f)),
                ("action_help_title", "action_help_desc", "Help", new Color(0.5f, 0.5f, 0.8f)),
                ("action_error_dashboard_title", "action_error_dashboard_desc", "ErrorDashboard", new Color(0.8f, 0.4f, 0.4f)),
                ("action_performance_dashboard_title", "action_performance_dashboard_desc", "PerformanceDashboard", new Color(0.4f, 0.8f, 0.4f)),
                ("action_network_dashboard_title", "action_network_dashboard_desc", "NetworkDashboard", new Color(0.4f, 0.6f, 0.8f))
            };

            var buttonGrid = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    justifyContent = Justify.FlexStart,
                }
            };
            for (int i = 0; i < actions.Length; i++)
            {
                var a = actions[i];
                AddActionCard(buttonGrid, NexusLang.Get(a.Item1), NexusLang.Get(a.Item2), a.Item4, () => Window?.SwitchToPlugin(a.Item3));
            }

            groupCard.Add(buttonGrid);
        }

        private Label CreateStatBox(VisualElement parent, string value, string label, Color accentColor)
        {
            var box = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    alignItems = Align.Center,
                    paddingLeft = 4, paddingRight = 4, paddingTop = 2, paddingBottom = 2,
                }
            };

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

        private Button CreateMetricJumpButton(string label, string tooltip, Color accentColor, Action onClick)
        {
            var button = new Button(() => onClick())
            {
                text = label,
                tooltip = tooltip,
                style =
                {
                    marginRight = 6, marginTop = 4,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 4, paddingBottom = 4,
                    backgroundColor = new StyleColor(new Color(accentColor.r, accentColor.g, accentColor.b, 0.18f)),
                    color = Color.white,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    fontSize = 9,
                }
            };
            return button;
        }

        private void AddActionCard(VisualElement parent, string title, string description, Color btnColor, Action onClick)
        {
            var card = new VisualElement
            {
                style =
                {
                    width = 220, minHeight = 96,
                    paddingLeft = 10, paddingRight = 10, paddingTop = 10, paddingBottom = 10,
                    marginRight = 0, marginBottom = 0,
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopColor = new StyleColor(NexusEditorStyles.BorderColor),
                    borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor),
                    borderLeftColor = new StyleColor(NexusEditorStyles.BorderColor),
                    borderRightColor = new StyleColor(NexusEditorStyles.BorderColor)
                }
            };
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

        private void BuildHealthSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;

            var titleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
            titleRow.Add(new Label(NexusLang.Get("db_nexus_health"))
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentGreen) }
            });
            card.Add(titleRow);

            _healthSummary = new Label(BuildHealthText())
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), whiteSpace = WhiteSpace.Normal, marginBottom = 4 }
            };
            card.Add(_healthSummary);

            var note = new Label(NexusLang.Get("db_health_note"))
            {
                style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText), whiteSpace = WhiteSpace.Normal }
            };
            card.Add(note);

            parent.Add(card);
        }

        private string BuildHealthText()
        {
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();

            string readiness = Application.isPlaying ? NexusLang.Get("db_play_mode") : NexusLang.Get("db_edit_mode");

            string healthText;
            if (!Application.isPlaying && rootCount == 0)
            {
                healthText = NexusLang.Get("db_health_no_root");
            }
            else if (Application.isPlaying && contextCount == 0)
            {
                healthText = NexusLang.Get("db_health_no_context");
            }
            else
            {
                healthText = string.Format(NexusLang.Get("db_health_counts"), contextCount, handlerCount, rootCount);
            }

            return string.Format(NexusLang.Get("db_health_line"), readiness, healthText);
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
            titleRow.Add(CreateSectionTitle(NexusLang.Get("build_validation"), NexusEditorStyles.AccentOrange));

            if (BuildValidation.HasRun)
            {
                var statusPill = BuildValidation.LastRunPassed
                    ? NexusEditorStyles.CreatePill(NexusLang.Get("pass"), new Color(0.1f, 0.3f, 0.1f), NexusEditorStyles.AccentGreen)
                    : NexusEditorStyles.CreatePill(NexusLang.Get("fail"), new Color(0.3f, 0.1f, 0.1f), NexusEditorStyles.AccentRed);
                titleRow.Add(statusPill);
            }
            card.Add(titleRow);

            _validationSummary = new Label(BuildValidation.LastRunSummary)
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 4 }
            };
            card.Add(_validationSummary);

            card.Add(new Label(NexusLang.Get("db_validation_note"))
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.DimText), whiteSpace = WhiteSpace.Normal, marginBottom = 4 }
            });

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
            NexusEditorStyles.CreateInfoCard(parent, NexusLang.Get("framework"), NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBgAlt,
                NexusLang.Get("framework_desc"));
        }

        private void RefreshStats()
        {
            if (_contextStat == null) return;

            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            _contextStat.text = contextCount.ToString();
            _handlerStat.text = handlerCount.ToString();
            _rootStat.text = rootCount.ToString();

            if (_statusLabel != null)
            {
                var titleColor = playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentBlue;
                _statusLabel.text = playing ? NexusLang.Get("system_active") : NexusLang.Get("system_standby");
                _statusLabel.style.color = new StyleColor(titleColor);
            }

            if (_statusHint != null)
            {
                _statusHint.text = GetStatusHintText(playing, rootCount, contextCount);
            }

            if (_healthSummary != null)
            {
                _healthSummary.text = BuildHealthText();
            }

            if (_perfStat != null && Application.isPlaying)
            {
                NexusRuntime.Metrics.UpdateRates();
                _perfStat.text = $"{NexusRuntime.Metrics.SignalsPerSecond:F1}/s";
            }
        }
    }
}
