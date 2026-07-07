using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class PerformanceDashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "PerformanceDashboard";
        public override string DisplayName => "Performance Dashboard";
        public override int Order => 9;

        private VisualElement _view;
        private ScrollView _scrollView;
        private IVisualElementScheduledItem _refreshSchedule;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("PERFORMANCE DASHBOARD");
            _view.Add(toolbar);

            var header = new Label("Performance Monitoring System")
            {
                style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, color = Color.white, paddingTop = 10, paddingLeft = 10 }
            };
            _view.Add(header);

            _scrollView = new ScrollView { style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 10 } };
            _view.Add(_scrollView);

            var recordBtn = new Button(() => { PerformanceMonitor.StartRecording(); })
            {
                text = "Start Recording",
                style = { marginLeft = 10, marginTop = 10, marginBottom = 10 }
            };
            _view.Add(recordBtn);

            var stopBtn = new Button(() => { PerformanceMonitor.StopRecording(); })
            {
                text = "Stop Recording",
                style = { marginLeft = 10, marginTop = 10, marginBottom = 10 }
            };
            _view.Add(stopBtn);

            var clearBtn = new Button(() => { PerformanceMonitor.ClearHistory(); RefreshUI(); })
            {
                text = "Clear History",
                style = { marginLeft = 10, marginTop = 10, marginBottom = 10 }
            };
            _view.Add(clearBtn);

            // Subscribe to performance events
            PerformanceMonitor.OnMetricRecorded += OnMetricRecorded;

            // Auto-refresh every 500ms
            _refreshSchedule = _view.schedule.Execute(RefreshUI).Every(500);

            RefreshUI();
            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            PerformanceMonitor.OnMetricRecorded -= OnMetricRecorded;
            base.OnDisable();
        }

        private void RefreshUI()
        {
            _scrollView.Clear();

            var metrics = PerformanceMonitor.GetAllCurrentMetrics();

            foreach (var kvp in metrics.OrderByDescending(m => m.Value))
            {
                var metricRow = new Label($"{kvp.Key}: {kvp.Value:F2}")
                {
                    style = { fontSize = 10, color = Color.white, marginBottom = 4 }
                };
                _scrollView.Add(metricRow);
            }

            if (metrics.Count == 0)
            {
                _scrollView.Add(new Label("No metrics recorded")
                {
                    style = { color = NexusEditorStyles.TextSecondary, marginTop = 20 }
                });
            }
        }

        private void OnMetricRecorded(PerformanceMonitor.MetricSample sample)
        {
            // Refresh only if recording to avoid constant updates
            if (PerformanceMonitor.IsRecording)
            {
                RefreshUI();
            }
        }
    }
}
