using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class TracerPlugin : NexusEditorPlugin, INexusTraceSink
    {
        public override string Id => "Tracer";
        public override string DisplayName => "Live Tracer";
        public override int Order => 4;

        private VisualElement _view;
        private ScrollView _tracerScrollView;
        private Toggle _pauseToggle;
        private VisualElement _detailPanel;
        private Label _detailContent;
        private TextField _searchField;

        private bool _isPaused = false;
        private string _searchFilter = "";
        
        private bool _filterSignal = true;
        private bool _filterCommand = true;
        private bool _filterModelChange = true;
        private bool _filterOk = true;
        private bool _filterFailed = true;
        private bool _filterCancelled = true;

        private int _selectedEventId = -1;

        // Thread-safe buffer for incoming trace events
        private readonly ConcurrentQueue<TraceEvent> _incomingEvents = new();
        private readonly List<TraceEvent> _allEvents = new();
        private readonly List<TraceEventElement> _renderedItems = new();
        private readonly Dictionary<int, List<TraceEvent>> _childrenCache = new();
        private readonly Dictionary<int, TraceEvent> _parentCache = new();
        private readonly Dictionary<int, int> _depthsCache = new();

        private IVisualElementScheduledItem _updateSchedule;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("tracer_title"));
            _view.Add(toolbar);

#if !NEXUS_DEBUG
            var warningCard = NexusEditorStyles.CreateInfoCard(
                _view,
                "CAUSAL TRACING: NEXUS_DEBUG DISABLED",
                NexusEditorStyles.AccentOrange,
                NexusEditorStyles.CardBgYellow,
                "Full causal tracing (event trees, parent/child chains) is compiled out.\n" +
                "Basic production trace is active below — showing recent signal dispatches.");

            var enableBtn = NexusEditorStyles.CreateButton(NexusLang.Get("tracer_enable"), () =>
            {
                BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
                var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group);
                string defines = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
                var defineList = new List<string>(defines.Split(';', StringSplitOptions.RemoveEmptyEntries));
                if (!defineList.Contains("NEXUS_DEBUG"))
                {
                    defineList.Add("NEXUS_DEBUG");
                    PlayerSettings.SetScriptingDefineSymbols(namedTarget, string.Join(";", defineList));
                }
                AssetDatabase.SaveAssets();
                Debug.Log("[Nexus] Added NEXUS_DEBUG scripting define symbol. Recompiling...");
            }, NexusEditorStyles.BtnBlue);

            warningCard.Add(enableBtn);
#endif

            // Filter Bar
            var filterBar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 6,
                    paddingBottom = 6,
                    borderBottomWidth = 1,
                    borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor),
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap
                }
            };

            _searchField = new TextField { value = _searchFilter, style = { width = 120, height = 20 } };
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue;
                RefreshTracerLogs();
            });
            filterBar.Add(_searchField);

            filterBar.Add(new Label("  ") { style = { width = 5 } });

            var clearBtn = new Button(ClearTraces) { text = "Clear" };
            clearBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.BtnGray);
            clearBtn.style.color = Color.white;
            filterBar.Add(clearBtn);

            _pauseToggle = new Toggle("Pause") { value = _isPaused, style = { marginLeft = 10, color = Color.white } };
            _pauseToggle.RegisterValueChangedCallback(evt =>
            {
                _isPaused = evt.newValue;
                if (!_isPaused)
                {
                    // Dequeue all buffered events during pause
                    ProcessIncomingQueue();
                }
                RefreshTracerLogs();
            });
            filterBar.Add(_pauseToggle);

            filterBar.Add(new Label(NexusLang.Get("tracer_type_filter")) { style = { fontSize = 10, color = Color.gray, marginLeft = 10 } });
            filterBar.Add(MakeFilterButton("SIG", () => { _filterSignal = !_filterSignal; RefreshTracerLogs(); }, () => _filterSignal));
            filterBar.Add(MakeFilterButton("CMD", () => { _filterCommand = !_filterCommand; RefreshTracerLogs(); }, () => _filterCommand));
            filterBar.Add(MakeFilterButton("MOD", () => { _filterModelChange = !_filterModelChange; RefreshTracerLogs(); }, () => _filterModelChange));

            filterBar.Add(new Label(NexusLang.Get("tracer_status_filter")) { style = { fontSize = 10, color = Color.gray, marginLeft = 10 } });
            filterBar.Add(MakeFilterButton("OK", () => { _filterOk = !_filterOk; RefreshTracerLogs(); }, () => _filterOk, NexusEditorStyles.AccentGreen));
            filterBar.Add(MakeFilterButton("FAIL", () => { _filterFailed = !_filterFailed; RefreshTracerLogs(); }, () => _filterFailed, NexusEditorStyles.AccentRed));
            filterBar.Add(MakeFilterButton("CANCEL", () => { _filterCancelled = !_filterCancelled; RefreshTracerLogs(); }, () => _filterCancelled, NexusEditorStyles.AccentYellow));

            _view.Add(filterBar);

            // Split view for logs and details
            var splitPane = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };

            _tracerScrollView = new ScrollView { style = { width = new Length(60, LengthUnit.Percent), paddingLeft = 10, paddingRight = 10, paddingTop = 10 } };
            _tracerScrollView.style.borderRightWidth = 1;
            _tracerScrollView.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            splitPane.Add(_tracerScrollView);

            _detailPanel = new VisualElement { style = { width = new Length(40, LengthUnit.Percent), backgroundColor = new StyleColor(NexusEditorStyles.DarkPanel), paddingLeft = 12, paddingRight = 12, paddingTop = 12, display = DisplayStyle.None } };
            _detailContent = new Label { style = { color = new StyleColor(NexusEditorStyles.TextPrimary), fontSize = 10, whiteSpace = WhiteSpace.Normal } };
            _detailPanel.Add(_detailContent);
            splitPane.Add(_detailPanel);

            _view.Add(splitPane);

            // Fetch causal events from ring buffer (NEXUS_DEBUG only)
            var events = NexusTrace.GetRecentEvents(out int count);
            for (int i = 0; i < count; i++)
            {
                _allEvents.Add(events[i]);
            }

            // Production trace fallback: load from Metrics ring buffer
            // This works even without NEXUS_DEBUG
            if (_allEvents.Count == 0)
            {
                var traces = NexusRuntime.Metrics.GetRecentTraces(out int traceCount);
                for (int i = 0; i < traceCount && _allEvents.Count < 200; i++)
                {
                    if (!string.IsNullOrEmpty(traces[i]))
                    {
                        _allEvents.Add(new TraceEvent(i, -1, TraceEventType.Signal,
                            UnityEngine.Time.realtimeSinceStartupAsDouble, traces[i],
                            TraceStatus.OK, ExecutionMode.Sequential));
                    }
                }
            }

            BuildChildrenCache();
            RefreshTracerLogs();

            // Register trace sink
            NexusTrace.AddSink(this);

            // Start main-thread queue processor schedule
            _updateSchedule = _view.schedule.Execute(OnMainThreadUpdate).Every(100);

            return _view;
        }

        public override void OnDisable()
        {
            NexusTrace.RemoveSink(this);
            _updateSchedule?.Pause();
            _allEvents.Clear();
            _renderedItems.Clear();
            _childrenCache.Clear();
            _parentCache.Clear();
            _depthsCache.Clear();
        }

        public void Write(in TraceEvent traceEvent)
        {
            // Enqueue event from background thread
            _incomingEvents.Enqueue(traceEvent);
        }

        private int _productionTraceFrameCounter;

        private void OnMainThreadUpdate()
        {
            if (!_isPaused)
            {
                bool hasNewEvents = ProcessIncomingQueue();

                // Reload production traces every ~500ms (5 frames at 100ms interval)
                _productionTraceFrameCounter++;
                if (_productionTraceFrameCounter >= 5)
                {
                    _productionTraceFrameCounter = 0;
                    ReloadProductionTraces();
                    hasNewEvents = true;
                }

                if (hasNewEvents)
                {
                    RefreshTracerLogs();
                }
            }
        }

        private void ReloadProductionTraces()
        {
            if (_allEvents.Count >= 200) return;
            var traces = NexusRuntime.Metrics.GetRecentTraces(out int traceCount);
            for (int i = 0; i < traceCount && _allEvents.Count < 200; i++)
            {
                if (!string.IsNullOrEmpty(traces[i]))
                {
                    _allEvents.Add(new TraceEvent(
                        _allEvents.Count, -1, TraceEventType.Signal,
                        UnityEngine.Time.realtimeSinceStartupAsDouble, traces[i],
                        TraceStatus.OK, ExecutionMode.Sequential));
                }
            }
            if (_allEvents.Count > 0) BuildChildrenCache();
        }

        private bool ProcessIncomingQueue()
        {
            bool addedAny = false;
            while (_incomingEvents.TryDequeue(out var ev))
            {
                _allEvents.Add(ev);
                addedAny = true;
            }

            if (_allEvents.Count > 5000)
            {
                _allEvents.RemoveRange(0, _allEvents.Count - 5000);
            }

            if (addedAny)
            {
                BuildChildrenCache();
            }
            return addedAny;
        }

        private void BuildChildrenCache()
        {
            _childrenCache.Clear();
            _parentCache.Clear();
            // Build an id -> event lookup first, then wire up parent relationships
            var eventById = new Dictionary<int, TraceEvent>();
            foreach (var ev in _allEvents)
                eventById[ev.Id] = ev;

            foreach (var ev in _allEvents)
            {
                if (ev.ParentId != -1)
                {
                    if (!_childrenCache.TryGetValue(ev.ParentId, out var list))
                    {
                        list = new List<TraceEvent>();
                        _childrenCache[ev.ParentId] = list;
                    }
                    list.Add(ev);
                    if (eventById.TryGetValue(ev.ParentId, out var parent))
                        _parentCache[ev.Id] = parent;
                }
            }
        }

        private void RefreshTracerLogs()
        {
            if (_tracerScrollView == null) return;
            _tracerScrollView.Clear();
            _renderedItems.Clear();

            if (!Application.isPlaying)
            {
                _tracerScrollView.Add(new Label(NexusLang.Get("tracer_offline")) { style = { color = Color.gray, alignSelf = Align.Center, marginTop = 20 } });
                _detailPanel.style.display = DisplayStyle.None;
                return;
            }

            var filtered = GetFilteredEvents();
            int filteredCount = filtered.Count;

            if (filteredCount == 0)
            {
                return;
            }

            const int maxDisplayCount = 200;
            int startIndex = Math.Max(0, filteredCount - maxDisplayCount);

            _depthsCache.Clear();

            for (int i = 0; i < filteredCount; i++)
            {
                var ev = filtered[i];
                int depth = 0;
                if (ev.ParentId != -1 && _depthsCache.TryGetValue(ev.ParentId, out int parentDepth))
                {
                    depth = parentDepth + 1;
                }
                _depthsCache[ev.Id] = depth;

                if (i >= startIndex)
                {
                    var item = CreateTraceElement(ev, depth);
                    _tracerScrollView.Add(item);
                    _renderedItems.Add(item);
                }
            }
        }

        private List<TraceEvent> GetFilteredEvents()
        {
            var result = new List<TraceEvent>(_allEvents.Count);
            foreach (var ev in _allEvents)
            {
                // Type Filter
                if (ev.Type == TraceEventType.Signal && !_filterSignal) continue;
                if (ev.Type == TraceEventType.Command && !_filterCommand) continue;
                if (ev.Type == TraceEventType.ModelChange && !_filterModelChange) continue;

                // Status Filter
                if (ev.Status == TraceStatus.OK && !_filterOk) continue;
                if (ev.Status == TraceStatus.Failed && !_filterFailed) continue;
                if (ev.Status == TraceStatus.Cancelled && !_filterCancelled) continue;

                // Name Filter
                if (!string.IsNullOrEmpty(_searchFilter))
                {
                    if (ev.TypeName.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                result.Add(ev);
            }
            return result;
        }

        private Button MakeFilterButton(string label, Action onClick, Func<bool> isActive, Color? activeColor = null)
        {
            var activeBg = activeColor ?? NexusEditorStyles.BtnBlue;
            Button btn = null;
            btn = new Button(() =>
            {
                onClick();
                UpdateFilterButtonStyle(btn, isActive(), activeBg);
            }) { text = label };
            
            btn.style.fontSize = 8;
            btn.style.paddingLeft = 4;
            btn.style.paddingRight = 4;
            btn.style.paddingTop = 1;
            btn.style.paddingBottom = 1;
            btn.style.marginLeft = 2;
            btn.style.marginRight = 2;
            btn.style.borderTopLeftRadius = 2;
            btn.style.borderTopRightRadius = 2;
            btn.style.borderBottomLeftRadius = 2;
            btn.style.borderBottomRightRadius = 2;
            UpdateFilterButtonStyle(btn, isActive(), activeBg);
            return btn;
        }

        private void UpdateFilterButtonStyle(Button btn, bool active, Color activeBg)
        {
            if (active)
            {
                btn.style.backgroundColor = new StyleColor(activeBg);
                btn.style.color = Color.white;
            }
            else
            {
                btn.style.backgroundColor = new StyleColor(NexusEditorStyles.RowAlt);
                btn.style.color = new StyleColor(NexusEditorStyles.DimText);
            }
        }

        private void ClearTraces()
        {
            NexusTrace.Reset();
            _allEvents.Clear();
            _renderedItems.Clear();
            _childrenCache.Clear();
            _parentCache.Clear();
            _depthsCache.Clear();
            _selectedEventId = -1;
            
            if (_tracerScrollView != null) _tracerScrollView.Clear();
            if (_detailPanel != null) _detailPanel.style.display = DisplayStyle.None;
        }

        private void OnTraceEventClicked(TraceEvent ev)
        {
            if (_selectedEventId == ev.Id)
            {
                _detailPanel.style.display = DisplayStyle.None;
                _selectedEventId = -1;
                RefreshTracerLogs();
                return;
            }

            _selectedEventId = ev.Id;
            _detailContent.text = BuildEventDetail(ev);
            _detailPanel.style.display = DisplayStyle.Flex;
            RefreshTracerLogs();
        }

        private string BuildEventDetail(TraceEvent ev)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"<b>Event #{ev.Id}</b>");
            sb.AppendLine($"Type: {ev.Type}");
            sb.AppendLine($"Name: {ev.TypeName}");
            sb.AppendLine($"Status: {ev.Status}");
            sb.AppendLine($"Mode: {ev.Mode}");
            sb.AppendLine($"Time: {ev.Timestamp:F3}s");
            sb.AppendLine($"Parent ID: {(ev.ParentId == -1 ? "None (root)" : ev.ParentId.ToString())}");

            if (ev.ParentId != -1 && _parentCache.TryGetValue(ev.Id, out var parent))
            {
                sb.AppendLine($"\n<b>Parent Event:</b> #{parent.Id} [{parent.Type}] {parent.TypeName}");
            }

            if (_childrenCache.TryGetValue(ev.Id, out var children) && children.Count > 0)
            {
                sb.AppendLine($"\n<b>Children ({children.Count}):</b>");
                foreach (var child in children)
                {
                    sb.AppendLine($"  #{child.Id} [{child.Type}] {child.TypeName} — {child.Status}");
                }
            }
            return sb.ToString();
        }

        private TraceEventElement CreateTraceElement(TraceEvent ev, int depth)
        {
            var element = new TraceEventElement(ev, depth, _isPaused && _selectedEventId == ev.Id, OnTraceEventClicked);
            return element;
        }

        private class TraceEventElement : VisualElement
        {
            public TraceEventElement(TraceEvent ev, int depth, bool isSelected, Action<TraceEvent> onClick)
            {
                style.flexDirection = FlexDirection.Row;
                style.alignItems = Align.Center;
                style.paddingTop = 4;
                style.paddingBottom = 4;
                style.marginTop = 2;
                style.marginBottom = 2;
                style.borderTopLeftRadius = 4;
                style.borderTopRightRadius = 4;
                style.borderBottomLeftRadius = 4;
                style.borderBottomRightRadius = 4;
                style.paddingLeft = 6 + (depth * 15);

                RegisterCallback<MouseDownEvent>(evt => onClick(ev));

                Color dotColor;

                switch (ev.Status)
                {
                    case TraceStatus.Failed:
                        style.backgroundColor = new StyleColor(new Color(0.25f, 0.1f, 0.1f, 0.4f));
                        dotColor = NexusEditorStyles.AccentRed;
                        break;
                    case TraceStatus.Cancelled:
                        style.backgroundColor = new StyleColor(new Color(0.25f, 0.2f, 0.1f, 0.4f));
                        dotColor = NexusEditorStyles.AccentYellow;
                        break;
                    default:
                        style.backgroundColor = new StyleColor(new Color(0.12f, 0.18f, 0.12f, 0.4f));
                        dotColor = NexusEditorStyles.AccentGreen;
                        break;
                }

                if (isSelected)
                {
                    style.backgroundColor = new StyleColor(new Color(0.2f, 0.3f, 0.5f, 0.6f));
                }

                var statusDot = NexusEditorStyles.CreateStatusDot(dotColor);
                Add(statusDot);

                if (depth > 0)
                {
                    var branchLabel = new Label("└─ ") { style = { color = new StyleColor(NexusEditorStyles.DimText), marginRight = 2 } };
                    Add(branchLabel);
                }

                VisualElement typeTag;
                switch (ev.Type)
                {
                    case TraceEventType.Signal:
                        typeTag = NexusEditorStyles.CreateTag(ev.Type.ToString().ToUpper(), NexusEditorStyles.BtnBlue, NexusEditorStyles.AccentBlueText);
                        break;
                    case TraceEventType.Command:
                        typeTag = NexusEditorStyles.CreateTag(ev.Type.ToString().ToUpper(), NexusEditorStyles.BtnPurple, NexusEditorStyles.AccentPurpleText);
                        break;
                    default:
                        typeTag = NexusEditorStyles.CreateTag(ev.Type.ToString().ToUpper(), new Color(0.3f, 0.3f, 0.35f), new Color(0.8f, 0.8f, 0.8f));
                        break;
                }
                Add(typeTag);

                var nameLabel = new Label(ev.TypeName) { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = Color.white } };
                Add(nameLabel);

                if (ev.Type == TraceEventType.Command)
                {
                    var modeLabel = new Label($"[{ev.Mode}]") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.TextSecondary), marginLeft = 8 } };
                    Add(modeLabel);
                }

                var timeLabel = new Label($"{ev.Timestamp:F3}s") { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText), marginLeft = StyleKeyword.Auto } };
                Add(timeLabel);
            }
        }
    }
}
