using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    /// <summary>
    /// Visualization helpers extracted from <see cref="NexusEditorStyles"/>.
    /// Keeps the design system focused on palette/constants while visualization
    /// utilities live in their own module.
    /// </summary>
    internal static class NexusVisualization
    {
        // ─── Sparkline (mini bar chart) ───────────────────────────
        /// <summary>
        /// Creates a simple horizontal bar-chart sparkline from a float[] history.
        /// Width is fixed; bar heights are normalized to the provided max value.
        /// </summary>
        internal static VisualElement CreateSparkline(float[] values, float maxValue,
            Color barColor, float width = 120f, float height = 32f)
        {
            var container = new VisualElement
            {
                style =
                {
                    width = width, height = height,
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.FlexEnd,
                    backgroundColor = new StyleColor(NexusEditorStyles.DarkPanel),
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                    paddingLeft = 2, paddingRight = 2, paddingTop = 2, paddingBottom = 2,
                    overflow = Overflow.Hidden
                }
            };

            if (values == null || values.Length == 0) return container;

            float barW = Mathf.Max(2f, width / Mathf.Min(values.Length, 60));
            int startIdx = Mathf.Max(0, values.Length - Mathf.FloorToInt(width / barW));
            float effectiveMax = maxValue > 0 ? maxValue : 1f;

            for (int i = startIdx; i < values.Length; i++)
            {
                float ratio = Mathf.Clamp01(values[i] / effectiveMax);
                float barH = Mathf.Max(1f, ratio * (height - 4f));
                var bar = new VisualElement
                {
                    style =
                    {
                        width = barW - 1,
                        height = barH,
                        backgroundColor = new StyleColor(barColor),
                        marginRight = 1,
                        borderTopLeftRadius = 1, borderTopRightRadius = 1
                    }
                };
                container.Add(bar);
            }
            return container;
        }

        /// <summary>
        /// Updates an existing sparkline element in-place, REUSING its bar children so the
        /// per-refresh redraw (the dashboard refreshes every 0.5 s while playing) does not
        /// allocate and discard hundreds of VisualElements.
        /// </summary>
        internal static void UpdateSparkline(VisualElement sparkline, float[] values,
            float maxValue, Color barColor, float width = 120f, float height = 32f)
        {
            if (sparkline == null) return;
            if (values == null || values.Length == 0)
            {
                sparkline.Clear();
                return;
            }

            float barW = Mathf.Max(2f, width / Mathf.Min(values.Length, 60));
            int startIdx = Mathf.Max(0, values.Length - Mathf.FloorToInt(width / barW));
            float effectiveMax = maxValue > 0 ? maxValue : 1f;

            int barCount = values.Length - startIdx;

            // Grow or shrink the child list to match the required bar count.
            while (sparkline.childCount < barCount)
            {
                sparkline.Add(new VisualElement
                {
                    style =
                    {
                        marginRight = 1,
                        borderTopLeftRadius = 1, borderTopRightRadius = 1
                    }
                });
            }
            while (sparkline.childCount > barCount)
                sparkline.RemoveAt(sparkline.childCount - 1);

            for (int i = 0; i < barCount; i++)
            {
                float ratio = Mathf.Clamp01(values[startIdx + i] / effectiveMax);
                float barH = Mathf.Max(1f, ratio * (height - 4f));
                var bar = sparkline[i];
                bar.style.width = barW - 1;
                bar.style.height = barH;
                bar.style.backgroundColor = new StyleColor(barColor);
            }
        }

        // ─── Gauge (value indicator bar) ─────────────────────────
        /// <summary>Creates a horizontal fill-bar gauge showing a value 0..max.</summary>
        internal static VisualElement CreateGauge(float value, float max,
            Color fillColor, float width = 100f, float height = 6f)
        {
            var bg = new VisualElement
            {
                style =
                {
                    width = width, height = height,
                    backgroundColor = new StyleColor(NexusEditorStyles.DarkPanel),
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                    overflow = Overflow.Hidden
                }
            };
            float ratio = max > 0 ? Mathf.Clamp01(value / max) : 0f;
            var fill = new VisualElement
            {
                style =
                {
                    width = new Length(ratio * 100f, LengthUnit.Percent),
                    height = height,
                    backgroundColor = new StyleColor(fillColor),
                    borderTopLeftRadius = 3, borderBottomLeftRadius = 3
                }
            };
            bg.Add(fill);
            return bg;
        }

        // ─── Data Table ─────────────────────────────────────────
        internal static VisualElement CreateDataTable(
            (string Header, float WidthFraction)[] columns,
            IEnumerable<string[]> rows,
            float tableWidth = 400f)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;

            var header = new VisualElement();
            header.AddToClassList(NexusEditorStyles.ClassHeader);
            foreach (var col in columns)
            {
                var lbl = new Label(col.Header);
                lbl.AddToClassList("nexus-table-header-text");
                lbl.style.width = new Length(col.WidthFraction * 100f, LengthUnit.Percent);
                header.Add(lbl);
            }
            container.Add(header);

            bool alt = false;
            foreach (var row in rows)
            {
                var dataRow = new VisualElement();
                dataRow.AddToClassList(NexusEditorStyles.ClassRow);
                if (alt)
                    dataRow.AddToClassList("alt");
                int c = 0;
                for (; c < columns.Length && c < row.Length; c++)
                {
                    var cell = new Label(row[c] ?? "");
                    cell.AddToClassList("nexus-table-cell");
                    cell.style.width = new Length(columns[c].WidthFraction * 100f, LengthUnit.Percent);
                    dataRow.Add(cell);
                }
                container.Add(dataRow);
                alt = !alt;
            }

            return container;
        }

        // ─── Stat Row ────────────────────────────────────────────
        internal static VisualElement CreateStatRow(string key, string value,
            Color valueColor = default, float fontSize = 10f)
        {
            if (valueColor == default) valueColor = NexusEditorStyles.TextPrimary;
            var row = new VisualElement();
            row.AddToClassList("nexus-stat-row");
            row.Add(new Label(key)
            {
                style = { fontSize = fontSize, color = new StyleColor(NexusEditorStyles.TextSecondary) }
            });
            row.Add(new Label(value)
            {
                style = { fontSize = fontSize, color = new StyleColor(valueColor), unityFontStyleAndWeight = FontStyle.Bold }
            });
            return row;
        }
    }
}
