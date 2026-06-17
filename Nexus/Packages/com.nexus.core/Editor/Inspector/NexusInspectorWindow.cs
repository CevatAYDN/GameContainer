using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Editor
{
    public class NexusInspectorWindow : EditorWindow
    {
        private ScrollView _scrollView;
        private Toggle _pauseToggle;
        private Label _statusLabel;
        private readonly List<VisualElement> _renderedItems = new();
        private bool _isPaused = false;

        // Time Travel Debugging state (Plan §9.7)
        private TraceEvent[] _pausedEvents;
        private int _pausedCount;
        private string _searchFilter = "";
        private bool _filterSignal = true;
        private bool _filterCommand = true;
        private bool _filterModelChange = true;
        private bool _filterOk = true;
        private bool _filterFailed = true;
        private bool _filterCancelled = true;
        private VisualElement _detailPanel;
        private Label _detailContent;
        private TextField _searchField;
        private readonly Dictionary<int, List<TraceEvent>> _childrenCache = new();

        // Causal chain detail: selected event and its children
        private int _selectedEventId = -1;

        [MenuItem("Window/Nexus/Inspector")]
        public static void ShowWindow()
        {
            var window = GetWindow<NexusInspectorWindow>("Nexus Inspector");
            window.minSize = new Vector2(450, 400);
            window.Show();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.14f));

            // 1. Header Toolbar
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.paddingLeft = 10;
            toolbar.style.paddingRight = 10;
            toolbar.style.paddingTop = 8;
            toolbar.style.paddingBottom = 8;
            toolbar.style.borderBottomWidth = 1;
            toolbar.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.22f));
            toolbar.style.alignItems = Align.Center;

            var titleLabel = new Label("LIVE SIGNAL TRACER");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 12;
            titleLabel.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            titleLabel.style.marginRight = 20;
            toolbar.Add(titleLabel);

            var clearButton = new Button(ClearTraces) { text = "Clear Buffer" };
            clearButton.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            clearButton.style.borderTopLeftRadius = 4;
            clearButton.style.borderTopRightRadius = 4;
            clearButton.style.borderBottomLeftRadius = 4;
            clearButton.style.borderBottomRightRadius = 4;
            clearButton.style.color = Color.white;
            clearButton.style.paddingLeft = 10;
            clearButton.style.paddingRight = 10;
            toolbar.Add(clearButton);

            _pauseToggle = new Toggle("Pause") { value = _isPaused };
            _pauseToggle.style.marginLeft = 15;
            _pauseToggle.style.color = Color.white;
            _pauseToggle.RegisterValueChangedCallback(evt =>
            {
                _isPaused = evt.newValue;
                if (_isPaused)
                {
                    SnapshotPausedEvents();
                }
                else
                {
                    _pausedEvents = null;
                    _pausedCount = 0;
                    _selectedEventId = -1;
                    _detailPanel.style.display = DisplayStyle.None;
                }
                RefreshAll();
            });
            toolbar.Add(_pauseToggle);

            _statusLabel = new Label("Steady State: 0 GC");
            _statusLabel.style.marginLeft = StyleKeyword.Auto;
            _statusLabel.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            _statusLabel.style.fontSize = 10;
            toolbar.Add(_statusLabel);

            root.Add(toolbar);

            // 2. Search and Filter Bar (Time Travel)
            var filterBar = new VisualElement();
            filterBar.style.flexDirection = FlexDirection.Row;
            filterBar.style.paddingLeft = 10;
            filterBar.style.paddingRight = 10;
            filterBar.style.paddingTop = 6;
            filterBar.style.paddingBottom = 6;
            filterBar.style.borderBottomWidth = 1;
            filterBar.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.22f));
            filterBar.style.alignItems = Align.Center;
            filterBar.style.flexWrap = Wrap.Wrap;

            // Search field
            _searchField = new TextField();
            _searchField.style.flexGrow = 1;
            _searchField.style.minWidth = 120;
            _searchField.style.height = 22;
            _searchField.style.fontSize = 11;
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue;
                RefreshAll();
            });
            var searchPlaceholder = new Label("Search events...");
            searchPlaceholder.style.position = Position.Absolute;
            searchPlaceholder.style.left = 8;
            searchPlaceholder.style.top = 4;
            searchPlaceholder.style.fontSize = 11;
            searchPlaceholder.style.color = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
            searchPlaceholder.style.unityFontStyleAndWeight = FontStyle.Italic;
            searchPlaceholder.pickingMode = PickingMode.Ignore;
            _searchField.Add(searchPlaceholder);
            _searchField.RegisterCallback<FocusInEvent>(evt => searchPlaceholder.style.display = DisplayStyle.None);
            _searchField.RegisterCallback<FocusOutEvent>(evt =>
            {
                if (string.IsNullOrEmpty(_searchField.value))
                    searchPlaceholder.style.display = DisplayStyle.Flex;
            });
            filterBar.Add(_searchField);

            // Spacing
            filterBar.Add(new Label("  ") { style = { width = 8 } });

            // Type filter buttons
            var typeFilterLabel = new Label("Type:") { style = { color = new StyleColor(new Color(0.7f, 0.7f, 0.7f)), fontSize = 10, marginRight = 4 } };
            filterBar.Add(typeFilterLabel);

            var signalFilterBtn = MakeFilterButton("SIG", () => { _filterSignal = !_filterSignal; RefreshAll(); }, () => _filterSignal);
            var cmdFilterBtn = MakeFilterButton("CMD", () => { _filterCommand = !_filterCommand; RefreshAll(); }, () => _filterCommand);
            var modelFilterBtn = MakeFilterButton("MOD", () => { _filterModelChange = !_filterModelChange; RefreshAll(); }, () => _filterModelChange);

            filterBar.Add(signalFilterBtn);
            filterBar.Add(cmdFilterBtn);
            filterBar.Add(modelFilterBtn);

            filterBar.Add(new Label("  |  ") { style = { color = new StyleColor(new Color(0.3f, 0.3f, 0.3f)), fontSize = 10 } });

            // Status filter buttons
            var statusFilterLabel = new Label("Status:") { style = { color = new StyleColor(new Color(0.7f, 0.7f, 0.7f)), fontSize = 10, marginRight = 4 } };
            filterBar.Add(statusFilterLabel);

            var okFilterBtn = MakeFilterButton("OK", () => { _filterOk = !_filterOk; RefreshAll(); }, () => _filterOk, new Color(0.3f, 0.8f, 0.3f));
            var failFilterBtn = MakeFilterButton("FAIL", () => { _filterFailed = !_filterFailed; RefreshAll(); }, () => _filterFailed, new Color(1f, 0.3f, 0.3f));
            var cancelFilterBtn = MakeFilterButton("CANCEL", () => { _filterCancelled = !_filterCancelled; RefreshAll(); }, () => _filterCancelled, new Color(1f, 0.7f, 0.2f));

            filterBar.Add(okFilterBtn);
            filterBar.Add(failFilterBtn);
            filterBar.Add(cancelFilterBtn);

            root.Add(filterBar);

            // 3. Detail Panel (initially hidden)
            _detailPanel = new VisualElement();
            _detailPanel.style.backgroundColor = new StyleColor(new Color(0.08f, 0.08f, 0.1f));
            _detailPanel.style.borderBottomWidth = 1;
            _detailPanel.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.22f));
            _detailPanel.style.paddingLeft = 12;
            _detailPanel.style.paddingRight = 12;
            _detailPanel.style.paddingTop = 8;
            _detailPanel.style.paddingBottom = 8;
            _detailPanel.style.display = DisplayStyle.None;

            _detailContent = new Label();
            _detailContent.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.9f));
            _detailContent.style.fontSize = 11;
            _detailContent.style.whiteSpace = WhiteSpace.Normal;
            _detailPanel.Add(_detailContent);

            root.Add(_detailPanel);

            // 4. Scrollable Event Container
            _scrollView = new ScrollView();
            _scrollView.style.flexGrow = 1;
            _scrollView.style.paddingLeft = 10;
            _scrollView.style.paddingRight = 10;
            _scrollView.style.paddingTop = 10;
            _scrollView.style.paddingBottom = 10;
            root.Add(_scrollView);

            // Schedule refresh
            root.schedule.Execute(RefreshGUI).Every(100);
        }

        private Button MakeFilterButton(string label, System.Action onClick, System.Func<bool> isActive, Color? activeColor = null)
        {
            var activeBg = activeColor ?? new Color(0.3f, 0.5f, 0.7f);
            Button btn = null;
            btn = new Button(() =>
            {
                onClick();
                UpdateFilterButtonStyle(btn, isActive(), activeBg);
            }) { text = label };
            btn.style.fontSize = 9;
            btn.style.paddingLeft = 6;
            btn.style.paddingRight = 6;
            btn.style.paddingTop = 2;
            btn.style.paddingBottom = 2;
            btn.style.marginLeft = 2;
            btn.style.marginRight = 2;
            btn.style.borderTopLeftRadius = 3;
            btn.style.borderTopRightRadius = 3;
            btn.style.borderBottomLeftRadius = 3;
            btn.style.borderBottomRightRadius = 3;
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
                btn.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.17f));
                btn.style.color = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
            }
        }

        private void SnapshotPausedEvents()
        {
            _pausedEvents = NexusTrace.GetRecentEvents(out _pausedCount);
            BuildChildrenCache(_pausedEvents, _pausedCount);
        }

        private void ClearTraces()
        {
            NexusTrace.Reset();
            _scrollView.Clear();
            _renderedItems.Clear();
            _pausedEvents = null;
            _pausedCount = 0;
            _selectedEventId = -1;
            _detailPanel.style.display = DisplayStyle.None;
        }

        private void RefreshGUI()
        {
            if (!Application.isPlaying)
            {
                if (_scrollView.childCount > 0)
                {
                    _scrollView.Clear();
                    _renderedItems.Clear();
                }
                _statusLabel.text = "Not Playing — Open Inspector in Play Mode";
                return;
            }

            if (_isPaused)
            {
                // When paused, we show the snapshot - filters apply
                // Snapshot is taken once when pause is toggled
                RenderFilteredEvents(_pausedEvents, _pausedCount);
                return;
            }

            // Live mode
            var events = NexusTrace.GetRecentEvents(out int count);
            if (count == 0 && _scrollView.childCount > 0)
            {
                _scrollView.Clear();
                _renderedItems.Clear();
            }

            RenderLiveEvents(events, count);
        }

        private void RefreshAll()
        {
            // Force a full re-render from current source
            _scrollView.Clear();
            _renderedItems.Clear();

            if (_isPaused && _pausedEvents != null)
            {
                RenderFilteredEvents(_pausedEvents, _pausedCount);
            }
            else if (!_isPaused)
            {
                var events = NexusTrace.GetRecentEvents(out int count);
                RenderLiveEvents(events, count);
            }
        }

        private TraceEvent[] GetFilteredEvents(TraceEvent[] source, int count)
        {
            if (count == 0 || source == null)
                return System.Array.Empty<TraceEvent>();

            // Build filter map for fast lookup
            bool HasTypeFilter(TraceEventType t)
            {
                switch (t)
                {
                    case TraceEventType.Signal: return _filterSignal;
                    case TraceEventType.Command: return _filterCommand;
                    case TraceEventType.ModelChange: return _filterModelChange;
                    default: return true;
                }
            }

            bool HasStatusFilter(TraceStatus s)
            {
                switch (s)
                {
                    case TraceStatus.OK: return _filterOk;
                    case TraceStatus.Failed: return _filterFailed;
                    case TraceStatus.Cancelled: return _filterCancelled;
                    default: return true;
                }
            }

            bool HasNameFilter(string name)
            {
                if (string.IsNullOrEmpty(_searchFilter))
                    return true;
                return name.IndexOf(_searchFilter, System.StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var result = new List<TraceEvent>(count);
            for (int i = 0; i < count; i++)
            {
                var ev = source[i];
                if (HasTypeFilter(ev.Type) && HasStatusFilter(ev.Status) && HasNameFilter(ev.TypeName))
                {
                    result.Add(ev);
                }
            }

            return result.ToArray();
        }

        private void RenderFilteredEvents(TraceEvent[] source, int count)
        {
            var filtered = GetFilteredEvents(source, count);
            int filteredCount = filtered.Length;

            if (filteredCount == 0)
            {
                if (_renderedItems.Count > 0)
                {
                    _scrollView.Clear();
                    _renderedItems.Clear();
                }
                _statusLabel.text = $"Total: {count} | Filtered: 0 | PAUSED";
                return;
            }

            // Diff-based update
            int renderedCount = _renderedItems.Count;
            if (filteredCount != renderedCount)
            {
                _scrollView.Clear();
                _renderedItems.Clear();
                renderedCount = 0;
            }

            var depths = new Dictionary<int, int>();

            for (int i = 0; i < filteredCount; i++)
            {
                var ev = filtered[i];
                int depth = 0;
                if (ev.ParentId != -1 && depths.TryGetValue(ev.ParentId, out int parentDepth))
                {
                    depth = parentDepth + 1;
                }
                depths[ev.Id] = depth;

                if (i >= renderedCount)
                {
                    var item = CreateTraceElement(ev, depth);
                    _scrollView.Add(item);
                    _renderedItems.Add(item);
                }
                else
                {
                    UpdateTraceElement(_renderedItems[i], ev, depth);
                }
            }

            _statusLabel.text = $"Total: {count} | Filtered: {filteredCount} | PAUSED";
        }

        private void RenderLiveEvents(TraceEvent[] events, int count)
        {
            if (count == 0) return;

            int renderedCount = _renderedItems.Count;
            if (count < renderedCount)
            {
                _scrollView.Clear();
                _renderedItems.Clear();
                renderedCount = 0;
            }

            var depths = new Dictionary<int, int>();

            for (int i = 0; i < count; i++)
            {
                var ev = events[i];
                int depth = 0;
                if (ev.ParentId != -1 && depths.TryGetValue(ev.ParentId, out int parentDepth))
                {
                    depth = parentDepth + 1;
                }
                depths[ev.Id] = depth;

                if (i >= renderedCount)
                {
                    var item = CreateTraceElement(ev, depth);
                    _scrollView.Add(item);
                    _renderedItems.Add(item);
                }
                else
                {
                    UpdateTraceElement(_renderedItems[i], ev, depth);
                }
            }

            _statusLabel.text = $"Total Traced: {count} | Steady State: 0 GC";
        }

        private void BuildChildrenCache(TraceEvent[] events, int count)
        {
            _childrenCache.Clear();
            for (int i = 0; i < count; i++)
            {
                var ev = events[i];
                if (ev.ParentId != -1)
                {
                    if (!_childrenCache.ContainsKey(ev.ParentId))
                        _childrenCache[ev.ParentId] = new List<TraceEvent>();
                    _childrenCache[ev.ParentId].Add(ev);
                }
            }
        }

        private void OnTraceEventClicked(TraceEvent ev)
        {
            if (!_isPaused) return;

            if (_selectedEventId == ev.Id)
            {
                _detailPanel.style.display = DisplayStyle.None;
                _selectedEventId = -1;
                return;
            }

            _selectedEventId = ev.Id;
            var detail = BuildEventDetail(ev);
            _detailContent.text = detail;
            _detailPanel.style.display = DisplayStyle.Flex;
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

            // Causal chain: parent info
            if (ev.ParentId != -1 && _pausedEvents != null)
            {
                for (int i = 0; i < _pausedCount; i++)
                {
                    if (_pausedEvents[i].Id == ev.ParentId)
                    {
                        var parent = _pausedEvents[i];
                        sb.AppendLine($"\n<b>Parent Event:</b> #{parent.Id} [{parent.Type}] {parent.TypeName}");
                        break;
                    }
                }
            }

            // Causal chain: children count
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

        private VisualElement CreateTraceElement(TraceEvent ev, int depth)
        {
            var element = new VisualElement();
            element.style.flexDirection = FlexDirection.Row;
            element.style.alignItems = Align.Center;
            element.style.paddingTop = 4;
            element.style.paddingBottom = 4;
            element.style.paddingLeft = 6 + (depth * 20);
            element.style.marginTop = 2;
            element.style.marginBottom = 2;
            element.style.borderTopLeftRadius = 4;
            element.style.borderTopRightRadius = 4;
            element.style.borderBottomLeftRadius = 4;
            element.style.borderBottomRightRadius = 4;
            // Make clickable for detail view
            element.RegisterCallback<MouseDownEvent>(evt => OnTraceEventClicked(ev));

            // Status indicator dot
            var statusDot = new VisualElement();
            statusDot.name = "StatusDot";
            statusDot.style.width = 8;
            statusDot.style.height = 8;
            statusDot.style.borderTopLeftRadius = 4;
            statusDot.style.borderTopRightRadius = 4;
            statusDot.style.borderBottomLeftRadius = 4;
            statusDot.style.borderBottomRightRadius = 4;
            statusDot.style.marginRight = 8;
            element.Add(statusDot);

            // Hierarchical branch indicator
            if (depth > 0)
            {
                var branchLabel = new Label("└─ ");
                branchLabel.style.color = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
                branchLabel.style.marginRight = 2;
                element.Add(branchLabel);
            }

            var typeTag = new Label();
            typeTag.name = "TypeTag";
            typeTag.style.unityFontStyleAndWeight = FontStyle.Bold;
            typeTag.style.fontSize = 9;
            typeTag.style.paddingLeft = 5;
            typeTag.style.paddingRight = 5;
            typeTag.style.paddingTop = 1;
            typeTag.style.paddingBottom = 1;
            typeTag.style.borderTopLeftRadius = 3;
            typeTag.style.borderTopRightRadius = 3;
            typeTag.style.borderBottomLeftRadius = 3;
            typeTag.style.borderBottomRightRadius = 3;
            typeTag.style.marginRight = 8;
            element.Add(typeTag);

            var nameLabel = new Label();
            nameLabel.name = "NameLabel";
            nameLabel.style.fontSize = 11;
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.color = Color.white;
            element.Add(nameLabel);

            var modeLabel = new Label();
            modeLabel.name = "ModeLabel";
            modeLabel.style.fontSize = 9;
            modeLabel.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            modeLabel.style.marginLeft = 10;
            element.Add(modeLabel);

            var timeLabel = new Label();
            timeLabel.name = "TimeLabel";
            timeLabel.style.fontSize = 9;
            timeLabel.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
            timeLabel.style.marginLeft = StyleKeyword.Auto;
            element.Add(timeLabel);

            ApplyTraceValues(element, ev, depth);

            return element;
        }

        private void UpdateTraceElement(VisualElement element, TraceEvent ev, int depth)
        {
            element.style.paddingLeft = 6 + (depth * 20);
            ApplyTraceValues(element, ev, depth);
        }

        private void ApplyTraceValues(VisualElement element, TraceEvent ev, int depth)
        {
            var dot = element.Q<VisualElement>("StatusDot");
            Color bgColor;
            Color dotColor;

            switch (ev.Status)
            {
                case TraceStatus.Failed:
                    bgColor = new Color(0.25f, 0.1f, 0.1f, 0.4f);
                    dotColor = new Color(1f, 0.3f, 0.3f);
                    break;
                case TraceStatus.Cancelled:
                    bgColor = new Color(0.25f, 0.2f, 0.1f, 0.4f);
                    dotColor = new Color(1f, 0.7f, 0.2f);
                    break;
                default:
                    bgColor = new Color(0.12f, 0.18f, 0.12f, 0.4f);
                    dotColor = new Color(0.3f, 1f, 0.3f);
                    break;
            }

            // Highlight selected event
            if (_isPaused && _selectedEventId == ev.Id)
            {
                bgColor = new Color(0.2f, 0.3f, 0.5f, 0.6f);
            }

            element.style.backgroundColor = new StyleColor(bgColor);
            if (dot != null) dot.style.backgroundColor = new StyleColor(dotColor);

            var typeTag = element.Q<Label>("TypeTag");
            if (typeTag != null)
            {
                typeTag.text = ev.Type.ToString().ToUpper();
                switch (ev.Type)
                {
                    case TraceEventType.Signal:
                        typeTag.style.backgroundColor = new StyleColor(new Color(0.1f, 0.35f, 0.5f));
                        typeTag.style.color = new StyleColor(new Color(0.7f, 0.9f, 1f));
                        break;
                    case TraceEventType.Command:
                        typeTag.style.backgroundColor = new StyleColor(new Color(0.4f, 0.2f, 0.5f));
                        typeTag.style.color = new StyleColor(new Color(0.9f, 0.7f, 1f));
                        break;
                    default:
                        typeTag.style.backgroundColor = new StyleColor(new Color(0.3f, 0.3f, 0.35f));
                        typeTag.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.8f));
                        break;
                }
            }

            var nameLabel = element.Q<Label>("NameLabel");
            if (nameLabel != null) nameLabel.text = ev.TypeName;

            var modeLabel = element.Q<Label>("ModeLabel");
            if (modeLabel != null)
            {
                if (ev.Type == TraceEventType.Command)
                {
                    modeLabel.text = $"[{ev.Mode}]";
                    modeLabel.style.display = DisplayStyle.Flex;
                }
                else
                {
                    modeLabel.style.display = DisplayStyle.None;
                }
            }

            var timeLabel = element.Q<Label>("TimeLabel");
            if (timeLabel != null) timeLabel.text = $"{ev.Timestamp:F3}s";
        }
    }
}
