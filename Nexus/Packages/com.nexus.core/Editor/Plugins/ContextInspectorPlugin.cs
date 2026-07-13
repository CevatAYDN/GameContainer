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
        private enum InspectorTab { Overview, Bindings, Singletons, Services, Signals, FireSignal }
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

            var toolbar = NexusEditorStyles.CreateToolbar("🔍 CONTEXT INSPECTOR");
            _view.Add(toolbar);

            // Play mode warning banner
            _playModeWarning = new Label("⚠ Enter Play Mode to inspect live contexts")
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
            NexusRuntime.OnContextRegistered   += OnContextsChanged;
            NexusRuntime.OnContextUnregistered += OnContextsChanged;

            RefreshContextDropdown();
            RenderContent();

            _refreshSchedule = _view.schedule.Execute(OnScheduled).Every(500);

            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            NexusRuntime.OnContextRegistered   -= OnContextsChanged;
            NexusRuntime.OnContextUnregistered -= OnContextsChanged;
        }

        public override void OnUpdate()
        {
            // Update play mode warning visibility
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
                ("🔄 Refresh",     () => { RefreshContextDropdown(); RenderContent(); }, NexusEditorStyles.BtnGray),
                ("📋 Copy Report", CopyContextReport, NexusEditorStyles.BtnBlue),
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

            bar.Add(new Label("Context:")
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), marginRight = 8, minWidth = 60 }
            });

            _contextDropdown = new DropdownField { choices = new List<string> { "(none)" }, value = "(none)" };
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
            var choices = new List<string> { "(none — edit mode)" };

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
            if (ctx == null) return "(null)";
            return ctx.ScopeTag ?? ctx.GetType().Name;
        }

        // ── Tab buttons ───────────────────────────────────────────

        private Button BuildTabButton(InspectorTab tab)
        {
            var labels = new Dictionary<InspectorTab, string>
            {
                { InspectorTab.Overview,   "Overview" },
                { InspectorTab.Bindings,   "Bindings" },
                { InspectorTab.Singletons, "Singletons" },
                { InspectorTab.Services,   "Services" },
                { InspectorTab.Signals,    "Signals" },
                { InspectorTab.FireSignal, "🔥 Fire Signal" },
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
                _content.Add(new Label("Start Play Mode to inspect live contexts.")
                {
                    style = { color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 20, unityTextAlign = TextAnchor.MiddleCenter }
                });
                return;
            }

            if (_selectedContext == null)
            {
                _content.Add(new Label("Select a context from the dropdown above.")
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
                case InspectorTab.FireSignal:  RenderFireSignal(); break;
            }
        }

        // ── Overview tab ──────────────────────────────────────────

        private void RenderOverview()
        {
            AddSectionTitle("📋 Context Overview");

            var ctx = _selectedContext;
            var concrete   = _selectedContext as Context;
            var bindings   = NexusEditorDataProvider.GetAllBindings(ctx);
            var singletons = NexusEditorDataProvider.GetResolvedSingletons(ctx);
            var handlers   = ctx.SignalBus?.RegisteredHandlers;
            var plugins    = concrete?.PluginsReadOnlyCopy;

            _content.Add(NexusEditorStyles.CreateStatRow("Tag",          ctx.ScopeTag ?? "(no tag)", NexusEditorStyles.AccentBlue));
            _content.Add(NexusEditorStyles.CreateStatRow("Type",         ctx.GetType().Name, NexusEditorStyles.TextPrimary));
            _content.Add(NexusEditorStyles.CreateStatRow("Parent",       ctx.Parent != null ? (ctx.Parent.ScopeTag ?? ctx.Parent.GetType().Name) : "none"));
            _content.Add(NexusEditorStyles.CreateStatRow("DI Bindings",  $"{bindings.Count}", NexusEditorStyles.AccentGreen));
            _content.Add(NexusEditorStyles.CreateStatRow("Singletons",   $"{singletons.Count}", NexusEditorStyles.AccentBlue));
            _content.Add(NexusEditorStyles.CreateStatRow("Signals",      $"{handlers?.Count ?? 0}", NexusEditorStyles.AccentPurple));
            _content.Add(NexusEditorStyles.CreateStatRow("Plugins",      $"{plugins?.Count ?? 0}", NexusEditorStyles.AccentOrange));
            _content.Add(NexusEditorStyles.CreateStatRow("Has Interceptors", (concrete?.HasInterceptors ?? false).ToString(), NexusEditorStyles.TextSecondary));

            // Child contexts
            var allContexts = NexusRuntime.ActiveContexts;
            var children = allContexts?.Where(c => c.Parent == ctx).ToList();
            if (children?.Count > 0)
            {
                _content.Add(NexusEditorStyles.CreateStatRow("Child Contexts", $"{children.Count}", NexusEditorStyles.AccentGreen));
                foreach (var child in children)
                {
                    _content.Add(NexusEditorStyles.CreateStatRow("  ↳", child.ScopeTag ?? child.GetType().Name, NexusEditorStyles.TextSecondary));
                }
            }

            // Plugins list
            if (plugins?.Count > 0)
            {
                _content.Add(MakeSpacer(8));
                AddSectionTitle("🔌 Runtime Plugins");
                foreach (var (plugin, _) in plugins)
                {
                    _content.Add(NexusEditorStyles.CreateStatRow("  Plugin", plugin.GetType().Name, NexusEditorStyles.AccentOrange));
                }
            }
        }

        // ── Bindings tab ──────────────────────────────────────────

        private void RenderBindings()
        {
            AddSectionTitle("🔗 DI Bindings");

            var bindings = NexusEditorDataProvider.GetAllBindings(_selectedContext);
            if (bindings.Count == 0)
            {
                AddEmpty("No bindings found. (Play Mode required)");
                return;
            }

            var filtered = bindings
                .Where(kv => string.IsNullOrEmpty(_searchFilter)
                    || kv.Key.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    || kv.Value.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(kv => kv.Key.Name)
                .ToList();

            var table = NexusEditorStyles.CreateDataTable(
                new[] { ("Interface / Key", 0.5f), ("Concrete Type", 0.5f) },
                filtered.Select(kv => new[] { kv.Key.Name, kv.Value.Name })
            );
            _content.Add(table);

            _content.Add(new Label($"Total: {bindings.Count} binding(s)")
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
        }

        // ── Singletons tab ────────────────────────────────────────

        private void RenderSingletons()
        {
            AddSectionTitle("📦 Resolved Singletons");

            var singletons = NexusEditorDataProvider.GetResolvedSingletons(_selectedContext);
            if (singletons.Count == 0)
            {
                AddEmpty("No singletons resolved yet.");
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
                    card.Add(new Label("  implements: " + string.Join(", ", interfaces))
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
                        card.Add(NexusEditorStyles.CreateStatRow($"  .{prop.Name}", valStr));
                    }
                    catch { }
                }

                _content.Add(card);
            }

            _content.Add(new Label($"Total: {singletons.Count} singleton(s)")
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 4 }
            });
        }

        // ── Services tab ──────────────────────────────────────────

        private void RenderServices()
        {
            AddSectionTitle("⚙️ Registered Services");

            var serviceTypes = NexusEditorDataProvider.GetLiveServiceTypes(_selectedContext);
            if (serviceTypes.Count == 0)
            {
                AddEmpty("No services registered in this context.");
                return;
            }

            var rows = serviceTypes
                .Where(t => string.IsNullOrEmpty(_searchFilter)
                    || t.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.Name)
                .Select(t =>
                {
                    var inst = NexusEditorDataProvider.TryGetServiceInstance(_selectedContext, t);
                    return new[] { t.Name, inst?.GetType().Name ?? "not resolved", inst != null ? "✓" : "—" };
                });

            _content.Add(NexusEditorStyles.CreateDataTable(
                new[] { ("Service Type", 0.45f), ("Concrete", 0.4f), ("Resolved", 0.15f) },
                rows
            ));

            _content.Add(new Label($"Total: {serviceTypes.Count} service(s)")
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
        }

        // ── Signals tab ───────────────────────────────────────────

        private void RenderSignals()
        {
            AddSectionTitle("⚡ Registered Signal Handlers");

            var handlers = _selectedContext.SignalBus?.RegisteredHandlers;
            if (handlers == null || handlers.Count == 0)
            {
                AddEmpty("No signal handlers registered in this context.");
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

            _content.Add(NexusEditorStyles.CreateDataTable(
                new[] { ("Signal", 0.4f), ("Command Handler", 0.4f), ("Mode", 0.2f) },
                rows
            ));

            _content.Add(new Label($"Total signals: {handlers.Count}")
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            });
        }

        // ── Fire Signal tab ───────────────────────────────────────

        private void RenderFireSignal()
        {
            AddSectionTitle("🔥 Fire Test Signal");

            if (_selectedContext == null)
            {
                AddEmpty("Select a context first.");
                return;
            }

            // Signal type dropdown
            var allSignalTypes = GetAvailableSignalTypes();
            if (allSignalTypes.Count == 0)
            {
                AddEmpty("No signal types found in loaded assemblies.");
                return;
            }

            var typeNames = allSignalTypes.Select(t => t.FullName ?? t.Name).ToList();
            var dropdown = new DropdownField("Signal Type", typeNames, 0);
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
                _signalFormContainer.Add(new Label("(select a signal type)"));
                _content.Add(_signalFormContainer);
                return;
            }

            try
            {
                _fireSignalInstance = Activator.CreateInstance(_fireSignalType);
            }
            catch
            {
                _signalFormContainer.Add(new Label("Cannot instantiate signal (no default constructor).")
                {
                    style = { color = new StyleColor(NexusEditorStyles.AccentRed) }
                });
                _content.Add(_signalFormContainer);
                return;
            }

            _fireSignalFields = _fireSignalType.GetFields(BindingFlags.Public | BindingFlags.Instance);

            if (_fireSignalFields.Length == 0)
            {
                _signalFormContainer.Add(new Label("(no public fields — signal is parameter-less)")
                {
                    style = { color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 8, fontSize = 9 }
                });
            }
            else
            {
                _signalFormContainer.Add(new Label("Fill in signal fields:")
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
                        row.Add(new Label($"({field.FieldType.Name} — not editable)")
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
                    resultLabel.text = "❌ No context or signal selected.";
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
                        resultLabel.text = $"✅ Fired {_fireSignalType.Name} at {DateTime.Now:HH:mm:ss.fff}";
                        resultLabel.style.color = new StyleColor(NexusEditorStyles.AccentGreen);
                    }
                    else
                    {
                        resultLabel.text = "❌ Fire<T> method not found on SignalBus.";
                        resultLabel.style.color = new StyleColor(NexusEditorStyles.AccentRed);
                    }
                }
                catch (Exception ex)
                {
                    resultLabel.text = $"❌ Error: {ex.InnerException?.Message ?? ex.Message}";
                    resultLabel.style.color = new StyleColor(NexusEditorStyles.AccentRed);
                    Debug.LogException(ex);
                }
            })
            {
                text = $"🔥 Fire {_fireSignalType?.Name ?? "Signal"}",
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
            if (Application.isPlaying && _activeTab == InspectorTab.Singletons)
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
