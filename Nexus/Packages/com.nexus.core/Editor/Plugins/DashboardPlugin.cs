using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
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
        public override string Category => "cat_status";

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
        private Label _validationSummary;
        private Label _healthSummary;
        private VisualElement _validationCard;
        // Last "Check now" result: null = not checked, true = binder current, false = stale.
        private static bool? s_binderCurrent;
        private static int s_diIssueCount = -1;

        private static int s_cachedModelCount = -1, s_cachedServiceCount = -1, s_cachedCommandCount = -1, s_cachedViewCount = -1;
        private static bool s_overviewCacheValid = false;

        private static List<(Type type, string category, Color color)> s_typedCatalog;
        private static bool s_catalogValid = false;

        private bool _quickFindDirty;
        private double _quickFindDirtyAt;

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

            foreach (var assembly in AssemblyCatalog.GameAssemblies())
            {
                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
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

            var scroll = new ScrollView();
            scroll.AddToClassList("nexus-scroll-content");
            scroll.style.flexGrow = 1;

            BuildStatusSection(scroll);
            BuildQuickFindSection(scroll);
            BuildOverviewSection(scroll);

            BuildHealthSection(scroll);
            BuildValidationSection(scroll);
            BuildReadinessSection(scroll);
            BuildFrameworkInfo(scroll);

            _view.Add(scroll);
            return _view;
        }

        public override void OnDisable()
        {
            _quickFindDirty = false;
            _quickFindDirtyAt = 0d;
            base.OnDisable();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            RefreshStats();

            if (!_quickFindDirty || _quickFindResultsContainer == null)
                return;

            if (EditorApplication.timeSinceStartup - _quickFindDirtyAt < 0.2)
                return;

            _quickFindDirty = false;
            UpdateQuickFindResults();
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
            var statusRow = new VisualElement();
            statusRow.AddToClassList("nexus-status-card-top");

            var statusDot = NexusEditorStyles.CreateStatusDot(titleColor, 12);
            statusRow.Add(statusDot);

            _statusLabel = new Label(playing ? NexusLang.Get("system_active") : NexusLang.Get("system_standby"));
            _statusLabel.AddToClassList("nexus-status-card-title");
            _statusLabel.style.color = new StyleColor(titleColor);
            statusRow.Add(_statusLabel);
            statusCard.Add(statusRow);

            var statRow1 = new VisualElement();
            statRow1.style.flexDirection = FlexDirection.Row;
            statRow1.style.marginTop = 8;
            _contextStat = CreateStatBox(statRow1, contextCount.ToString(), NexusLang.Get("contexts"), NexusEditorStyles.AccentBlue);
            _handlerStat = CreateStatBox(statRow1, handlerCount.ToString(), NexusLang.Get("handlers"), NexusEditorStyles.AccentPurple);
            _rootStat = CreateStatBox(statRow1, rootCount.ToString(), NexusLang.Get("roots"), NexusEditorStyles.AccentYellow);
            statusCard.Add(statRow1);

            var actionRow = new VisualElement();
            actionRow.AddToClassList("nexus-actions-row");
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("contexts"), NexusLang.Get("dash_tip_contexts"), NexusEditorStyles.AccentBlue, () => Window?.SwitchToPlugin("ContextInspector")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("handlers"), NexusLang.Get("dash_tip_handlers"), NexusEditorStyles.AccentPurple, () => Window?.SwitchToPlugin("Architecture")));
            statusCard.Add(actionRow);

            string hintText = GetStatusHintText(playing, rootCount, contextCount);
            _statusHint = NexusEditorStyles.CreateHint(hintText);
            _statusHint.AddToClassList("nexus-status-hint");
            statusCard.Add(_statusHint);

            parent.Add(statusCard);
        }

        private string GetStatusHintText(bool playing, int rootCount, int contextCount)
            => DashboardSections.GetStatusHintText(playing, rootCount, contextCount);

        private string _quickSearchQuery = "";
        private VisualElement _quickFindResultsContainer;

        private void BuildQuickFindSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            card.style.marginTop = 12;

            card.Add(DashboardSections.CreateSectionTitle(NexusLang.Get("db_quick_find"), NexusEditorStyles.AccentBlue));
            card.Add(CreateQuickFindRow());

            _quickFindResultsContainer = new VisualElement { style = { marginTop = 8 } };
            card.Add(_quickFindResultsContainer);

            parent.Add(card);

            if (!string.IsNullOrEmpty(_quickSearchQuery))
            {
                UpdateQuickFindResults();
            }
        }


        private VisualElement CreateQuickFindRow()
        {
            var searchRow = new VisualElement();
            searchRow.AddToClassList("nexus-search-row");
            var searchInput = new TextField
            {
                value = _quickSearchQuery,
                tooltip = NexusLang.Get("dash_quickfind_tooltip"),
            };
            searchInput.AddToClassList(NexusEditorStyles.ClassSearchField);

            searchInput.RegisterValueChangedCallback(evt =>
            {
                _quickSearchQuery = evt.newValue;
                _quickFindDirty = true;
                _quickFindDirtyAt = EditorApplication.timeSinceStartup;
            });

            searchRow.Add(searchInput);

            var clearBtn = new Button(() =>
            {
                searchInput.value = "";
                _quickSearchQuery = "";
                _quickFindDirty = false;
                UpdateQuickFindResults();
            }) { text = NexusLang.Get("clear") };
            clearBtn.AddToClassList("nexus-search-clear-btn");
            searchRow.Add(clearBtn);
            return searchRow;
        }

        private void UpdateQuickFindResults()
        {
            if (_quickFindResultsContainer == null) return;
            _quickFindResultsContainer.Clear();

            if (string.IsNullOrWhiteSpace(_quickSearchQuery)) return;

            string query = _quickSearchQuery.Trim();

            if (!s_catalogValid || s_typedCatalog == null)
            {
                RefreshOverviewCache();
            }

            int matchCount = 0;
            foreach (var (type, category, color) in s_typedCatalog)
            {
                if (matchCount >= 10) break;

                if (type.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var resultRow = new VisualElement();
                    resultRow.AddToClassList(NexusEditorStyles.ClassQfRow);

                    var catPill = NexusEditorStyles.CreatePill(category, new Color(color.r, color.g, color.b, 0.2f), color);
                    catPill.style.marginRight = 8;
                    resultRow.Add(catPill);

                    var nameLabel = new Label(type.Name);
                    nameLabel.AddToClassList("nexus-qf-name");
                    resultRow.Add(nameLabel);

                    var targetType = type;

                    var copyBtn = new Button(() =>
                    {
                        EditorGUIUtility.systemCopyBuffer = targetType.FullName;
                    }) { text = NexusLang.Get("dash_qf_copy") };
                    copyBtn.AddToClassList(NexusEditorStyles.ClassQfBtn);
                    copyBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.BtnGray);
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
                    }) { text = NexusLang.Get("open") };
                    openBtn.AddToClassList(NexusEditorStyles.ClassQfBtn);
                    openBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.BtnBlue);
                    resultRow.Add(openBtn);

                    _quickFindResultsContainer.Add(resultRow);
                    matchCount++;
                }
            }

            if (matchCount == 0)
            {
                var noMatch = new Label(string.Format(NexusLang.Get("dash_qf_no_matches"), _quickSearchQuery));
                noMatch.style.fontSize = 10;
                noMatch.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
                noMatch.style.marginTop = 4;
                _quickFindResultsContainer.Add(noMatch);
            }
        }

        private void BuildOverviewSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 12;

            card.Add(DashboardSections.CreateSectionTitle(NexusLang.Get("project_overview"), NexusEditorStyles.AccentYellow));

            if (!s_overviewCacheValid) RefreshOverviewCache();
            int modelCount = s_cachedModelCount, serviceCount = s_cachedServiceCount, commandCount = s_cachedCommandCount, viewCount = s_cachedViewCount;

            var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _modelStat = CreateStatBox(statRow, modelCount.ToString(), NexusLang.Get("models"), NexusEditorStyles.AccentYellow);
            _serviceStat = CreateStatBox(statRow, serviceCount.ToString(), NexusLang.Get("services"), NexusEditorStyles.AccentGreen);
            _commandStat = CreateStatBox(statRow, commandCount.ToString(), NexusLang.Get("commands"), NexusEditorStyles.AccentOrange);
            _viewStat = CreateStatBox(statRow, viewCount.ToString(), NexusLang.Get("views"), NexusEditorStyles.AccentBlue);
            card.Add(statRow);

            var actionRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 6 } };
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("models"), NexusLang.Get("dash_tip_models"), NexusEditorStyles.AccentYellow, () => Window?.SwitchToPlugin("Architecture")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("services"), NexusLang.Get("dash_tip_services"), NexusEditorStyles.AccentGreen, () => Window?.SwitchToPlugin("Architecture")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("commands"), NexusLang.Get("dash_tip_commands"), NexusEditorStyles.AccentOrange, () => Window?.SwitchToPlugin("Architecture")));
            actionRow.Add(CreateMetricJumpButton(NexusLang.Get("views"), NexusLang.Get("dash_tip_views"), NexusEditorStyles.AccentBlue, () => Window?.SwitchToPlugin("Architecture")));
            card.Add(actionRow);

            parent.Add(card);
        }

        // Delegated to DashboardSections — keep these shims for backward compat
        private Label CreateStatBox(VisualElement parent, string value, string label, Color accentColor)
            => DashboardSections.CreateStatBox(parent, value, label, accentColor);

        private Button CreateMetricJumpButton(string label, string tooltip, Color accentColor, Action onClick)
            => DashboardSections.CreateMetricJumpButton(label, tooltip, accentColor, onClick);

        private void BuildValidationSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;
            _validationCard = card;
            PopulateValidationCard(card);
            parent.Add(card);
        }

        private void BuildReadinessSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;
            PopulateReadinessCard(card);
            parent.Add(card);
        }

        /// <summary>
        /// Build readiness: the facts a release build depends on, read from the same sources the
        /// build guard and the runtime use (binder freshness is a dry run of the generator, so it is
        /// computed on request, not every tick).
        /// </summary>
        private void PopulateReadinessCard(VisualElement card)
        {
            card.Clear();
            var titleRow = new VisualElement();
            titleRow.AddToClassList("nexus-status-card-top");
            titleRow.Add(DashboardSections.CreateSectionTitle(NexusLang.Get("db_build_readiness"), NexusEditorStyles.AccentGreen));
            card.Add(titleRow);

            bool binderExists = NexusCodeGenerator.BinderExists();
            string binderText; Color binderColor;
            if (!binderExists) { binderText = NexusLang.Get("db_rd_binder_missing"); binderColor = NexusEditorStyles.AccentYellow; }
            else if (s_binderCurrent == null) { binderText = NexusLang.Get("db_rd_binder_unknown"); binderColor = NexusEditorStyles.AccentBlue; }
            else if (s_binderCurrent == true) { binderText = NexusLang.Get("db_rd_binder_current"); binderColor = NexusEditorStyles.AccentGreen; }
            else { binderText = NexusLang.Get("db_rd_binder_stale"); binderColor = NexusEditorStyles.AccentRed; }
            AddReadinessRow(card, NexusLang.Get("db_rd_binder"), binderText, binderColor, "readiness-binder");

            var settings = NexusEditorSettings.GetOrCreateSettings();
            bool linkXml = File.Exists(Path.Combine(settings.LinkXmlOutputPath, "link.xml"));
            AddReadinessRow(card, NexusLang.Get("db_rd_linkxml"), linkXml ? NexusLang.Get("db_rd_present") : NexusLang.Get("db_rd_missing"),
                linkXml ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentYellow, "readiness-linkxml");

            var target = EditorUserBuildSettings.activeBuildTarget;
            AddReadinessRow(card, NexusLang.Get("db_rd_target"), target.ToString(), NexusEditorStyles.AccentBlue, "readiness-target");
            string defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target)));
            AddReadinessRow(card, NexusLang.Get("db_rd_defines"), string.IsNullOrEmpty(defines) ? NexusLang.Get("db_rd_defines_none") : defines,
                NexusEditorStyles.AccentBlue, "readiness-defines");

            var stale = NexusDI.GetStaleGeneratedInjectorTypes();
            AddReadinessRow(card, NexusLang.Get("db_rd_stale_injectors"),
                stale.Length == 0 ? NexusLang.Get("db_rd_none") : string.Join(", ", stale.Select(t => t.Name)),
                stale.Length == 0 ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentRed, "readiness-stale");

            int live = NexusDI.GeneratedRegistrationCount;
            bool binderDead = Application.isPlaying && binderExists && live == 0;
            AddReadinessRow(card, NexusLang.Get("db_rd_generated_live"), live.ToString(),
                binderDead ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentBlue, "readiness-live");

            string issues = !Application.isPlaying ? NexusLang.Get("db_rd_play_only")
                : s_diIssueCount < 0 ? NexusLang.Get("db_rd_binder_unknown") : s_diIssueCount.ToString();
            AddReadinessRow(card, NexusLang.Get("db_rd_di_issues"), issues,
                s_diIssueCount > 0 ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentBlue, "readiness-di");

            var actions = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };
            actions.Add(NexusEditorStyles.CreateButton(NexusLang.Get("db_rd_check"), () =>
            {
                s_binderCurrent = NexusCodeGenerator.BinderExists() ? NexusCodeGenerator.IsBinderCurrent() : (bool?)null;
                if (Application.isPlaying)
                {
                    int count = 0;
                    foreach (var context in NexusRuntime.ActiveContexts)
                        if (context?.Container != null) count += context.Container.ValidateBindings().Count;
                    s_diIssueCount = count;
                }
                PopulateReadinessCard(card);
            }, NexusEditorStyles.BtnBlue));
            actions.Add(NexusEditorStyles.CreateButton(NexusLang.Get("db_rd_generate"), () =>
            {
                NexusCodeGenerator.GenerateBinder();
                s_binderCurrent = true;
                PopulateReadinessCard(card);
            }, NexusEditorStyles.BtnTeal));
            card.Add(actions);
            card.Add(NexusEditorStyles.CreateHint(NexusLang.Get("db_rd_note")));
        }

        private static void AddReadinessRow(VisualElement card, string label, string value, Color accent, string name)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 3 } };
            var key = NexusEditorStyles.CreateBody(label);
            key.style.minWidth = 220;
            key.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
            row.Add(key);
            var pill = NexusEditorStyles.CreatePill(value, new Color(accent.r * 0.18f, accent.g * 0.18f, accent.b * 0.18f), accent);
            pill.name = name;
            row.Add(pill);
            card.Add(row);
        }

        private void BuildHealthSection(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 8;

            var titleRow = new VisualElement();
            titleRow.AddToClassList("nexus-status-card-top");
            var healthTitle = new Label(NexusLang.Get("db_nexus_health"));
            healthTitle.AddToClassList("nexus-health-title");
            titleRow.Add(healthTitle);
            card.Add(titleRow);

            _healthSummary = new Label(BuildHealthText());
            _healthSummary.AddToClassList("nexus-health-text");
            card.Add(_healthSummary);

            var note = new Label(NexusLang.Get("db_health_note"));
            note.AddToClassList("nexus-health-note");
            card.Add(note);

            parent.Add(card);
        }

        private string BuildHealthText() => DashboardSections.BuildHealthText();

        private void RefreshValidationCard(VisualElement card)
        {
            if (card == null) return;
            card.Clear();
            PopulateValidationCard(card);
        }

        private void PopulateValidationCard(VisualElement card)
        {
            var titleRow = new VisualElement();
            titleRow.AddToClassList("nexus-status-card-top");
            titleRow.Add(DashboardSections.CreateSectionTitle(NexusLang.Get("build_validation"), NexusEditorStyles.AccentOrange));

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
                string.Format(NexusLang.Get("framework_desc"), NexusLang.PackageVersion));
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
        }
    }
}
