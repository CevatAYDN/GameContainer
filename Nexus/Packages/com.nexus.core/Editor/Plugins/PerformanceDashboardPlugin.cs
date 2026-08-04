using System;
using System.Collections.Generic;
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
        // Deltas of NexusRuntime.Metrics' Interlocked totals, sampled per tick. The previous
        // implementation subscribed to PerformanceMonitor.OnMetricRecorded and counted samples
        // with Category "Signal"/"Command" — but nothing records metrics with those categories
        // (MetricsSampler records Frame/Memory/GC only), so the signal/command rates always read
        // 0.0, and the per-record subscription added GetInvocationList + delegate overhead to
        // every recorded metric. The Interlocked totals are exact and allocation-free.
        private long _lastTotalSignals;
        private long _lastTotalCommands;
        private readonly RingBuffer<float> _signalRateBuffer  = new(BufferSize);
        private readonly RingBuffer<float> _commandRateBuffer = new(BufferSize);

        // ── Alarm thresholds (configurable) ──────────────────────
        private float _fpsAlarm = 30f;
        private float _memAlarmMb = 512f;
        private bool _alarmsEnabled = true;

        // Memory baseline: the ALARM measures GROWTH since recording started, never the
        // absolute mono heap. In the Editor, GetMonoUsedSizeLong() includes the editor's own
        // managed heap (routinely 500MB-1GB), so the old code displayed ~800MB at startup and
        // tripped the 512MB alarm immediately. The absolute value is still shown in the big
        // label and (when no runtime MetricsSampler exists) in the chart.
        private float _memBaselineMb;
        // Last session delta (monoMb - baseline) — the single source for the alarm and the
        // Δ figure, independent of what the chart plots (delta vs. absolute fallback).
        private float _memDeltaMb;

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
        private bool _recording;
        private double _sampleInterval = 0.5;
        private double _lastSampleTime;

        // ─────────────────────────────────────────────────────────
        public override VisualElement CreateView()
        {
            // Must be here: the window calls CreateView on every tab show, but OnEnable only
            // once at window open. Entering Play Mode while this tab is open does NOT re-run
            // CreateView, so without this subscription recording would never start (the panel
            // would show "—" forever). Mirror the DashboardPlugin/ExplorerPlugin pattern.
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

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
            if (Application.isPlaying) StartRecording();

            return _view;
        }

        public override void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            // CONTRIBUTING: OnDisable() must reset all flags, queues, and debounces.
            // The stat-row cache holds rows belonging to the (now-detached) old container;
            // a later CreateView rebuilds _statsContainer, so the cache must be cleared to
            // avoid mutating stale rows and leaving the new summary card empty.
            _recording = false;
            PerformanceMonitor.StopRecording();
            _statRowCache.Clear();
            _statsContainer = null;

            base.OnDisable();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Auto-start recording when entering Play Mode (with a fresh baseline) and stop
            // on exit, so the panel works even when the tab was open before Play started.
            if (state == PlayModeStateChange.ExitingPlayMode)
                StopRecording();
            else if (state == PlayModeStateChange.EnteredPlayMode)
                StartRecording();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (_recording && Application.isPlaying)
            {
                OnSampleTick();
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

            card.Add(NexusVisualization.CreateStatRow(NexusLang.Get("pd_fps_alarm"), $"{_fpsAlarm:F0}", NexusEditorStyles.AccentOrange));
            card.Add(NexusVisualization.CreateStatRow(NexusLang.Get("pd_mem_alarm"), $"{_memAlarmMb:F0}", NexusEditorStyles.AccentOrange));
        }

        private void BuildStatsSummary(VisualElement parent)
        {
            var card = BuildCard(parent, NexusLang.Get("pd_sec_summary"));
            _statsContainer = card;
            // A fresh container is created every CreateView. If the plugin instance is
            // reused across window reopens, cached rows belong to the previous container;
            // drop them so SetStatRow re-creates rows inside this new card.
            _statRowCache.Clear();
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

            sparkline = NexusVisualization.CreateSparkline(null, maxVal, color, sparkWidth, 32f);
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

            // Frame metrics. FPS is read from the GAME-recorded metric (MetricsSampler →
            // PerformanceMonitor.UpdateFrameMetrics, running in the real Update loop), NOT
            // computed from Time.deltaTime here. An editor window callback's
            // Time.deltaTime is the time since the last RENDERED game frame, which
            // balloons whenever the Game view is occluded by this window or the editor is
            // busy repainting — producing bogus 1-4 FPS readings (and false "FPS below
            // threshold" alarms) even when the game runs at 60. The recorded metric is
            // refreshed ~10x/sec by the game loop and read thread-safely via GetMetric.
            float fps = PerformanceMonitor.GetMetric("FPS");
            // Fall back to Time.deltaTime (the game frame time in play mode) when no
            // recorded metric exists — a scene without a MetricsSampler. dt >= 1s is a
            // freeze/hitch, not a 1 FPS signal, so it is skipped rather than plotted.
            if (fps <= 0f)
            {
                float dt = Time.deltaTime;
                if (dt > 0f && dt < 1f) fps = 1f / dt;
            }
            // Only push real samples: a 0.0 reading means "not sampled yet" (no
            // MetricsSampler in the scene and no frame yet, or recording just started) —
            // pushing it would drag the sparkline and the avg/min stat rows to 0.
            if (fps > 0f) _fpsBuffer.Push(fps);

            // Memory — the ALARM uses the session delta, never the absolute value: the
            // absolute includes the editor's own managed heap (huge by default, which made
            // the old code show ~800MB at startup and trip the 512MB alarm instantly). Prefer
            // the runtime-recorded "MonoUsed" metric (sampled by MetricsSampler from the game
            // loop — exact in builds); fall back to the editor-process Profiler read.
            float monoMb = ReadMonoUsedMb(out bool fromRecordedMetric);
            if (monoMb > 0f)
            {
                // First usable sample becomes the baseline (covers recording started before
                // any game metric was sampled).
                if (_memBaselineMb <= 0f) _memBaselineMb = monoMb;
                _memDeltaMb = monoMb - _memBaselineMb;
                // Chart: plot the session delta when a runtime sampler feeds the game heap;
                // plot the ABSOLUTE value when we fell back to the Profiler read (no
                // MetricsSampler in the scene) — a flat ~0 delta chart would read as broken.
                _memBuffer.Push(fromRecordedMetric ? _memDeltaMb : monoMb);
            }

            // GC Gen0
            int gen0 = GC.CollectionCount(0);
            _gcGen0Buffer.Push(gen0 - _lastGen0);
            _lastGen0 = gen0;

            // Nexus rates: delta of the exact Interlocked totals. A negative delta means the
            // totals were reset (context teardown) — treat it as zero rather than a bogus rate.
            long totalSignals = NexusRuntime.Metrics.TotalSignalsDispatched;
            long totalCommands = NexusRuntime.Metrics.TotalCommandsExecuted;
            long sigDelta = totalSignals - _lastTotalSignals;
            long cmdDelta = totalCommands - _lastTotalCommands;
            _lastTotalSignals = totalSignals;
            _lastTotalCommands = totalCommands;
            float sigRate = sigDelta > 0 ? sigDelta / (float)elapsed : 0f;
            float cmdRate = cmdDelta > 0 ? cmdDelta / (float)elapsed : 0f;
            _signalRateBuffer.Push(sigRate);
            _commandRateBuffer.Push(cmdRate);

            RefreshUI(fps, monoMb, sigRate, cmdRate);
        }

        private void RefreshUI(float fps, float monoMb, float sigRate, float cmdRate)
        {
            // Labels. fps <= 0 means no game sample yet — show the placeholder rather
            // than a misleading 0.0 (the old bogus low-FPS display).
            _fpsLabel.text  = fps > 0f ? $"{fps:F1}" : NexusLang.Get("pd_value_placeholder");
            _memLabel.text  = monoMb > 0f ? $"{monoMb:F1}{NexusLang.Get("pd_unit_mb")}" : NexusLang.Get("pd_value_placeholder");
            _gcLabel.text   = _gcGen0Buffer.Count > 0
                ? string.Format(NexusLang.Get("pd_gc_gen0_value"), _gcGen0Buffer.Last())
                : NexusLang.Get("pd_value_placeholder");
            _sigLabel.text  = $"{sigRate:F1}/s";
            _cmdLabel.text  = $"{cmdRate:F1}/s";

            // Fetch each ring buffer ONCE per tick and share the arrays between the sparkline
            // redraw and the stats summary (the previous code called ToArray() on the FPS
            // buffer twice per tick plus LINQ Average/Min on top).
            var fpsArr = _fpsBuffer.ToArray();
            var memArr = _memBuffer.ToArray();
            var gcArr  = _gcGen0Buffer.ToArray();
            var sigArr = _signalRateBuffer.ToArray();
            var cmdArr = _commandRateBuffer.ToArray();

            // Memory chart max: the alarm threshold when plotting deltas; scale above the
            // actual peak when plotting absolute values (no MetricsSampler) so a ~800MB heap
            // does not saturate a chart capped at 512.
            float memMax = _memAlarmMb;
            for (int i = 0; i < memArr.Length; i++)
                if (memArr[i] > memMax) memMax = memArr[i];
            if (memMax > _memAlarmMb) memMax *= 1.15f;

            NexusVisualization.UpdateSparkline(_fpsSparkline, fpsArr, 120f, NexusEditorStyles.AccentGreen,  120f, 32f);
            NexusVisualization.UpdateSparkline(_memSparkline, memArr, memMax, NexusEditorStyles.AccentBlue, 120f, 32f);
            NexusVisualization.UpdateSparkline(_gcSparkline,  gcArr,  20f,  NexusEditorStyles.AccentYellow, 120f, 32f);
            NexusVisualization.UpdateSparkline(_sigSparkline, sigArr, 500f, NexusEditorStyles.AccentPurple, 120f, 32f);
            NexusVisualization.UpdateSparkline(_cmdSparkline, cmdArr, 200f, NexusEditorStyles.AccentOrange, 120f, 32f);

            // Label colors based on alarms. fps <= 0 means the game metric has not been
            // sampled yet (no MetricsSampler in the scene, or recording just started) —
            // do NOT paint that as an alarm, that would be the old bogus 0-4 FPS display.
            _fpsLabel.style.color = new StyleColor(fps > 0f && fps < _fpsAlarm && _alarmsEnabled
                ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentGreen);
            _memLabel.style.color = new StyleColor(_memBuffer.Count > 0 && _memDeltaMb > _memAlarmMb && _alarmsEnabled
                ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentBlue);

            RefreshAlarmLabels();
            RefreshStatsSummary(fps, monoMb, sigRate, cmdRate, fpsArr);
        }

        private void RefreshAlarmLabels()
        {
            // The two alarms are evaluated independently: the old `if (_fpsBuffer.Count == 0)
            // return;` also gated the memory block, so with no MetricsSampler in the scene the
            // memory alarm never rendered even when the delta exceeded the threshold.
            if (_fpsBuffer.Count > 0)
            {
                float lastFps = _fpsBuffer.Last();
                // Guard fps > 0: a 0.0 reading means "not sampled yet" (no MetricsSampler
                // component in the scene, or recording just started), not a real 0 FPS.
                bool fpsAlarm = lastFps > 0f && lastFps < _fpsAlarm && _alarmsEnabled && Application.isPlaying;
                _fpsAlarmLabel.text = fpsAlarm ? string.Format(NexusLang.Get("pd_fps_below"), lastFps.ToString("F1"), _fpsAlarm.ToString("F0")) : "";
                _fpsAlarmLabel.style.display = fpsAlarm ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_memBuffer.Count > 0)
            {
                // Alarm on the session delta (never the absolute editor-inclusive heap).
                bool memAlarm = _memDeltaMb > _memAlarmMb && _alarmsEnabled && Application.isPlaying;
                _memAlarmLabel.text = memAlarm ? string.Format(NexusLang.Get("pd_mem_above"), _memDeltaMb.ToString("F1"), _memAlarmMb.ToString("F0")) : "";
                _memAlarmLabel.style.display = memAlarm ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // Stat-row cache so per-refresh updates mutate existing rows instead of
        // removing/re-creating them (the summary refreshes every 0.5 s while playing).
        private readonly List<VisualElement> _statRowCache = new();

        private void SetStatRow(int index, string key, string value, Color color)
        {
            // Rows live after the card title (child 0). Reuse cached rows; create on demand.
            while (_statRowCache.Count <= index)
            {
                var row = NexusVisualization.CreateStatRow("", "", NexusEditorStyles.TextPrimary);
                _statsContainer.Add(row);
                _statRowCache.Add(row);
            }
            var cachedRow = _statRowCache[index];
            if (cachedRow.childCount >= 2 && cachedRow[0] is Label keyLabel && cachedRow[1] is Label valueLabel)
            {
                keyLabel.text = key;
                valueLabel.text = value;
                valueLabel.style.color = new StyleColor(color);
            }
        }

        private void RefreshStatsSummary(float fps, float monoMb, float sigRate, float cmdRate, float[] fpsArr)
        {
            if (_statsContainer == null) return;

            if (fpsArr.Length > 0)
            {
                // Manual single pass for avg + min — LINQ Average/Min create iterators and
                // delegate allocs on every 0.5 s tick.
                float sum = 0f, min = float.MaxValue;
                for (int i = 0; i < fpsArr.Length; i++)
                {
                    sum += fpsArr[i];
                    if (fpsArr[i] < min) min = fpsArr[i];
                }
                SetStatRow(0, NexusLang.Get("pd_fps_current"), $"{fps:F1}", ColorForFps(fps));
                SetStatRow(1, NexusLang.Get("pd_fps_avg"), $"{sum / fpsArr.Length:F1}", NexusEditorStyles.TextPrimary);
                SetStatRow(2, NexusLang.Get("pd_fps_min"),  $"{min:F1}", NexusEditorStyles.AccentOrange);
            }
            SetStatRow(3, NexusLang.Get("pd_mono_heap_short"),
                monoMb > 0f
                    ? _memBuffer.Count > 0
                        ? $"{monoMb:F1} MB  {NexusLang.Get("pd_mem_delta")} {_memDeltaMb:+0.0;-0.0} MB"
                        : $"{monoMb:F1} MB"
                    : NexusLang.Get("pd_value_placeholder"),
                NexusEditorStyles.AccentBlue);
            SetStatRow(4, NexusLang.Get("pd_signals_current"), $"{sigRate:F1}", NexusEditorStyles.AccentPurple);
            SetStatRow(5, NexusLang.Get("pd_commands_current"), $"{cmdRate:F1}", NexusEditorStyles.AccentOrange);
            SetStatRow(6, NexusLang.Get("pd_gc_delta"),
                _gcGen0Buffer.Count > 0 ? $"{_gcGen0Buffer.Last():F0}" : NexusLang.Get("pd_value_placeholder"),
                NexusEditorStyles.AccentYellow);
        }

        private Color ColorForFps(float fps) =>
            fps >= _fpsAlarm * 2 ? NexusEditorStyles.AccentGreen
            : fps >= _fpsAlarm   ? NexusEditorStyles.AccentYellow
                                 : NexusEditorStyles.AccentRed;

        // ── Control actions ───────────────────────────────────────

        private void StartRecording()
        {
            _recording = true;
            _lastGen0  = GC.CollectionCount(0);
            _lastSampleTime = EditorApplication.timeSinceStartup;
            // Baseline the counters so the first tick reports a delta since recording start
            // (not since app boot — a huge first-sample spike otherwise).
            _lastTotalSignals = NexusRuntime.Metrics.TotalSignalsDispatched;
            _lastTotalCommands = NexusRuntime.Metrics.TotalCommandsExecuted;
            _memBaselineMb = ReadMonoUsedMb(out _);
            _memDeltaMb = 0f;
            // Drop the previous session's samples: auto-start on every EnteredPlayMode makes
            // this the hot path, and without the clears the sparklines + avg/min/GC summary
            // rows would keep plotting the last session's tail for up to ~60 s (120 × 0.5 s).
            _fpsBuffer.Clear(); _memBuffer.Clear(); _gcGen0Buffer.Clear();
            _signalRateBuffer.Clear(); _commandRateBuffer.Clear();
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
            _lastTotalSignals = NexusRuntime.Metrics.TotalSignalsDispatched;
            _lastTotalCommands = NexusRuntime.Metrics.TotalCommandsExecuted;
            _memBaselineMb = ReadMonoUsedMb(out _);
            _memDeltaMb = 0f;
            PerformanceMonitor.ClearHistory();
        }

        // ── Memory read ─────────────────────────────────────────
        private static float ReadMonoUsedMb(out bool fromRecordedMetric)
        {
            float recorded = PerformanceMonitor.GetMetric("MonoUsed");
            if (recorded > 0f)
            {
                fromRecordedMetric = true;
                return recorded;
            }
            fromRecordedMetric = false;
            return UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024f * 1024f);
        }

        private void ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Index,FPS,MonoUsedMB,GCGen0Delta,Signals/s,Commands/s"); // MonoUsedMB: session delta (sampler present) or absolute (fallback)
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
