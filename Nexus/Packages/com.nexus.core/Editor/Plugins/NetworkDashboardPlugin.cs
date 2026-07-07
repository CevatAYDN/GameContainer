using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class NetworkDashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "NetworkDashboard";
        public override string DisplayName => NexusLang.Get("tab_networkdashboard");
        public override int Order => 10;

        private VisualElement _view;
        private ScrollView _scrollView;
        private IVisualElementScheduledItem _refreshSchedule;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("tab_network_dashboard").ToUpper());
            _view.Add(toolbar);

            var header = new Label("Network Monitoring System")
            {
                style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, color = Color.white, paddingTop = 10, paddingLeft = 10 }
            };
            _view.Add(header);

            var status = NetworkMonitor.CurrentStatus;
            var statusInfo = new Label($"Status: {(status.IsConnected ? "Connected" : "Disconnected")}, Latency: {status.LatencyMs:F1}ms")
            {
                style = { fontSize = 12, color = NexusEditorStyles.TextSecondary, paddingLeft = 10, paddingBottom = 10 }
            };
            _view.Add(statusInfo);

            _scrollView = new ScrollView { style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 10 } };
            _view.Add(_scrollView);

            var clearBtn = new Button(() => { NetworkMonitor.ClearHistory(); RefreshUI(); })
            {
                text = "Clear History",
                style = { marginLeft = 10, marginTop = 10, marginBottom = 10 }
            };
            _view.Add(clearBtn);

            // Subscribe to network events
            NetworkMonitor.OnNetworkEvent += OnNetworkEvent;
            NetworkMonitor.OnConnectionStatusChanged += OnConnectionStatusChanged;

            // Auto-refresh every 500ms
            _refreshSchedule = _view.schedule.Execute(RefreshUI).Every(500);

            RefreshUI();
            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            NetworkMonitor.OnNetworkEvent -= OnNetworkEvent;
            NetworkMonitor.OnConnectionStatusChanged -= OnConnectionStatusChanged;
            base.OnDisable();
        }

        private void RefreshUI()
        {
            _scrollView.Clear();

            var events = NetworkMonitor.GetRecentEvents(20);

            foreach (var evt in events)
            {
                var eventRow = new Label($"[{evt.EventType}] {evt.SignalName} - {evt.Timestamp:HH:mm:ss}")
                {
                    style = { fontSize = 10, color = Color.white, marginBottom = 4 }
                };
                _scrollView.Add(eventRow);
            }

            if (events.Length == 0)
            {
                _scrollView.Add(new Label("No network events recorded")
                {
                    style = { color = NexusEditorStyles.TextSecondary, marginTop = 20 }
                });
            }
        }

        private void OnNetworkEvent(NetworkMonitor.NetworkEvent evt)
        {
            RefreshUI();
        }

        private void OnConnectionStatusChanged(NetworkMonitor.ConnectionStatus status)
        {
            RefreshUI();
        }
    }
}
