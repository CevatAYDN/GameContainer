using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Performance Dashboard — real-time FPS, memory, GC and Nexus-specific signal/command metrics
    /// with sparkline charts and configurable alarm thresholds.
    /// </summary>
    public class PerformanceDashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "PerformanceDashboard";
        public override string DisplayName => NexusLang.Get("tab_performancedashboard");
        public override int Order => 10;

        // ── Metric ring buffers ───────────────────────────────────
        private const int BufferSize = 120; // 60 s @ 0.5 s interval
        private readonly RingBuffer<float> _fpsBuffer    = new(BufferSize);
        private readonly RingBuffer<float> _memBuffer    = new(BufferSize);
        private readonly RingBuffer<float> _gcGen0Buffer = new(BufferSize);
        private int _lastGen0;

        // ── Nexus signal/command counters ─────────────────────────
        private int _signalsSinceLastSample;
        private int _commandsSinceLastSample;
        private readonly RingBuffer<float> _signalRateBuffer  = new(BufferSize);
        private readonly RingBuffer<float> _commandRateBuffer = new(BufferSize);

        // ── Alarm thresholds (configurable) ──────────────────────
        private float _fpsAlarm = 30f;
        private float _memAlarmMb = 512f;
        private bool _alarmsEnabled = true;

        // ── UI references ─────────────────────────────────────────
        private VisualElement _view;
        private VisualElement _fpsSparkline;
        private VisualElement _memSparkline;
        private VisualElement _gcSparkline;
        private VisualElement _sigSparkline;
        private VisualElement _cmdSparkline;
        private Label _fpsLabel, _memLabel, _gcLabel, _sigLabel, _cmdLabel;
        private Label _fpsAlarmLabel, _memAlarmLabel;
        private VisualElement _statsContainer;
        private IVisualElementScheduledItem _refreshSchedule;
        private bool _recording;
        private double _sampleInterval = 0.5;
        private double _lastSampleTime;

        // ── Subscription tracking ─────────────────────────────────
        private bool _subscribed;

        // ─────────────────────────────────────────────────────────
        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("pd_toolbar"));
            _view.Add(toolbar);

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.style.paddingLeft = 16;
            scroll.style.paddingRight = 16;
            scroll.style.paddingTop = 16;

            BuildControlBar(scroll);
            BuildFrameSection(scroll);
            BuildMemorySection(scroll);
            BuildNexusSection(scroll);
            BuildAlarmSection(scroll);
            BuildStatsSummary(scroll);

            _view.Add(scroll);

            // Start recording by default when Play Mode is active
            SubscribeToNexusEvents();
            if (Application.isPlaying) StartRecording();

            _refreshSchedule = _view.schedule.Execute(OnSampleTick).Every(500);

            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            UnsubscribeFromNexusEvents();
            base.OnDisable();
        }

        public override void OnUpdate()
        {
            // Called every ~200 ms by NexusWindow scheduler
            if (_recording && Application.isPlaying)
            {
                RefreshAlarmLabels();
            }
        }

        public override IReadOnlyList<(string Label, Action Action, Color Color)> GetContextActions()
            => new List<(string, Action, Color)>
            {
                (NexusLang.Get("pd_start_recording"), StartRecording, NexusEditorStyles.BtnGreen),
                (NexusLang.Get("pd_stop"),           StopRecording,  NexusEditorStyles.BtnRed),
                (NexusLang.Get("pd_clear"),          ClearAll,        NexusEditorStyles.BtnGray),
                (NexusLang.Get("pd_export_csv"),     ExportCsv,       NexusEditorStyles.BtnBlue),
            };

        // ── Build helpers ─────────────────────────────────────────

        private void BuildControlBar(VisualElement parent)
        {
            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginBottom = 12,
                    flexWrap = Wrap.Wrap
                }
            };

            var liveBadge = NexusEditorStyles.CreateLiveBadge();
            liveBadge.name = "live_badge";
            liveBadge.style.marginRight = 8;
            bar.Add(liveBadge);

            var intervalLabel = new Label(NexusLang.Get("pd_sample")) { style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), marginRight = 8 } };
            bar.Add(intervalLabel);

            var alarmsToggle = new Toggle(NexusLang.Get("pd_alarms")) { value = _alarmsEnabled };
            alarmsToggle.style.fontSize = 9;
            alarmsToggle.RegisterValueChangedCallback(evt => _alarmsEnabled = evt.newValue);
            bar.Add(alarmsToggle);

            parent.Add(bar);
        }

        private void BuildFrameSection(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("pd_sec_frame"));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };

            var fpsGroup = BuildMetricGroup(NexusLang.Get("pd_fps"), out _fpsLabel, out _fpsSparkline, 120f, 120f, NexusEditorStyles.AccentGreen);
            fpsGroup.style.marginRight = 16;
            row.Add(fpsGroup);

            card.Add(row);

            // Alarm indicator
            _fpsAlarmLabel = new Label("") { style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.AccentRed), marginTop = 4, display = DisplayStyle.None } };
            card.Add(_fpsAlarmLabel);
        }

        private void BuildMemorySection(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("pd_sec_memory"));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };

            var memGroup = BuildMetricGroup(NexusLang.Get("pd_mono_heap"), out _memLabel, out _memSparkline, 120f, 512f, NexusEditorStyles.AccentBlue);
            memGroup.style.marginRight = 16;
            row.Add(memGroup);

            var gcGroup = BuildMetricGroup(NexusLang.Get("pd_gc_gen0"), out _gcLabel, out _gcSparkline, 120f, 100f, NexusEditorStyles.AccentYellow);
            row.Add(gcGroup);

            card.Add(row);

            _memAlarmLabel = new Label("") { style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.AccentRed), marginTop = 4, display = DisplayStyle.None } };
            card.Add(_memAlarmLabel);
        }

        private void BuildNexusSection(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("pd_sec_throughput"));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };

            var sigGroup = BuildMetricGroup(NexusLang.Get("pd_signals_per_s"), out _sigLabel, out _sigSparkline, 120f, 500f, NexusEditorStyles.AccentPurple);
            sigGroup.style.marginRight = 16;
            row.Add(sigGroup);

            var cmdGroup = BuildMetricGroup(NexusLang.Get("pd_commands_per_s"), out _cmdLabel, out _cmdSparkline, 120f, 200f, NexusEditorStyles.AccentOrange);
            row.Add(cmdGroup);

            card.Add(row);

            var note = new Label(NexusLang.Get("pd_metrics_note"))
            {
                style = { fontSize = 8, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 6 }
            };
            card.Add(note);
        }

        private void BuildAlarmSection(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("pd_sec_alarms"));

            card.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_fps_alarm"), $"{_fpsAlarm:F0}", NexusEditorStyles.AccentOrange));
            card.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_mem_alarm"), $"{_memAlarmMb:F0}", NexusEditorStyles.AccentOrange));
        }

        private void BuildStatsSummary(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("pd_sec_summary"));
            _statsContainer = card;
        }

        // ── Metric group builder ──────────────────────────────────

        private VisualElement BuildMetricGroup(string title, out Label valueLabel,
            out VisualElement sparkline, float sparkWidth, float maxVal, Color color)
        {
            var group = new VisualElement
            {
                style =
                {
                    marginBottom = 12,
                    minWidth = sparkWidth + 12
                }
            };

            var hdr = new Label(title)
            {
                style =
                {
                    fontSize = 9,
                    color = new StyleColor(NexusEditorStyles.TextSecondary),
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginBottom = 2
                }
            };
            group.Add(hdr);

            valueLabel = new Label(NexusLang.Get("pd_value_placeholder"))
            {
                style =
                {
                    fontSize = 16,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(color),
                    marginBottom = 4
                }
            };
            group.Add(valueLabel);

            sparkline = NexusEditorStyles.CreateSparkline(null, maxVal, color, sparkWidth, 32f);
            group.Add(sparkline);

            return group;
        }

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
            var titleLabel = new Label(title)
            {
                style =
                {
                    fontSize = 11,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = new StyleColor(NexusEditorStyles.AccentBlue),
                    marginBottom = 8
                }
            };
            card.Add(titleLabel);
            parent.Add(card);
            return card;
        }

        // ── Sample tick (every 500 ms) ────────────────────────────

        private void OnSampleTick()
        {
            if (!Application.isPlaying || !_recording) return;

            double now = EditorApplication.timeSinceStartup;
            double elapsed = now - _lastSampleTime;
            if (elapsed < _sampleInterval - 0.05) return;
            _lastSampleTime = now;

            // Frame metrics
            float fps = 1f / Mathf.Max(Time.deltaTime, 0.0001f);
            _fpsBuffer.Push(fps);

            // Memory
            float monoMb = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024f * 1024f);
            _memBuffer.Push(monoMb);

            // GC Gen0
            int gen0 = GC.CollectionCount(0);
            _gcGen0Buffer.Push(gen0 - _lastGen0);
            _lastGen0 = gen0;

            // Nexus rates
            float sigRate = _signalsSinceLastSample / (float)elapsed;
            float cmdRate = _commandsSinceLastSample / (float)elapsed;
            _signalRateBuffer.Push(sigRate);
            _commandRateBuffer.Push(cmdRate);
            _signalsSinceLastSample = 0;
            _commandsSinceLastSample = 0;

            RefreshUI(fps, monoMb, sigRate, cmdRate);
        }

        private void RefreshUI(float fps, float monoMb, float sigRate, float cmdRate)
        {
            // Labels
            _fpsLabel.text  = $"{fps:F1}";
            _memLabel.text  = $"{monoMb:F1}{NexusLang.Get("pd_unit_mb")}";
            _gcLabel.text   = string.Format(NexusLang.Get("pd_gc_gen0"), _gcGen0Buffer.Last());
            _sigLabel.text  = $"{sigRate:F1}/s";
            _cmdLabel.text  = $"{cmdRate:F1}/s";

            // Sparklines
            NexusEditorStyles.UpdateSparkline(_fpsSparkline,    _fpsBuffer.ToArray(),    120f, NexusEditorStyles.AccentGreen,  120f, 32f);
            NexusEditorStyles.UpdateSparkline(_memSparkline,    _memBuffer.ToArray(),    _memAlarmMb, NexusEditorStyles.AccentBlue,   120f, 32f);
            NexusEditorStyles.UpdateSparkline(_gcSparkline,     _gcGen0Buffer.ToArray(), 20f,  NexusEditorStyles.AccentYellow, 120f, 32f);
            NexusEditorStyles.UpdateSparkline(_sigSparkline,    _signalRateBuffer.ToArray(), 500f, NexusEditorStyles.AccentPurple, 120f, 32f);
            NexusEditorStyles.UpdateSparkline(_cmdSparkline,    _commandRateBuffer.ToArray(), 200f, NexusEditorStyles.AccentOrange, 120f, 32f);

            // Label colors based on alarms
            _fpsLabel.style.color = new StyleColor(fps < _fpsAlarm && _alarmsEnabled
                ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentGreen);
            _memLabel.style.color = new StyleColor(monoMb > _memAlarmMb && _alarmsEnabled
                ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentBlue);

            RefreshAlarmLabels();
            RefreshStatsSummary(fps, monoMb, sigRate, cmdRate);
        }

        private void RefreshAlarmLabels()
        {
            if (_fpsBuffer.Count == 0) return;

            float lastFps = _fpsBuffer.Last();
            bool fpsAlarm = lastFps < _fpsAlarm && _alarmsEnabled && Application.isPlaying;
            _fpsAlarmLabel.text = fpsAlarm ? string.Format(NexusLang.Get("pd_fps_below"), lastFps.ToString("F1"), _fpsAlarm.ToString("F0")) : "";
            _fpsAlarmLabel.style.display = fpsAlarm ? DisplayStyle.Flex : DisplayStyle.None;

            if (_memBuffer.Count > 0)
            {
                float lastMem = _memBuffer.Last();
                bool memAlarm = lastMem > _memAlarmMb && _alarmsEnabled && Application.isPlaying;
                _memAlarmLabel.text = memAlarm ? string.Format(NexusLang.Get("pd_mem_above"), lastMem.ToString("F1"), _memAlarmMb.ToString("F0")) : "";
                _memAlarmLabel.style.display = memAlarm ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void RefreshStatsSummary(float fps, float monoMb, float sigRate, float cmdRate)
        {
            if (_statsContainer == null) return;
            // Remove old stat rows (keep the title = first child)
            while (_statsContainer.childCount > 1)
                _statsContainer.RemoveAt(_statsContainer.childCount - 1);

            var fpsArr = _fpsBuffer.ToArray();
            if (fpsArr.Length > 0)
            {
                _statsContainer.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_fps_current"), $"{fps:F1}", ColorForFps(fps)));
                _statsContainer.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_fps_avg"), $"{fpsArr.Average():F1}", NexusEditorStyles.TextPrimary));
                _statsContainer.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_fps_min"),  $"{fpsArr.Min():F1}", NexusEditorStyles.AccentOrange));
            }
            _statsContainer.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_mono_heap_short"), $"{monoMb:F2} MB", NexusEditorStyles.AccentBlue));
            _statsContainer.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_signals_current"), $"{sigRate:F1}", NexusEditorStyles.AccentPurple));
            _statsContainer.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_commands_current"), $"{cmdRate:F1}", NexusEditorStyles.AccentOrange));
            _statsContainer.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("pd_gc_delta"), $"{_gcGen0Buffer.Last():F0}", NexusEditorStyles.AccentYellow));
        }

        private Color ColorForFps(float fps) =>
            fps >= _fpsAlarm * 2 ? NexusEditorStyles.AccentGreen
            : fps >= _fpsAlarm   ? NexusEditorStyles.AccentYellow
                                 : NexusEditorStyles.AccentRed;

        // ── Nexus event hooks ─────────────────────────────────────

        private void SubscribeToNexusEvents()
        {
            if (_subscribed) return;
            _subscribed = true;
            // Hook into SignalBus fire events to count signals/commands
            // We use PerformanceMonitor.OnMetricRecorded as a proxy where available
            PerformanceMonitor.OnMetricRecorded += OnNexusMetricRecorded;
        }

        private void UnsubscribeFromNexusEvents()
        {
            if (!_subscribed) return;
            _subscribed = false;
            PerformanceMonitor.OnMetricRecorded -= OnNexusMetricRecorded;
        }

        private void OnNexusMetricRecorded(PerformanceMonitor.MetricSample sample)
        {
            if (sample.Category == "Signal")   _signalsSinceLastSample++;
            if (sample.Category == "Command")  _commandsSinceLastSample++;
        }

        // ── Control actions ───────────────────────────────────────

        private void StartRecording()
        {
            _recording = true;
            _lastGen0  = GC.CollectionCount(0);
            _lastSampleTime = EditorApplication.timeSinceStartup;
            PerformanceMonitor.StartRecording();
            Debug.Log("[Nexus] Performance recording started.");
        }

        private void StopRecording()
        {
            _recording = false;
            PerformanceMonitor.StopRecording();
        }

        private void ClearAll()
        {
            _fpsBuffer.Clear(); _memBuffer.Clear();
            _gcGen0Buffer.Clear();
            _signalRateBuffer.Clear(); _commandRateBuffer.Clear();
            _signalsSinceLastSample = _commandsSinceLastSample = 0;
            PerformanceMonitor.ClearHistory();
        }

        private void ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Index,FPS,MonoMB,GCGen0Delta,Signals/s,Commands/s");
            var fps  = _fpsBuffer.ToArray();
            var mem  = _memBuffer.ToArray();
            var gc   = _gcGen0Buffer.ToArray();
            var sig  = _signalRateBuffer.ToArray();
            var cmd  = _commandRateBuffer.ToArray();
            int len  = Mathf.Max(fps.Length, mem.Length);
            for (int i = 0; i < len; i++)
            {
                sb.AppendLine(
                    $"{i}," +
                    $"{(i < fps.Length ? fps[i].ToString("F2") : "")}," +
                    $"{(i < mem.Length ? mem[i].ToString("F2") : "")}," +
                    $"{(i < gc.Length  ? gc[i].ToString("F0") : "")}," +
                    $"{(i < sig.Length ? sig[i].ToString("F2") : "")}," +
                    $"{(i < cmd.Length ? cmd[i].ToString("F2") : "")}");
            }
            string path = System.IO.Path.Combine(
                UnityEngine.Application.dataPath, "..",
                $"nexus_perf_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllText(path, sb.ToString());
            Debug.Log($"[Nexus] Performance CSV exported: {path}");
        }

        // ── Ring buffer ───────────────────────────────────────────

        private class RingBuffer<T>
        {
            private readonly T[] _buf;
            private int _head, _count;

            public RingBuffer(int capacity) => _buf = new T[capacity];
            public int Count => _count;

            public void Push(T value)
            {
                _buf[_head % _buf.Length] = value;
                _head++;
                _count = Mathf.Min(_count + 1, _buf.Length);
            }

            public T Last() => _count > 0 ? _buf[(_head - 1) % _buf.Length] : default;

            public T[] ToArray()
            {
                if (_count == 0) return Array.Empty<T>();
                var result = new T[_count];
                int start = (_head - _count + _buf.Length) % _buf.Length;
                for (int i = 0; i < _count; i++)
                    result[i] = _buf[(start + i) % _buf.Length];
                return result;
            }

            public void Clear() { _head = _count = 0; }
        }
    }
}
