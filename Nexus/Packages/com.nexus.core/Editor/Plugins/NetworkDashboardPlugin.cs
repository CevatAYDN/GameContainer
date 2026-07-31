using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Network Dashboard — live connection status, latency gauge, event log with filtering,
    /// and packet statistics for Nexus NetworkSignalBus.
    /// </summary>
    public class NetworkDashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "NetworkDashboard";
        public override string DisplayName => NexusLang.Get("tab_networkdashboard");
        public override int Order => 11;

        // ── State ─────────────────────────────────────────────────
        private readonly List<NetworkMonitor.NetworkEvent> _filteredEvents = new();
        private string _typeFilter = "All";
        private string _searchFilter = "";
        private bool _autoScroll = true;

        // ── Latency ring buffer ───────────────────────────────────
        private const int LatencyBufSize = 60;
        private readonly float[] _latencyBuf = new float[LatencyBufSize];
        private int _latencyHead, _latencyCount;

        // ── UI refs ───────────────────────────────────────────────
        private VisualElement _view;
        private Label _statusLabel;
        private Label _latencyLabel;
        private VisualElement _latencyGaugeFill;
        private VisualElement _latencySparkline;
        private Label _sentLabel, _rcvdLabel, _errLabel;
        private ScrollView _eventLog;
        private VisualElement _eventTable;
        private double _lastRefreshTime;

        // ── Counters ──────────────────────────────────────────────
        private int _totalSent, _totalRcvd, _totalErr;

        // ─────────────────────────────────────────────────────────
        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("nd_title"));
            _view.Add(toolbar);

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.style.paddingLeft = 16;
            scroll.style.paddingRight = 16;
            scroll.style.paddingTop = 16;

            BuildConnectionCard(scroll);
            BuildLatencyCard(scroll);
            BuildStatsCard(scroll);
            BuildFilterBar(scroll);
            BuildEventLog(scroll);

            _view.Add(scroll);

            SubscribeEvents();
            RefreshStatus();
            ApplyFilters();

            return _view;
        }

        public override void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRefreshTime < 0.5) return;
            _lastRefreshTime = now;
            RefreshStatus();
        }

        public override void OnDisable()
        {
            UnsubscribeEvents();
            base.OnDisable();
        }

        public override IReadOnlyList<(string Label, Action Action, Color Color)> GetContextActions()
            => new List<(string, Action, Color)>
            {
                (NexusLang.Get("nd_action_clear"),  ClearLog,     NexusEditorStyles.BtnGray),
                (NexusLang.Get("nd_action_export"), ExportLog,    NexusEditorStyles.BtnBlue),
            };

        // ── Build helpers ─────────────────────────────────────────

        private void BuildConnectionCard(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("nd_section_connection"));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

            _statusLabel = new Label(NexusLang.Get("nd_disconnected"))
            {
                style =
                {
                    fontSize = 14,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.AccentRed),
                    marginRight = 16
                }
            };
            row.Add(_statusLabel);
            card.Add(row);
        }

        private void BuildLatencyCard(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("nd_section_latency"));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

            _latencyLabel = new Label(NexusLang.Get("nd_latency_default"))
            {
                style =
                {
                    fontSize = 20,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.AccentBlue),
                    marginRight = 16,
                    minWidth = 80
                }
            };
            row.Add(_latencyLabel);

            var gaugeContainer = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Column,
                    marginRight = 16
                }
            };
            var gaugeLabel = new Label(NexusLang.Get("nd_latency_range")) { style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText), marginBottom = 2 } };
            gaugeContainer.Add(gaugeLabel);
            var gaugeBg = new VisualElement
            {
                style =
                {
                    width = 120, height = 8,
                    backgroundColor = new StyleColor(NexusEditorStyles.DarkPanel),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    overflow = Overflow.Hidden
                }
            };
            _latencyGaugeFill = new VisualElement
            {
                style =
                {
                    width = new Length(0, LengthUnit.Percent),
                    height = 8,
                    backgroundColor = new StyleColor(NexusEditorStyles.AccentGreen),
                    borderTopLeftRadius = 4, borderBottomLeftRadius = 4
                }
            };
            gaugeBg.Add(_latencyGaugeFill);
            gaugeContainer.Add(gaugeBg);
            row.Add(gaugeContainer);

            _latencySparkline = NexusVisualization.CreateSparkline(null, 500f, NexusEditorStyles.AccentBlue, 120f, 32f);
            row.Add(_latencySparkline);

            card.Add(row);
        }

        private void BuildStatsCard(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("nd_section_stats"));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };

            _sentLabel = AddStatPill(row, NexusLang.Get("nd_stat_sent"),     "0", NexusEditorStyles.AccentGreen);
            _rcvdLabel = AddStatPill(row, NexusLang.Get("nd_stat_received"), "0", NexusEditorStyles.AccentBlue);
            _errLabel  = AddStatPill(row, NexusLang.Get("nd_stat_errors"),   "0", NexusEditorStyles.AccentRed);

            card.Add(row);
        }

        private Label AddStatPill(VisualElement parent, string label, string value, Color color)
        {
            var box = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.DarkPanel),
                    paddingTop = 8, paddingBottom = 8, paddingLeft = 8, paddingRight = 8,
                    marginRight = 8,
                    marginBottom = 8,
                    borderTopLeftRadius = 6, borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6, borderBottomRightRadius = 6,
                    minWidth = 80
                }
            };
            var valueLabel = new Label(value)
            {
                style =
                {
                    fontSize = 18,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(color)
                }
            };
            var keyLabel = new Label(label)
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary) }
            };
            box.Add(valueLabel);
            box.Add(keyLabel);
            parent.Add(box);
            return valueLabel;
        }

        private void BuildFilterBar(VisualElement parent)
        {
            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginBottom = 8,
                    flexWrap = Wrap.Wrap
                }
            };

            var typeLabel = new Label(NexusLang.Get("nd_filter_type")) { style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), marginRight = 4 } };
            bar.Add(typeLabel);

            foreach (var t in new[] { NexusLang.Get("nd_filter_all"), NexusLang.Get("nd_filter_sent"), NexusLang.Get("nd_filter_received"), NexusLang.Get("nd_filter_failed"), NexusLang.Get("nd_filter_timeout") })
            {
                var t1 = t;
                var btn = new Button(() =>
                {
                    _typeFilter = t1;
                    ApplyFilters();
                }) { text = t };
                StyleFilterBtn(btn, t == _typeFilter);
                bar.Add(btn);
            }

            bar.Add(new Label(NexusLang.Get("nd_filter_search")) { style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), marginLeft = 8, marginRight = 4 } });
            var searchField = new TextField { value = _searchFilter, style = { width = 100, height = 18 } };
            searchField.RegisterValueChangedCallback(evt => { _searchFilter = evt.newValue; ApplyFilters(); });
            bar.Add(searchField);

            var autoScrollToggle = new Toggle(NexusLang.Get("nd_autoscroll")) { value = _autoScroll };
            autoScrollToggle.style.fontSize = 9;
            autoScrollToggle.RegisterValueChangedCallback(evt => _autoScroll = evt.newValue);
            bar.Add(autoScrollToggle);

            parent.Add(bar);
        }

        private void BuildEventLog(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("nd_section_events"));
            _eventLog = new ScrollView { style = { maxHeight = 280 } };
            _eventTable = new VisualElement();
            _eventLog.Add(_eventTable);
            card.Add(_eventLog);
        }

        // ── Refresh ───────────────────────────────────────────────

        private void RefreshStatus()
        {
            var status = NetworkMonitor.CurrentStatus;
            bool connected = status.IsConnected;
            float latencyMs = status.LatencyMs;

            _statusLabel.text = connected ? NexusLang.Get("nd_connected") : NexusLang.Get("nd_disconnected");
            _statusLabel.style.color = new StyleColor(connected
                ? NexusEditorStyles.AccentGreen
                : NexusEditorStyles.AccentRed);

            _latencyLabel.text = connected ? $"{latencyMs:F1} ms" : NexusLang.Get("nd_latency_default");

            // Gauge: 0-500ms range
            float ratio = Mathf.Clamp01(latencyMs / 500f);
            _latencyGaugeFill.style.width = new Length(ratio * 100f, LengthUnit.Percent);
            Color gaugeColor = latencyMs < 50  ? NexusEditorStyles.AccentGreen
                             : latencyMs < 150 ? NexusEditorStyles.AccentYellow
                                               : NexusEditorStyles.AccentRed;
            _latencyGaugeFill.style.backgroundColor = new StyleColor(gaugeColor);

            // Latency sparkline
            PushLatency(latencyMs);
            NexusVisualization.UpdateSparkline(_latencySparkline,
                GetLatencyArray(), 500f, NexusEditorStyles.AccentBlue, 120f, 32f);

            // Stats
            _sentLabel.text = _totalSent.ToString();
            _rcvdLabel.text = _totalRcvd.ToString();
            _errLabel.text  = _totalErr.ToString();
        }

        private void ApplyFilters()
        {
            var all = NetworkMonitor.GetRecentEvents(200);
            _filteredEvents.Clear();
            foreach (var evt in all)
            {
                if (_typeFilter != "All" &&
                    !evt.EventType.ToString().Equals(_typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(_searchFilter) &&
                    evt.SignalName?.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _filteredEvents.Add(evt);
            }
            RebuildEventTable();
        }

        private void RebuildEventTable()
        {
            _eventTable.Clear();

            // Drive the table purely from the filtered set so active filters are honored
            // (an empty result must render as "no events", never silently fall back to all).
            var events = _filteredEvents;

            if (events.Count == 0)
            {
                _eventTable.Add(new Label(NexusLang.Get("nd_no_events"))
                {
                    style = { color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 12, unityTextAlign = TextAnchor.MiddleCenter }
                });
                return;
            }

            var table = NexusVisualization.CreateDataTable(
                new[] {
                    (NexusLang.Get("nd_col_type"), 0.2f),
                    (NexusLang.Get("nd_col_signal"), 0.4f),
                    (NexusLang.Get("nd_col_direction"), 0.2f),
                    (NexusLang.Get("nd_col_time"), 0.2f)
                },
                events.TakeLast(200).Select(e => new[]
                {
                    e.EventType.ToString(),
                    e.SignalName ?? "",
                    e.EventType == "Sent" ? NexusLang.Get("nd_dir_out")
                        : e.EventType == "Received" ? NexusLang.Get("nd_dir_in")
                        : NexusLang.Get("nd_dir_err"),
                    e.Timestamp.ToString("HH:mm:ss.fff")
                })
            );
            _eventTable.Add(table);

            if (_autoScroll)
                _eventLog.ScrollTo(_eventTable.ElementAt(_eventTable.childCount - 1));
        }

        // ── Event subscription ────────────────────────────────────

        private void SubscribeEvents()
        {
            NetworkMonitor.OnNetworkEvent           += OnNetworkEvent;
            NetworkMonitor.OnConnectionStatusChanged += OnConnectionChanged;
        }

        private void UnsubscribeEvents()
        {
            NetworkMonitor.OnNetworkEvent           -= OnNetworkEvent;
            NetworkMonitor.OnConnectionStatusChanged -= OnConnectionChanged;
        }

        private void OnNetworkEvent(NetworkMonitor.NetworkEvent evt)
        {
            if (evt.EventType == "Sent") _totalSent++;
            else if (evt.EventType == "Received") _totalRcvd++;
            if (evt.EventType == "Failed" || evt.EventType == "Timeout") _totalErr++;

            // Only rebuild if filter matches
            bool passes = (_typeFilter == "All" || evt.EventType.ToString().Equals(_typeFilter, StringComparison.OrdinalIgnoreCase)) &&
                          (string.IsNullOrEmpty(_searchFilter) || (evt.SignalName?.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0));
            if (passes)
            {
                _filteredEvents.Add(evt);
                if (_filteredEvents.Count > 200) _filteredEvents.RemoveAt(0);
                RebuildEventTable();
            }
        }

        private void OnConnectionChanged(NetworkMonitor.ConnectionStatus status)
        {
            RefreshStatus();
        }

        // ── Actions ───────────────────────────────────────────────

        private void ClearLog()
        {
            _filteredEvents.Clear();
            _totalSent = _totalRcvd = _totalErr = 0;
            NetworkMonitor.ClearHistory();
            RebuildEventTable();
        }

        private void ExportLog()
        {
            var events = NetworkMonitor.GetRecentEvents(1000);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Timestamp,Type,Signal,Direction");
            foreach (var e in events)
                sb.AppendLine($"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{e.EventType},{e.SignalName},{(e.EventType == "Sent" ? "Out" : "In")}");

            string path = System.IO.Path.Combine(
                UnityEngine.Application.dataPath, "..",
                $"nexus_network_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllText(path, sb.ToString());
            Debug.Log($"[Nexus] Network log exported: {path}");
        }

        // ── Helpers ───────────────────────────────────────────────

        private static VisualElement BuildCard(VisualElement parent, string title)
        {
            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    borderTopLeftRadius = 6, borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6, borderBottomRightRadius = 6,
                    paddingTop = 12, paddingBottom = 12, paddingLeft = 12, paddingRight = 12,
                    marginBottom = 12
                }
            };
            card.Add(new Label(title)
            {
                style =
                {
                    fontSize = 11,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.AccentBlue),
                    marginBottom = 8
                }
            });
            parent.Add(card);
            return card;
        }

        private static void StyleFilterBtn(Button btn, bool active)
        {
            btn.style.fontSize = 9;
            btn.style.paddingLeft = btn.style.paddingRight = 8;
            btn.style.paddingTop = btn.style.paddingBottom = 2;
            btn.style.marginRight = 4;
            btn.style.borderTopLeftRadius = btn.style.borderTopRightRadius =
            btn.style.borderBottomLeftRadius = btn.style.borderBottomRightRadius = 3;
            btn.style.backgroundColor = new StyleColor(active ? NexusEditorStyles.HighlightBg : Color.clear);
            btn.style.color = new StyleColor(active ? NexusEditorStyles.AccentBlue : NexusEditorStyles.TextSecondary);
        }

        private void PushLatency(float value)
        {
            _latencyBuf[_latencyHead % LatencyBufSize] = value;
            _latencyHead++;
            _latencyCount = Mathf.Min(_latencyCount + 1, LatencyBufSize);
        }

        private float[] GetLatencyArray()
        {
            if (_latencyCount == 0) return Array.Empty<float>();
            var result = new float[_latencyCount];
            int start = (_latencyHead - _latencyCount + LatencyBufSize) % LatencyBufSize;
            for (int i = 0; i < _latencyCount; i++)
                result[i] = _latencyBuf[(start + i) % LatencyBufSize];
            return result;
        }
    }
}
