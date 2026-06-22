using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public partial class NexusWindow
    {
        // ==========================================
        // ── TAB 5: LIVE TRACER
        // ==========================================
        private void BuildTracerTab()
        {
            var toolbar = NexusEditorStyles.CreateToolbar("LIVE SIGNAL & COMMAND TRACER");
            _contentArea.Add(toolbar);

            // Filter bar
            var filterBar = new VisualElement();
            filterBar.style.flexDirection = FlexDirection.Row;
            filterBar.style.paddingLeft = 10;
            filterBar.style.paddingRight = 10;
            filterBar.style.paddingTop = 6;
            filterBar.style.paddingBottom = 6;
            filterBar.style.borderBottomWidth = 1;
            filterBar.style.borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor);
            filterBar.style.alignItems = Align.Center;
            filterBar.style.flexWrap = Wrap.Wrap;

            _tracerSearchField = new TextField { value = _tracerSearchFilter };
            _tracerSearchField.style.width = 120;
            _tracerSearchField.style.height = 20;
            _tracerSearchField.RegisterValueChangedCallback(evt =>
            {
                _tracerSearchFilter = evt.newValue;
                RefreshTracerLogs();
            });
            filterBar.Add(_tracerSearchField);

            filterBar.Add(new Label("  ") { style = { width = 5 } });

            var clearBtn = new Button(ClearTraces) { text = "Clear" };
            clearBtn.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            clearBtn.style.color = Color.white;
            filterBar.Add(clearBtn);

            _tracerPauseToggle = new Toggle("Pause") { value = _tracerIsPaused };
            _tracerPauseToggle.style.marginLeft = 10;
            _tracerPauseToggle.style.color = Color.white;
            _tracerPauseToggle.RegisterValueChangedCallback(evt =>
            {
                _tracerIsPaused = evt.newValue;
                if (_tracerIsPaused)
                {
                    _tracerPausedEvents = NexusTrace.GetRecentEvents(out _tracerPausedCount);
                    BuildChildrenCache(_tracerPausedEvents, _tracerPausedCount);
                }
                else
                {
                    _tracerPausedEvents = null;
                    _tracerPausedCount = 0;
                    _tracerSelectedEventId = -1;
                    if (_tracerDetailPanel != null) _tracerDetailPanel.style.display = DisplayStyle.None;
                }
                RefreshTracerLogs();
            });
            filterBar.Add(_tracerPauseToggle);

            filterBar.Add(new Label(" Type:") { style = { fontSize = 10, color = Color.gray, marginLeft = 10 } });
            filterBar.Add(MakeFilterButton("SIG", () => { _tracerFilterSignal = !_tracerFilterSignal; RefreshTracerLogs(); }, () => _tracerFilterSignal));
            filterBar.Add(MakeFilterButton("CMD", () => { _tracerFilterCommand = !_tracerFilterCommand; RefreshTracerLogs(); }, () => _tracerFilterCommand));
            filterBar.Add(MakeFilterButton("MOD", () => { _tracerFilterModelChange = !_tracerFilterModelChange; RefreshTracerLogs(); }, () => _tracerFilterModelChange));

            filterBar.Add(new Label(" Status:") { style = { fontSize = 10, color = Color.gray, marginLeft = 10 } });
            filterBar.Add(MakeFilterButton("OK", () => { _tracerFilterOk = !_tracerFilterOk; RefreshTracerLogs(); }, () => _tracerFilterOk, new Color(0.3f, 0.8f, 0.3f)));
            filterBar.Add(MakeFilterButton("FAIL", () => { _tracerFilterFailed = !_tracerFilterFailed; RefreshTracerLogs(); }, () => _tracerFilterFailed, new Color(1f, 0.3f, 0.3f)));
            filterBar.Add(MakeFilterButton("CANCEL", () => { _tracerFilterCancelled = !_tracerFilterCancelled; RefreshTracerLogs(); }, () => _tracerFilterCancelled, new Color(1f, 0.7f, 0.2f)));

            // Split Pane layout for logs & details
            var splitPane = new VisualElement();
            splitPane.style.flexDirection = FlexDirection.Row;
            splitPane.style.flexGrow = 1;

            _tracerScrollView = new ScrollView();
            _tracerScrollView.style.width = new Length(60, LengthUnit.Percent);
            _tracerScrollView.style.paddingLeft = 10;
            _tracerScrollView.style.paddingRight = 10;
            _tracerScrollView.style.paddingTop = 10;
            _tracerScrollView.style.borderRightWidth = 1;
            _tracerScrollView.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            splitPane.Add(_tracerScrollView);

            _tracerDetailPanel = new VisualElement();
            _tracerDetailPanel.style.width = new Length(40, LengthUnit.Percent);
            _tracerDetailPanel.style.backgroundColor = new StyleColor(new Color(0.08f, 0.08f, 0.1f));
            _tracerDetailPanel.style.paddingLeft = 12;
            _tracerDetailPanel.style.paddingRight = 12;
            _tracerDetailPanel.style.paddingTop = 12;
            _tracerDetailPanel.style.display = DisplayStyle.None;

            _tracerDetailContent = new Label();
            _tracerDetailContent.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
            _tracerDetailContent.style.fontSize = 10;
            _tracerDetailContent.style.whiteSpace = WhiteSpace.Normal;
            _tracerDetailPanel.Add(_tracerDetailContent);
            splitPane.Add(_tracerDetailPanel);

            _contentArea.Add(splitPane);

            RefreshTracerLogs();
        }

        private void RefreshTracerLogs()
        {
            if (_tracerScrollView == null) return;
            _tracerScrollView.Clear();
            _tracerRenderedItems.Clear();

            if (!Application.isPlaying)
            {
                _tracerScrollView.Add(new Label("Tracer is offline. Enter Play Mode to trace signals.") { style = { color = Color.gray, alignSelf = Align.Center, marginTop = 20 } });
                return;
            }

            if (_tracerIsPaused)
            {
                RenderFilteredEvents(_tracerPausedEvents, _tracerPausedCount);
            }
            else
            {
                var events = NexusTrace.GetRecentEvents(out int count);
                RenderLiveEvents(events, count);
            }
        }

        private Button MakeFilterButton(string label, Action onClick, Func<bool> isActive, Color? activeColor = null)
        {
            var activeBg = activeColor ?? new Color(0.2f, 0.35f, 0.5f);
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
                btn.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.17f));
                btn.style.color = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
            }
        }

        private void ClearTraces()
        {
            NexusTrace.Reset();
            if (_tracerScrollView != null) _tracerScrollView.Clear();
            _tracerRenderedItems.Clear();
            _tracerPausedEvents = null;
            _tracerPausedCount = 0;
            _tracerSelectedEventId = -1;
            if (_tracerDetailPanel != null) _tracerDetailPanel.style.display = DisplayStyle.None;
        }

        private TraceEvent[] GetFilteredEvents(TraceEvent[] source, int count)
        {
            if (count == 0 || source == null)
                return Array.Empty<TraceEvent>();

            bool HasTypeFilter(TraceEventType t)
            {
                switch (t)
                {
                    case TraceEventType.Signal: return _tracerFilterSignal;
                    case TraceEventType.Command: return _tracerFilterCommand;
                    case TraceEventType.ModelChange: return _tracerFilterModelChange;
                    default: return true;
                }
            }

            bool HasStatusFilter(TraceStatus s)
            {
                switch (s)
                {
                    case TraceStatus.OK: return _tracerFilterOk;
                    case TraceStatus.Failed: return _tracerFilterFailed;
                    case TraceStatus.Cancelled: return _tracerFilterCancelled;
                    default: return true;
                }
            }

            bool HasNameFilter(string name)
            {
                if (string.IsNullOrEmpty(_tracerSearchFilter)) return true;
                return name.IndexOf(_tracerSearchFilter, StringComparison.OrdinalIgnoreCase) >= 0;
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
                if (_tracerRenderedItems.Count > 0)
                {
                    _tracerScrollView.Clear();
                    _tracerRenderedItems.Clear();
                }
                return;
            }

            const int maxDisplayCount = 200;
            int startIndex = 0;
            int displayCount = filteredCount;
            if (filteredCount > maxDisplayCount)
            {
                startIndex = filteredCount - maxDisplayCount;
                displayCount = maxDisplayCount;
            }

            int renderedCount = _tracerRenderedItems.Count;
            if (displayCount != renderedCount)
            {
                _tracerScrollView.Clear();
                _tracerRenderedItems.Clear();
                renderedCount = 0;
            }

            _tracerDepthsCache.Clear();

            for (int i = 0; i < filteredCount; i++)
            {
                var ev = filtered[i];
                int depth = 0;
                if (ev.ParentId != -1 && _tracerDepthsCache.TryGetValue(ev.ParentId, out int parentDepth))
                {
                    depth = parentDepth + 1;
                }
                _tracerDepthsCache[ev.Id] = depth;

                if (i >= startIndex)
                {
                    int renderIdx = i - startIndex;
                    if (renderIdx >= renderedCount)
                    {
                        var item = CreateTraceElement(ev, depth);
                        _tracerScrollView.Add(item);
                        _tracerRenderedItems.Add(item);
                    }
                    else
                    {
                        UpdateTraceElement(_tracerRenderedItems[renderIdx], ev, depth);
                    }
                }
            }
        }

        private void RenderLiveEvents(TraceEvent[] events, int count)
        {
            if (count == 0) return;

            const int maxDisplayCount = 200;
            int startIndex = 0;
            int displayCount = count;
            if (count > maxDisplayCount)
            {
                startIndex = count - maxDisplayCount;
                displayCount = maxDisplayCount;
            }

            int renderedCount = _tracerRenderedItems.Count;
            if (displayCount < renderedCount)
            {
                _tracerScrollView.Clear();
                _tracerRenderedItems.Clear();
                renderedCount = 0;
            }

            _tracerDepthsCache.Clear();

            for (int i = 0; i < count; i++)
            {
                var ev = events[i];
                int depth = 0;
                if (ev.ParentId != -1 && _tracerDepthsCache.TryGetValue(ev.ParentId, out int parentDepth))
                {
                    depth = parentDepth + 1;
                }
                _tracerDepthsCache[ev.Id] = depth;

                if (i >= startIndex)
                {
                    int renderIdx = i - startIndex;
                    if (renderIdx >= renderedCount)
                    {
                        var item = CreateTraceElement(ev, depth);
                        _tracerScrollView.Add(item);
                        _tracerRenderedItems.Add(item);
                    }
                    else
                    {
                        UpdateTraceElement(_tracerRenderedItems[renderIdx], ev, depth);
                    }
                }
            }
        }

        private void BuildChildrenCache(TraceEvent[] events, int count)
        {
            _tracerChildrenCache.Clear();
            for (int i = 0; i < count; i++)
            {
                var ev = events[i];
                if (ev.ParentId != -1)
                {
                    if (!_tracerChildrenCache.ContainsKey(ev.ParentId))
                        _tracerChildrenCache[ev.ParentId] = new List<TraceEvent>();
                    _tracerChildrenCache[ev.ParentId].Add(ev);
                }
            }
        }

        private void OnTraceEventClicked(TraceEvent ev)
        {
            if (!_tracerIsPaused) return;

            if (_tracerSelectedEventId == ev.Id)
            {
                _tracerDetailPanel.style.display = DisplayStyle.None;
                _tracerSelectedEventId = -1;
                return;
            }

            _tracerSelectedEventId = ev.Id;
            var detail = BuildEventDetail(ev);
            _tracerDetailContent.text = detail;
            _tracerDetailPanel.style.display = DisplayStyle.Flex;
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

            if (ev.ParentId != -1 && _tracerPausedEvents != null)
            {
                for (int i = 0; i < _tracerPausedCount; i++)
                {
                    if (_tracerPausedEvents[i].Id == ev.ParentId)
                    {
                        var parent = _tracerPausedEvents[i];
                        sb.AppendLine($"\n<b>Parent Event:</b> #{parent.Id} [{parent.Type}] {parent.TypeName}");
                        break;
                    }
                }
            }

            if (_tracerChildrenCache.TryGetValue(ev.Id, out var children) && children.Count > 0)
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
            var element = new TraceEventElement(OnTraceEventClicked);
            UpdateTraceElement(element, ev, depth);
            return element;
        }

        private void UpdateTraceElement(VisualElement element, TraceEvent ev, int depth)
        {
            var traceElem = (TraceEventElement)element;
            traceElem.Event = ev;
            traceElem.style.paddingLeft = 6 + (depth * 15);
            traceElem.BranchLabel.style.display = depth > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            ApplyTraceValues(traceElem, ev);
        }

        private void ApplyTraceValues(TraceEventElement element, TraceEvent ev)
        {
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

            if (_tracerIsPaused && _tracerSelectedEventId == ev.Id)
            {
                bgColor = new Color(0.2f, 0.3f, 0.5f, 0.6f);
            }

            element.style.backgroundColor = new StyleColor(bgColor);
            element.StatusDot.style.backgroundColor = new StyleColor(dotColor);

            element.TypeTag.text = ev.Type.ToString().ToUpper();
            switch (ev.Type)
            {
                case TraceEventType.Signal:
                    element.TypeTag.style.backgroundColor = new StyleColor(new Color(0.1f, 0.35f, 0.5f));
                    element.TypeTag.style.color = new StyleColor(new Color(0.7f, 0.9f, 1f));
                    break;
                case TraceEventType.Command:
                    element.TypeTag.style.backgroundColor = new StyleColor(new Color(0.4f, 0.2f, 0.5f));
                    element.TypeTag.style.color = new StyleColor(new Color(0.9f, 0.7f, 1f));
                    break;
                default:
                    element.TypeTag.style.backgroundColor = new StyleColor(new Color(0.3f, 0.3f, 0.35f));
                    element.TypeTag.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.8f));
                    break;
            }

            element.NameLabel.text = ev.TypeName;

            if (ev.Type == TraceEventType.Command)
            {
                element.ModeLabel.text = $"[{ev.Mode}]";
                element.ModeLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                element.ModeLabel.style.display = DisplayStyle.None;
            }

            element.TimeLabel.text = $"{ev.Timestamp:F3}s";
        }

        // --- Custom Visual Element for optimized rendering ---
        private class TraceEventElement : VisualElement
        {
            public TraceEvent Event { get; set; }
            public VisualElement StatusDot { get; }
            public Label BranchLabel { get; }
            public Label TypeTag { get; }
            public Label NameLabel { get; }
            public Label ModeLabel { get; }
            public Label TimeLabel { get; }

            public TraceEventElement(System.Action<TraceEvent> onClick)
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
                RegisterCallback<MouseDownEvent>(evt => onClick(Event));

                StatusDot = new VisualElement { name = "StatusDot" };
                StatusDot.style.width = 6;
                StatusDot.style.height = 6;
                StatusDot.style.borderTopLeftRadius = 3;
                StatusDot.style.borderTopRightRadius = 3;
                StatusDot.style.borderBottomLeftRadius = 3;
                StatusDot.style.borderBottomRightRadius = 3;
                StatusDot.style.marginRight = 6;
                Add(StatusDot);

                BranchLabel = new Label("└─ ");
                BranchLabel.style.color = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
                BranchLabel.style.marginRight = 2;
                Add(BranchLabel);

                TypeTag = new Label { name = "TypeTag" };
                TypeTag.style.unityFontStyleAndWeight = FontStyle.Bold;
                TypeTag.style.fontSize = 8;
                TypeTag.style.paddingLeft = 4;
                TypeTag.style.paddingRight = 4;
                TypeTag.style.paddingTop = 1;
                TypeTag.style.paddingBottom = 1;
                TypeTag.style.borderTopLeftRadius = 2;
                TypeTag.style.borderTopRightRadius = 2;
                TypeTag.style.borderBottomLeftRadius = 2;
                TypeTag.style.borderBottomRightRadius = 2;
                TypeTag.style.marginRight = 6;
                Add(TypeTag);

                NameLabel = new Label { name = "NameLabel" };
                NameLabel.style.fontSize = 10;
                NameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                NameLabel.style.color = Color.white;
                Add(NameLabel);

                ModeLabel = new Label { name = "ModeLabel" };
                ModeLabel.style.fontSize = 8;
                ModeLabel.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
                ModeLabel.style.marginLeft = 8;
                Add(ModeLabel);

                TimeLabel = new Label { name = "TimeLabel" };
                TimeLabel.style.fontSize = 8;
                TimeLabel.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                TimeLabel.style.marginLeft = StyleKeyword.Auto;
                Add(TimeLabel);
            }
        }
    }
}
