using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class ErrorDashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "ErrorDashboard";
        public override string DisplayName => "Error Dashboard";
        public override int Order => 8;

        private VisualElement _view;
        private ScrollView _scrollView;
        private IVisualElementScheduledItem _refreshSchedule;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("ERROR DASHBOARD");
            _view.Add(toolbar);

            var header = new Label("Error Collection System")
            {
                style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, color = Color.white, paddingTop = 10, paddingLeft = 10 }
            };
            _view.Add(header);

            var info = new Label($"Total Errors: {ErrorCollection.TotalErrorCount}")
            {
                style = { fontSize = 12, color = NexusEditorStyles.TextSecondary, paddingLeft = 10, paddingBottom = 10 }
            };
            _view.Add(info);

            _scrollView = new ScrollView { style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 10 } };
            _view.Add(_scrollView);

            var clearBtn = new Button(() => { ErrorCollection.Clear(); RefreshUI(); })
            {
                text = "Clear All Errors",
                style = { marginLeft = 10, marginTop = 10, marginBottom = 10 }
            };
            _view.Add(clearBtn);

            // Subscribe to error events
            ErrorCollection.OnErrorAdded += OnErrorAdded;

            // Auto-refresh every 500ms
            _refreshSchedule = _view.schedule.Execute(RefreshUI).Every(500);

            RefreshUI();
            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            ErrorCollection.OnErrorAdded -= OnErrorAdded;
            base.OnDisable();
        }

        private void RefreshUI()
        {
            _scrollView.Clear();

            var errors = ErrorCollection.GetRecentErrors(20);

            foreach (var error in errors)
            {
                var errorRow = new Label($"[{error.Severity}] {error.Message} - {error.Timestamp:HH:mm:ss}")
                {
                    style = { fontSize = 10, color = Color.white, marginBottom = 4 }
                };
                _scrollView.Add(errorRow);
            }

            if (errors.Length == 0)
            {
                _scrollView.Add(new Label("No errors recorded")
                {
                    style = { color = NexusEditorStyles.TextSecondary, marginTop = 20 }
                });
            }
        }

        private void OnErrorAdded(ErrorCollection.ErrorEntry error)
        {
            RefreshUI();
        }
    }
}
