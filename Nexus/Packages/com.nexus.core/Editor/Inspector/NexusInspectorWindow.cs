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
        private double _lastRefreshTime;

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
            root.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.14f)); // dark theme

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
            _pauseToggle.RegisterValueChangedCallback(evt => _isPaused = evt.newValue);
            toolbar.Add(_pauseToggle);

            _statusLabel = new Label("Steady State: 0 GC");
            _statusLabel.style.marginLeft = StyleKeyword.Auto;
            _statusLabel.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            _statusLabel.style.fontSize = 10;
            toolbar.Add(_statusLabel);

            root.Add(toolbar);

            // 2. Scrollable Event Container
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

        private void ClearTraces()
        {
            NexusTrace.Reset();
            _scrollView.Clear();
            _renderedItems.Clear();
        }

        private void RefreshGUI()
        {
            if (_isPaused || !Application.isPlaying) return;

            var events = NexusTrace.GetRecentEvents(out int count);
            if (count == 0)
            {
                if (_scrollView.childCount > 0)
                {
                    _scrollView.Clear();
                    _renderedItems.Clear();
                }
                return;
            }

            // Simple diffing to avoid recreating all elements
            int currentRenderedCount = _renderedItems.Count;
            if (count < currentRenderedCount)
            {
                _scrollView.Clear();
                _renderedItems.Clear();
                currentRenderedCount = 0;
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

                if (i >= currentRenderedCount)
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

        private VisualElement CreateTraceElement(TraceEvent ev, int depth)
        {
            var element = new VisualElement();
            element.style.flexDirection = FlexDirection.Row;
            element.style.alignItems = Align.Center;
            element.style.paddingTop = 4;
            element.style.paddingBottom = 4;
            element.style.paddingLeft = 6 + (depth * 20); // Indentation depth
            element.style.marginTop = 2;
            element.style.marginBottom = 2;
            element.style.borderTopLeftRadius = 4;
            element.style.borderTopRightRadius = 4;
            element.style.borderBottomLeftRadius = 4;
            element.style.borderBottomRightRadius = 4;

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

            // Hierarchical branch indicator line
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
            // Background & Dot based on status
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

            element.style.backgroundColor = new StyleColor(bgColor);
            if (dot != null) dot.style.backgroundColor = new StyleColor(dotColor);

            // Type Tag
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

            // Name
            var nameLabel = element.Q<Label>("NameLabel");
            if (nameLabel != null) nameLabel.text = ev.TypeName;

            // Mode / Priority
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

            // Timestamp
            var timeLabel = element.Q<Label>("TimeLabel");
            if (timeLabel != null) timeLabel.text = $"{ev.Timestamp:F3}s";
        }
    }
}
