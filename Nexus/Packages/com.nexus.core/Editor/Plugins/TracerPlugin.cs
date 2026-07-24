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
        public override string DisplayName => NexusLang.Get("action_tracer_title");
        public override int Order => 4;

        private VisualElement _view;
        private ListView _listView;
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

        private static int s_nextTraceId = 0;
        private static int NextTraceId() => System.Threading.Interlocked.Increment(ref s_nextTraceId);

        // Thread-safe buffer for incoming trace events
        private readonly ConcurrentQueue<TraceEvent> _incomingEvents = new();
        private readonly List<TraceEvent> _allEvents = new();
        private readonly List<TraceEvent> _filteredSnapshot = new();
        private readonly Dictionary<int, List<TraceEvent>> _childrenCache = new();
        private readonly Dictionary<int, TraceEvent> _parentCache = new();
        private readonly Dictionary<int, int> _depthsCache = new();

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("tracer_title"));
            _view.Add(toolbar);

#if !NEXUS_DEBUG
            var warningCard = NexusEditorStyles.CreateInfoCard(
                _view,
                NexusLang.Get("tracer_debug_disabled_title"),
                NexusEditorStyles.AccentOrange,
                NexusEditorStyles.CardBgYellow,
                NexusLang.Get("tracer_debug_disabled_desc"));

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

            var clearBtn = new Button(ClearTraces) { text = NexusLang.Get("tracer_clear") };
            clearBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.BtnGray);
            clearBtn.style.color = Color.white;
            filterBar.Add(clearBtn);

            _pauseToggle = new Toggle(NexusLang.Get("tr_pause")) { value = _isPaused, style = { marginLeft = 10, color = Color.white } };
            _pauseToggle.RegisterValueChangedCallback(evt =>
            {
                _isPaused = evt.newValue;
                if (!_isPaused)
                {
                    ProcessIncomingQueue();
                }
                RefreshTracerLogs();
            });
            filterBar.Add(_pauseToggle);

            filterBar.Add(new Label(NexusLang.Get("tracer_type_filter")) { style = { fontSize = 10, color = Color.gray, marginLeft = 10 } });
            filterBar.Add(MakeFilterButton(NexusLang.Get("tracer_sig"), () => { _filterSignal = !_filterSignal; RefreshTracerLogs(); }, () => _filterSignal));
            filterBar.Add(MakeFilterButton(NexusLang.Get("tracer_cmd"), () => { _filterCommand = !_filterCommand; RefreshTracerLogs(); }, () => _filterCommand));
            filterBar.Add(MakeFilterButton(NexusLang.Get("tracer_mod"), () => { _filterModelChange = !_filterModelChange; RefreshTracerLogs(); }, () => _filterModelChange));

            filterBar.Add(new Label(NexusLang.Get("tracer_status_filter")) { style = { fontSize = 10, color = Color.gray, marginLeft = 10 } });
            filterBar.Add(MakeFilterButton(NexusLang.Get("tracer_status_ok"), () => { _filterOk = !_filterOk; RefreshTracerLogs(); }, () => _filterOk, NexusEditorStyles.AccentGreen));
            filterBar.Add(MakeFilterButton(NexusLang.Get("tracer_status_fail"), () => { _filterFailed = !_filterFailed; RefreshTracerLogs(); }, () => _filterFailed, NexusEditorStyles.AccentRed));
            filterBar.Add(MakeFilterButton(NexusLang.Get("tracer_status_cancel"), () => { _filterCancelled = !_filterCancelled; RefreshTracerLogs(); }, () => _filterCancelled, NexusEditorStyles.AccentYellow));

            _view.Add(filterBar);

            // Split view for logs and details
            var splitPane = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };

            _listView = new ListView
            {
                fixedItemHeight = 28,
                selectionType = SelectionType.Single,
                style = { width = new Length(60, LengthUnit.Percent), paddingLeft = 6, paddingRight = 6, paddingTop = 6 }
            };
            _listView.style.borderRightWidth = 1;
            _listView.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            _listView.makeItem = () => new TraceEventElement();
            _listView.bindItem = (el, i) =>
            {
                if (i >= 0 && i < _filteredSnapshot.Count)
                {
                    var ev = _filteredSnapshot[i];
                    int depth = _depthsCache.TryGetValue(ev.Id, out int d) ? d : 0;
                    ((TraceEventElement)el).Bind(ev, depth, ev.Id == _selectedEventId, OnTraceEventClicked);
                }
            };
            splitPane.Add(_listView);

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
            if (_allEvents.Count == 0)
            {
                var traces = NexusRuntime.Metrics.GetRecentTraces(out int traceCount);
                for (int i = 0; i < traceCount && _allEvents.Count < 200; i++)
                {
                    if (!string.IsNullOrEmpty(traces[i]))
                    {
                        _allEvents.Add(new TraceEvent(NextTraceId(), -1, TraceEventType.Signal,
                            UnityEngine.Time.realtimeSinceStartupAsDouble, traces[i],
                            TraceStatus.OK, ExecutionMode.Sequential));
                    }
                }
            }

            BuildChildrenCache();
            RefreshTracerLogs();

            // Register trace sink
            NexusTrace.AddSink(this);

            return _view;
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            OnMainThreadUpdate();
        }

        public override void OnDisable()
        {
            NexusTrace.RemoveSink(this);
            _hasLiveEvents = false;
            _productionTraceFrameCounter = 0;
            while (_incomingEvents.TryDequeue(out _)) {}
            _allEvents.Clear();
            _filteredSnapshot.Clear();
            _childrenCache.Clear();
            _parentCache.Clear();
            _depthsCache.Clear();
            base.OnDisable();
        }

        public override System.Collections.Generic.IReadOnlyList<(string Label, System.Action Action, UnityEngine.Color Color)> GetContextActions()
            => new System.Collections.Generic.List<(string, System.Action, UnityEngine.Color)>
            {
                (NexusLang.Get("tr_ctx_clear_buffer"), () => { _allEvents.Clear(); RefreshTracerLogs(); }, NexusEditorStyles.AccentRed),
                (NexusLang.Get("tr_ctx_pause"),         () => _isPaused = !_isPaused,                     NexusEditorStyles.BtnGray),
                (NexusLang.Get("tr_ctx_inspector"),    () => Window?.SwitchToPlugin("ContextInspector"),  NexusEditorStyles.BtnPurple),
            };

        public void Write(in TraceEvent traceEvent)
        {
            _incomingEvents.Enqueue(traceEvent);
        }

        private int _productionTraceFrameCounter;
        private bool _hasLiveEvents;

        private void OnMainThreadUpdate()
        {
            if (!_isPaused)
            {
                bool hasNewEvents = ProcessIncomingQueue();

                if (!_hasLiveEvents)
                {
                    _productionTraceFrameCounter++;
                    if (_productionTraceFrameCounter >= 5)
                    {
                        _productionTraceFrameCounter = 0;
                        if (ReloadProductionTraces()) hasNewEvents = true;
                    }
                }

                if (hasNewEvents)
                {
                    RefreshTracerLogs();
                }
            }
        }

        private bool ReloadProductionTraces()
        {
            var traces = NexusRuntime.Metrics.GetRecentTraces(out int traceCount);

            var rebuilt = new List<TraceEvent>(Math.Min(traceCount, 200));
            for (int i = 0; i < traceCount && rebuilt.Count < 200; i++)
            {
                if (!string.IsNullOrEmpty(traces[i]))
                {
                    rebuilt.Add(new TraceEvent(
                        NextTraceId(), -1, TraceEventType.Signal,
                        UnityEngine.Time.realtimeSinceStartupAsDouble, traces[i],
                        TraceStatus.OK, ExecutionMode.Sequential));
                }
            }

            if (rebuilt.Count == _allEvents.Count) return false;

            _allEvents.Clear();
            _allEvents.AddRange(rebuilt);
            if (_allEvents.Count > 0) BuildChildrenCache();
            return true;
        }

        private bool ProcessIncomingQueue()
        {
            bool addedAny = false;
            while (_incomingEvents.TryDequeue(out var ev))
            {
                _allEvents.Add(ev);
                addedAny = true;
            }

            if (addedAny)
            {
                _hasLiveEvents = true;
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
            if (_listView == null) return;

            if (!Application.isPlaying)
            {
                _detailPanel.style.display = DisplayStyle.None;
                _filteredSnapshot.Clear();
                _listView.itemsSource = _filteredSnapshot;
                _listView.Rebuild();
                return;
            }

            var filtered = GetFilteredEvents();
            int filteredCount = filtered.Count;
            const int maxDisplayCount = 200;
            int startIndex = Math.Max(0, filteredCount - maxDisplayCount);

            _depthsCache.Clear();
            _filteredSnapshot.Clear();

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
                    _filteredSnapshot.Add(ev);
                }
            }

            _listView.itemsSource = _filteredSnapshot;
            _listView.Rebuild();
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
            _childrenCache.Clear();
            _parentCache.Clear();
            _depthsCache.Clear();
            _filteredSnapshot.Clear();
            _selectedEventId = -1;
            
            if (_listView != null)
            {
                _listView.itemsSource = _filteredSnapshot;
                _listView.Rebuild();
            }
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
            sb.AppendLine(string.Format(NexusLang.Get("tr_detail_event_id"), ev.Id));
            sb.AppendLine(string.Format(NexusLang.Get("tr_detail_type"), ev.Type));
            sb.AppendLine(string.Format(NexusLang.Get("tr_detail_name"), ev.TypeName));
            sb.AppendLine(string.Format(NexusLang.Get("tr_detail_status"), ev.Status));
            sb.AppendLine(string.Format(NexusLang.Get("tr_detail_mode"), ev.Mode));
            sb.AppendLine($"{NexusLang.Get("tr_detail_time_label")}{ev.Timestamp:F3}{NexusLang.Get("tracer_time_suffix")}");
            sb.AppendLine($"{NexusLang.Get("tr_detail_parent_id_label")}{(ev.ParentId == -1 ? NexusLang.Get("tr_detail_none_root") : ev.ParentId.ToString())}");

            if (ev.ParentId != -1 && _parentCache.TryGetValue(ev.Id, out var parent))
            {
                sb.AppendLine($"\n<b>Parent Event:</b> #{parent.Id} [{parent.Type}] {parent.TypeName}");
            }

            if (_childrenCache.TryGetValue(ev.Id, out var children) && children.Count > 0)
            {
                sb.AppendLine(string.Format(NexusLang.Get("tr_detail_children"), children.Count));
                foreach (var child in children)
                {
                    sb.AppendLine(string.Format(NexusLang.Get("tr_detail_child_row"), child.Id, child.Type, child.TypeName, child.Status));
                }
            }
            return sb.ToString();
        }

        private class TraceEventElement : VisualElement
        {
            private EventCallback<MouseDownEvent> _currentCallback;

            public TraceEventElement()
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
            }

            public void Bind(TraceEvent ev, int depth, bool isSelected, Action<TraceEvent> onClick)
            {
                Clear();
                if (_currentCallback != null)
                {
                    UnregisterCallback<MouseDownEvent>(_currentCallback);
                }

                _currentCallback = evt => onClick(ev);
                RegisterCallback<MouseDownEvent>(_currentCallback);

                style.paddingLeft = 6 + (depth * 15);

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
                    var branchLabel = new Label(NexusLang.Get("tr_tree_prefix")) { style = { color = new StyleColor(NexusEditorStyles.DimText), marginRight = 2 } };
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
