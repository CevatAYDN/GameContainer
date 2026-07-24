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
    /// Full-featured error dashboard: severity summary, severity/category/text filtering,
    /// color-coded expandable rows (with stack trace + context), CSV export, and a capture toggle.
    /// Backed by the runtime <see cref="ErrorCollection"/> API.
    /// </summary>
    public class ErrorDashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "ErrorDashboard";
        public override string DisplayName => NexusLang.Get("tab_errordashboard");
        public override int Order => 9;

        private const int DefaultLimit = 100;

        private VisualElement _view;
        private VisualElement _summaryRow;
        private ScrollView _scrollView;
        private Label _statusBar;
        private IVisualElementScheduledItem _refreshSchedule;

        private ErrorCollection.ErrorSeverity? _minSeverity;
        private ErrorCollection.ErrorCategory? _categoryFilter;
        private string _searchText = string.Empty;
        private bool _dirty = true;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            _view.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("tab_error_dashboard").ToUpper()));

            _summaryRow = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 4
                }
            };
            _view.Add(_summaryRow);

            _view.Add(BuildFilterBar());

            _scrollView = new ScrollView
            {
                style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 6 }
            };
            _view.Add(_scrollView);

            _statusBar = NexusEditorStyles.CreateStatusBar();
            _view.Add(_statusBar);

            ErrorCollection.OnErrorAdded += OnErrorAdded;
            _refreshSchedule = _view.schedule.Execute(RefreshIfDirty).Every(500);

            _dirty = true;
            RefreshUI();
            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            ErrorCollection.OnErrorAdded -= OnErrorAdded;
            base.OnDisable();
        }

        // ─── Filter Bar ───
        private VisualElement BuildFilterBar()
        {
            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.Center,
                    paddingLeft = 10, paddingRight = 10, paddingBottom = 8
                }
            };

            bar.Add(new Label("Severity:")
            {
                style = { color = NexusEditorStyles.TextSecondary, fontSize = 10, marginRight = 4 }
            });
            bar.Add(SeverityFilterButton("All", null));
            bar.Add(SeverityFilterButton("Info+", ErrorCollection.ErrorSeverity.Info));
            bar.Add(SeverityFilterButton("Warn+", ErrorCollection.ErrorSeverity.Warning));
            bar.Add(SeverityFilterButton("Error+", ErrorCollection.ErrorSeverity.Error));
            bar.Add(SeverityFilterButton("Critical", ErrorCollection.ErrorSeverity.Critical));

            var categoryEnum = new EnumField("Category", ErrorCollection.ErrorCategory.General)
            {
                style = { marginLeft = 12, minWidth = 150 }
            };
            var catToggle = new Toggle("Filter Category") { style = { marginLeft = 6 } };
            categoryEnum.RegisterValueChangedCallback(evt =>
            {
                if (catToggle.value)
                {
                    _categoryFilter = (ErrorCollection.ErrorCategory)evt.newValue;
                    _dirty = true;
                }
            });
            catToggle.RegisterValueChangedCallback(evt =>
            {
                _categoryFilter = evt.newValue
                    ? (ErrorCollection.ErrorCategory)categoryEnum.value
                    : (ErrorCollection.ErrorCategory?)null;
                _dirty = true;
            });
            bar.Add(catToggle);
            bar.Add(categoryEnum);

            var search = new TextField { style = { marginLeft = 12, minWidth = 160 } };
            search.textEdition.placeholder = "Search message...";
            search.RegisterValueChangedCallback(evt =>
            {
                _searchText = evt.newValue ?? string.Empty;
                _dirty = true;
            });
            bar.Add(search);

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            bar.Add(spacer);

            var captureToggle = new Toggle("Capture") { value = ErrorCollection.Enabled, style = { marginRight = 8 } };
            captureToggle.RegisterValueChangedCallback(evt => ErrorCollection.Enabled = evt.newValue);
            bar.Add(captureToggle);

            bar.Add(NexusEditorStyles.CreateButton("Export CSV", ExportCsv, NexusEditorStyles.BtnBlue));
            bar.Add(NexusEditorStyles.CreateButton("Clear", () => { ErrorCollection.Clear(); _dirty = true; RefreshUI(); }, NexusEditorStyles.BtnRed));

            return bar;
        }

        private VisualElement SeverityFilterButton(string label, ErrorCollection.ErrorSeverity? severity)
        {
            bool active = _minSeverity == severity;
            var color = active ? NexusEditorStyles.BtnBlue : NexusEditorStyles.BtnGray;
            return NexusEditorStyles.CreateFilterButton(label, () =>
            {
                _minSeverity = severity;
                _dirty = true;
                RefreshUI();
            }, color);
        }

        // ─── Refresh ───
        private void RefreshIfDirty()
        {
            if (_dirty) RefreshUI();
        }

        private void RefreshUI()
        {
            _dirty = false;
            RebuildSummary();
            RebuildList();
            RebuildFilterBarActiveState();
        }

        private void RebuildSummary()
        {
            _summaryRow.Clear();
            var counts = ErrorCollection.GetSeverityCounts();

            _summaryRow.Add(SummaryPill("TOTAL", ErrorCollection.TotalErrorCount, NexusEditorStyles.CardBg, Color.white));
            _summaryRow.Add(SummaryPill("INFO", GetCount(counts, ErrorCollection.ErrorSeverity.Info), NexusEditorStyles.CardBgBlue, NexusEditorStyles.AccentBlue));
            _summaryRow.Add(SummaryPill("WARN", GetCount(counts, ErrorCollection.ErrorSeverity.Warning), NexusEditorStyles.CardBgYellow, NexusEditorStyles.AccentYellow));
            _summaryRow.Add(SummaryPill("ERROR", GetCount(counts, ErrorCollection.ErrorSeverity.Error), NexusEditorStyles.CardBgRed, NexusEditorStyles.AccentRed));
            _summaryRow.Add(SummaryPill("CRITICAL", GetCount(counts, ErrorCollection.ErrorSeverity.Critical), NexusEditorStyles.CardBgRed, NexusEditorStyles.AccentOrange));
        }

        private static int GetCount(Dictionary<ErrorCollection.ErrorSeverity, int> counts, ErrorCollection.ErrorSeverity s)
            => counts.TryGetValue(s, out var c) ? c : 0;

        private VisualElement SummaryPill(string label, int count, Color bg, Color accent)
        {
            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = bg,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    paddingLeft = 10, paddingRight = 10, paddingTop = 4, paddingBottom = 4,
                    marginRight = 6, marginBottom = 4,
                    minWidth = 70, alignItems = Align.Center
                }
            };
            card.Add(new Label(count.ToString())
            {
                style = { color = accent, fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold }
            });
            card.Add(new Label(label) { style = { color = NexusEditorStyles.TextSecondary, fontSize = 9 } });
            return card;
        }

        private void RebuildList()
        {
            _scrollView.Clear();

            var errors = ErrorCollection.GetErrors(_minSeverity, _categoryFilter, DefaultLimit);
            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                errors = errors.Where(e =>
                    (e.Message != null && e.Message.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (e.RelatedType != null && e.RelatedType.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToArray();
            }

            foreach (var error in errors)
                _scrollView.Add(BuildErrorRow(error));

            if (errors.Length == 0)
                _scrollView.Add(NexusEditorStyles.CreateEmptyState("No errors match the current filters."));

            if (_statusBar != null)
                _statusBar.text = $"Showing {errors.Length} of {ErrorCollection.TotalErrorCount} (limit {DefaultLimit})   |   Capture: {(ErrorCollection.Enabled ? "ON" : "OFF")}";
        }

        private VisualElement BuildErrorRow(ErrorCollection.ErrorEntry error)
        {
            var (bg, accent) = SeverityColors(error.Severity);

            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = bg,
                    borderLeftWidth = 3, borderLeftColor = accent,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 4, paddingBottom = 4,
                    marginBottom = 4
                }
            };

            var foldout = new Foldout { value = false };
            foldout.text = $"[{error.Severity}] {error.Message}";
            var toggleLabel = foldout.Q<Label>();
            if (toggleLabel != null)
            {
                toggleLabel.style.color = accent;
                toggleLabel.style.fontSize = 11;
                toggleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                toggleLabel.style.whiteSpace = WhiteSpace.Normal;
            }

            var meta = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginBottom = 2 } };
            meta.Add(NexusEditorStyles.CreatePill(error.Category.ToString(), NexusEditorStyles.CardBgAlt, NexusEditorStyles.TextPrimary));
            if (error.Count > 1)
                meta.Add(NexusEditorStyles.CreatePill($"x{error.Count}", NexusEditorStyles.CardBgYellow, NexusEditorStyles.AccentYellow));
            meta.Add(new Label(error.Timestamp.ToString("HH:mm:ss"))
            {
                style = { color = NexusEditorStyles.TextSecondary, fontSize = 9, marginLeft = 6 }
            });
            if (!string.IsNullOrEmpty(error.RelatedType))
                meta.Add(new Label(error.RelatedType) { style = { color = NexusEditorStyles.AccentPurpleText, fontSize = 9, marginLeft = 6 } });
            foldout.Add(meta);

            if (!string.IsNullOrEmpty(error.Context))
                foldout.Add(new Label($"Context: {error.Context}") { style = { color = NexusEditorStyles.TextSecondary, fontSize = 10, whiteSpace = WhiteSpace.Normal } });

            if (!string.IsNullOrEmpty(error.StackTrace))
            {
                var stack = new TextField { multiline = true, isReadOnly = true, value = error.StackTrace };
                stack.style.marginTop = 4;
                stack.style.whiteSpace = WhiteSpace.Normal;
                foldout.Add(stack);
            }

            card.Add(foldout);
            return card;
        }

        private static (Color bg, Color accent) SeverityColors(ErrorCollection.ErrorSeverity severity)
        {
            switch (severity)
            {
                case ErrorCollection.ErrorSeverity.Info: return (NexusEditorStyles.CardBgBlue, NexusEditorStyles.AccentBlue);
                case ErrorCollection.ErrorSeverity.Warning: return (NexusEditorStyles.CardBgYellow, NexusEditorStyles.AccentYellow);
                case ErrorCollection.ErrorSeverity.Error: return (NexusEditorStyles.CardBgRed, NexusEditorStyles.AccentRed);
                case ErrorCollection.ErrorSeverity.Critical: return (NexusEditorStyles.CardBgRed, NexusEditorStyles.AccentOrange);
                default: return (NexusEditorStyles.CardBg, NexusEditorStyles.TextPrimary);
            }
        }

        private void RebuildFilterBarActiveState()
        {
            // Rebuild filter bar so severity buttons reflect the active selection color.
            if (_view == null) return;
            var oldBar = _view.Children().ElementAtOrDefault(2);
            var newBar = BuildFilterBar();
            if (oldBar != null) _view.Insert(_view.IndexOf(oldBar), newBar);
            oldBar?.RemoveFromHierarchy();
        }

        private void ExportCsv()
        {
            var errors = ErrorCollection.GetErrors(_minSeverity, _categoryFilter, ErrorCollection.MaxErrors);
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Severity,Category,Count,RelatedType,Message");
            foreach (var e in errors)
            {
                sb.AppendLine(string.Join(",",
                    e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    e.Severity,
                    e.Category,
                    e.Count,
                    Csv(e.RelatedType),
                    Csv(e.Message)));
            }

            string path = System.IO.Path.Combine(
                Application.dataPath, "..",
                $"nexus_errors_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllText(path, sb.ToString());
            Debug.Log($"[Nexus] Error report exported: {System.IO.Path.GetFullPath(path)}");
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private void OnErrorAdded(ErrorCollection.ErrorEntry error) => _dirty = true;
    }
}
