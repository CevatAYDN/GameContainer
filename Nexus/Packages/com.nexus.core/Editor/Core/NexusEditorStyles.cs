using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    internal static class NexusEditorStyles
    {
        // ─── Color Palette ───
        internal static readonly Color Background = new(0.12f, 0.12f, 0.14f);
        internal static readonly Color CardBg = new(0.18f, 0.18f, 0.2f);
        internal static readonly Color CardBgAlt = new(0.16f, 0.16f, 0.18f);
        internal static readonly Color CardBgGreen = new(0.14f, 0.18f, 0.14f);
        internal static readonly Color CardBgYellow = new(0.2f, 0.18f, 0.14f);
        internal static readonly Color CardBgRed = new(0.2f, 0.14f, 0.14f);
        internal static readonly Color CardBgBlue = new(0.14f, 0.16f, 0.2f);
        internal static readonly Color AccentBlue = new(0.3f, 0.8f, 1f);
        internal static readonly Color AccentGreen = new(0.4f, 1f, 0.4f);
        internal static readonly Color AccentYellow = new(1f, 0.85f, 0.3f);
        internal static readonly Color AccentOrange = new(1f, 0.7f, 0.2f);
        internal static readonly Color AccentPurple = new(0.8f, 0.6f, 0.9f);
        internal static readonly Color AccentRed = new(1f, 0.3f, 0.3f);
        internal static readonly Color TextPrimary = new(0.85f, 0.85f, 0.85f);
        internal static readonly Color TextSecondary = new(0.6f, 0.6f, 0.6f);
        internal static readonly Color BorderColor = new(0.2f, 0.2f, 0.22f);
        internal static readonly Color BorderLight = new(0.25f, 0.25f, 0.28f);
        internal static readonly Color SignalBlue = new(0.7f, 0.85f, 1f);
        internal static readonly Color BtnBlue = new(0.2f, 0.35f, 0.5f);
        internal static readonly Color BtnPurple = new(0.3f, 0.2f, 0.4f);
        internal static readonly Color BtnTeal = new(0.2f, 0.3f, 0.3f);
        internal static readonly Color BtnGray = new(0.25f, 0.25f, 0.28f);
        internal static readonly Color BtnGreen = new(0.2f, 0.4f, 0.2f);
        internal static readonly Color BtnRed = new(0.5f, 0.2f, 0.2f);
        internal static readonly Color BtnYellow = new(0.5f, 0.4f, 0.1f);
        internal static readonly Color DarkPanel = new(0.08f, 0.08f, 0.1f);
        internal static readonly Color SidebarBg = new(0.1f, 0.1f, 0.12f);
        internal static readonly Color ToolbarBg = new(0.1f, 0.1f, 0.12f);
        internal static readonly Color HighlightBg = new(0.18f, 0.22f, 0.28f);
        internal static readonly Color SelectedRow = new(0.18f, 0.22f, 0.28f);
        internal static readonly Color RowAlt = new(0.15f, 0.15f, 0.17f);
        internal static readonly Color RowBase = new(0.18f, 0.18f, 0.2f);
        internal static readonly Color TableHeaderBg = new(0.16f, 0.16f, 0.18f);

        internal static readonly Color AccentBlueText = new(0.7f, 0.9f, 1f);
        internal static readonly Color AccentPurpleText = new(0.9f, 0.7f, 1f);
        internal static readonly Color AccentGreenText = new(0.6f, 1f, 0.6f);
        internal static readonly Color DimText = new(0.4f, 0.4f, 0.4f);

        internal static readonly Color TitleColor = AccentBlue;

        internal const float CardRadius = 6f;
        internal const float BtnRadius = 4f;
        internal const float ToolbarPadding = 8f;
        internal const float CardPadding = 12f;

        // ─── USS Class Name Constants ───
        internal const string ClassSidebarBtn = "nexus-sidebar-btn";
        internal const string ClassActiveSidebar = "nexus-sidebar-btn active";
        internal const string ClassCard = "nexus-card";
        internal const string ClassPillGreen = "nexus-pill-green";
        internal const string ClassPillBlue = "nexus-pill-blue";
        internal const string ClassPillPurple = "nexus-pill-purple";
        internal const string ClassPillYellow = "nexus-pill-yellow";
        internal const string ClassFilterBtn = "nexus-filter-btn";
        internal const string ClassActionBtn = "nexus-action-btn";
        internal const string ClassToolbar = "nexus-toolbar";
        internal const string ClassEmptyState = "nexus-empty-state";
        internal const string ClassSectionTitle = "nexus-section-title";
        internal const string ClassDashboardActionCard = "nexus-dashboard-action-card";

        // ─── USS Loading ───
        internal static void LoadTheme(VisualElement root)
        {
            var theme = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.nexus.core/Editor/Styles/NexusTheme.uss");
            if (theme != null)
                root.styleSheets.Add(theme);
        }

        // ─── Icon Helpers ───
        internal static Texture2D GetIcon(string iconName)
        {
            // Load from editor default resources; returns null silently if not found.
            return EditorGUIUtility.Load($"Editor Default Resources/Icons/{iconName}.png") as Texture2D;
        }

        internal static VisualElement CreateColorIcon(Color color, int size = 16)
        {
            var icon = new VisualElement();
            icon.style.width = size;
            icon.style.height = size;
            icon.style.borderTopLeftRadius = size / 2;
            icon.style.borderTopRightRadius = size / 2;
            icon.style.borderBottomLeftRadius = size / 2;
            icon.style.borderBottomRightRadius = size / 2;
            icon.style.backgroundColor = new StyleColor(color);
            icon.style.marginRight = 8;
            icon.style.flexShrink = 0;
            return icon;
        }

        // Legacy aliases — kept for compatibility with agent-generated plugin code.
        internal static void SetIcon(VisualElement element, string iconName) { }
        internal static VisualElement CreateIcon(string iconName, int size = 16) => CreateColorIcon(Color.gray, size);

        // ─── Sidebar Helpers ───
        internal static Button CreateSidebarButton(string label, string iconName, System.Action onClick)
        {
            var btn = new Button(onClick);
            btn.AddToClassList(ClassSidebarBtn);

            if (!string.IsNullOrEmpty(iconName))
            {
                var icon = CreateIcon(iconName);
                btn.Add(icon);
            }

            var txt = new Label(label);
            btn.Add(txt);

            return btn;
        }

        // ─── Builder Methods ───
        internal static VisualElement CreateCard(Color bgColor)
        {
            var card = new VisualElement();
            card.AddToClassList(ClassCard);
            card.style.backgroundColor = new StyleColor(bgColor);
            return card;
        }

        internal static Label CreateTitle(string text, Color color, int fontSize = 11)
        {
            return new Label(text)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    fontSize = fontSize,
                    color = new StyleColor(color),
                    marginBottom = 6
                }
            };
        }

        internal static Label CreateBody(string text, int fontSize = 11)
        {
            return new Label(text)
            {
                style =
                {
                    color = new StyleColor(TextPrimary),
                    fontSize = fontSize,
                    whiteSpace = WhiteSpace.Normal
                }
            };
        }

        internal static Label CreateHint(string text)
        {
            return new Label(text)
            {
                style =
                {
                    color = new StyleColor(TextSecondary),
                    fontSize = 10,
                    whiteSpace = WhiteSpace.Normal
                }
            };
        }

        internal static Button CreateButton(string label, System.Action onClick, Color bgColor)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList("nexus-btn");
            if (bgColor != default)
                btn.style.backgroundColor = new StyleColor(bgColor);
            return btn;
        }

        internal static VisualElement CreateInfoCard(VisualElement parent, string title, Color titleColor, Color bgColor, string description)
        {
            var card = CreateCard(bgColor);
            var titleLabel = CreateTitle(title, titleColor);
            card.Add(titleLabel);
            if (!string.IsNullOrEmpty(description))
            {
                var desc = CreateBody(description);
                card.Add(desc);
            }
            parent.Add(card);
            return card;
        }

        internal static VisualElement CreateActionGroup(VisualElement parent, string groupTitle)
        {
            var card = CreateInfoCard(parent, groupTitle, TextSecondary, CardBgAlt, "");
            return card;
        }

        internal static void AddActionButton(VisualElement parent, string label, System.Action onClick, Color bgColor)
        {
            parent.Add(CreateButton(label, onClick, bgColor));
        }

        internal static VisualElement CreateToolbar(string windowTitle)
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList(ClassToolbar);

            var titleLabel = new Label(windowTitle);
            titleLabel.AddToClassList("nexus-toolbar-title");
            toolbar.Add(titleLabel);

            return toolbar;
        }

        internal static void AddToolbarButton(VisualElement toolbar, string label, System.Action onClick)
        {
            var btn = new Button(onClick) { text = label };
            btn.style.backgroundColor = new StyleColor(BtnGray);
            btn.style.borderTopLeftRadius = BtnRadius;
            btn.style.borderTopRightRadius = BtnRadius;
            btn.style.borderBottomLeftRadius = BtnRadius;
            btn.style.borderBottomRightRadius = BtnRadius;
            btn.style.color = Color.white;
            btn.style.paddingLeft = 10;
            btn.style.paddingRight = 10;
            btn.style.marginLeft = 5;
            toolbar.Add(btn);
        }

        internal static Label CreateStatusBar()
        {
            return new Label
            {
                style =
                {
                    backgroundColor = new StyleColor(ToolbarBg),
                    color = new StyleColor(TextSecondary),
                    fontSize = 10,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 4,
                    paddingBottom = 4,
                    borderTopWidth = 1,
                    borderTopColor = new StyleColor(BorderColor),
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
        }

        internal static Label CreateEmptyState(string text)
        {
            return new Label(text)
            {
                style =
                {
                    color = new StyleColor(TextSecondary),
                    fontSize = 11,
                    alignSelf = Align.Center,
                    marginTop = 20
                }
            };
        }

        internal static VisualElement CreatePill(string text, Color bgColor, Color textColor)
        {
            var pill = new Label(text);
            pill.style.fontSize = 8;
            pill.style.backgroundColor = new StyleColor(bgColor);
            pill.style.color = new StyleColor(textColor);
            pill.style.paddingLeft = 3;
            pill.style.paddingRight = 3;
            pill.style.paddingTop = 1;
            pill.style.paddingBottom = 1;
            pill.style.marginLeft = 6;
            pill.style.borderTopLeftRadius = 2;
            pill.style.borderTopRightRadius = 2;
            pill.style.borderBottomLeftRadius = 2;
            pill.style.borderBottomRightRadius = 2;
            pill.style.unityFontStyleAndWeight = FontStyle.Bold;
            return pill;
        }

        internal static VisualElement CreateFilterButton(string label, System.Action onClick, Color activeColor)
        {
            var btn = new Button(onClick) { text = label };
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
            btn.style.borderTopWidth = 0;
            btn.style.borderBottomWidth = 0;
            btn.style.borderLeftWidth = 0;
            btn.style.borderRightWidth = 0;
            btn.style.backgroundColor = new StyleColor(activeColor);
            btn.style.color = Color.white;
            return btn;
        }

        internal static VisualElement CreateStatusDot(Color dotColor, int size = 6)
        {
            var dot = new VisualElement();
            dot.style.width = size;
            dot.style.height = size;
            dot.style.borderTopLeftRadius = size / 2;
            dot.style.borderTopRightRadius = size / 2;
            dot.style.borderBottomLeftRadius = size / 2;
            dot.style.borderBottomRightRadius = size / 2;
            dot.style.marginRight = 6;
            dot.style.flexShrink = 0;
            dot.style.backgroundColor = new StyleColor(dotColor);
            return dot;
        }

        internal static VisualElement CreateTag(string text, Color bgColor, Color textColor)
        {
            var tag = new Label(text);
            tag.style.unityFontStyleAndWeight = FontStyle.Bold;
            tag.style.fontSize = 8;
            tag.style.paddingLeft = 4;
            tag.style.paddingRight = 4;
            tag.style.paddingTop = 1;
            tag.style.paddingBottom = 1;
            tag.style.borderTopLeftRadius = 2;
            tag.style.borderTopRightRadius = 2;
            tag.style.borderBottomLeftRadius = 2;
            tag.style.borderBottomRightRadius = 2;
            tag.style.marginRight = 6;
            tag.style.backgroundColor = new StyleColor(bgColor);
            tag.style.color = new StyleColor(textColor);
            return tag;
        }

        internal static VisualElement CreateWarningBox(string text)
        {
            var box = new Label(text);
            box.style.color = new StyleColor(AccentOrange);
            box.style.backgroundColor = new StyleColor(new Color(0.2f, 0.15f, 0.1f));
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.paddingTop = 8;
            box.style.paddingBottom = 8;
            box.style.marginTop = 8;
            box.style.fontSize = 10;
            box.style.whiteSpace = WhiteSpace.Normal;
            box.style.borderTopLeftRadius = 4;
            box.style.borderTopRightRadius = 4;
            box.style.borderBottomLeftRadius = 4;
            box.style.borderBottomRightRadius = 4;
            return box;
        }

        internal static VisualElement CreateSectionTitle(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 10;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new StyleColor(TextSecondary);
            label.style.marginBottom = 4;
            return label;
        }

        internal static void MakeRow(VisualElement row, Color bgColor, bool clickable = false)
        {
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 8;
            row.style.paddingRight = 8;
            row.style.paddingTop = 5;
            row.style.paddingBottom = 5;
            row.style.marginTop = 2;
            row.style.marginBottom = 2;
            row.style.borderTopLeftRadius = 4;
            row.style.borderTopRightRadius = 4;
            row.style.borderBottomLeftRadius = 4;
            row.style.borderBottomRightRadius = 4;
            row.style.backgroundColor = new StyleColor(bgColor);
            // clickable styling is applied via hover in USS
        }

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
                    backgroundColor = new StyleColor(DarkPanel),
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                    paddingLeft = 2, paddingRight = 2, paddingTop = 2, paddingBottom = 2,
                    overflow = Overflow.Hidden
                }
            };

            if (values == null || values.Length == 0) return container;

            // Show last N bars that fit in width
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

        /// <summary>Updates an existing sparkline element in-place (removes children and redraws).</summary>
        internal static void UpdateSparkline(VisualElement sparkline, float[] values,
            float maxValue, Color barColor, float width = 120f, float height = 32f)
        {
            sparkline.Clear();
            if (values == null || values.Length == 0) return;

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
                sparkline.Add(bar);
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
                    backgroundColor = new StyleColor(DarkPanel),
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

        // ─── Stat Row (key: value pair) ───────────────────────────
        /// <summary>Creates a single-line key/value label row for stat displays.</summary>
        internal static VisualElement CreateStatRow(string key, string value,
            Color valueColor = default, float fontSize = 10f)
        {
            if (valueColor == default) valueColor = TextPrimary;
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    justifyContent = Justify.SpaceBetween,
                    paddingLeft = 8, paddingRight = 8,
                    paddingTop = 3, paddingBottom = 3,
                    borderBottomWidth = 1,
                    borderBottomColor = new StyleColor(BorderColor)
                }
            };
            row.Add(new Label(key)
            {
                style = { fontSize = fontSize, color = new StyleColor(TextSecondary) }
            });
            row.Add(new Label(value)
            {
                style = { fontSize = fontSize, color = new StyleColor(valueColor), unityFontStyleAndWeight = FontStyle.Bold }
            });
            return row;
        }

        // ─── Live Badge ───────────────────────────────────────────
        /// <summary>Creates an animated "● LIVE" badge label.</summary>
        internal static Label CreateLiveBadge()
        {
            var label = new Label("● LIVE")
            {
                style =
                {
                    fontSize = 9,
                    color = new StyleColor(AccentGreen),
                    unityFontStyleAndWeight = FontStyle.Bold,
                    paddingLeft = 6, paddingRight = 6,
                    paddingTop = 2, paddingBottom = 2,
                    backgroundColor = new StyleColor(new Color(0.1f, 0.3f, 0.1f)),
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3
                }
            };
            return label;
        }

        // ─── Data Table ───────────────────────────────────────────
        /// <summary>
        /// Creates a simple scrollable table with a header row and data rows.
        /// columns: (header, width fraction 0-1)
        /// rows: string[] per row matching column count
        /// </summary>
        internal static VisualElement CreateDataTable(
            (string Header, float WidthFraction)[] columns,
            System.Collections.Generic.IEnumerable<string[]> rows,
            float tableWidth = 400f)
        {
            var container = new VisualElement { style = { flexDirection = FlexDirection.Column } };

            // Header
            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    backgroundColor = new StyleColor(TableHeaderBg),
                    paddingLeft = 4, paddingRight = 4, paddingTop = 4, paddingBottom = 4,
                    borderBottomWidth = 1, borderBottomColor = new StyleColor(BorderColor)
                }
            };
            foreach (var col in columns)
            {
                header.Add(new Label(col.Header)
                {
                    style =
                    {
                        width = new Length(col.WidthFraction * 100f, LengthUnit.Percent),
                        fontSize = 9,
                        unityFontStyleAndWeight = FontStyle.Bold,
                        color = new StyleColor(TextSecondary)
                    }
                });
            }
            container.Add(header);

            // Data rows
            bool alt = false;
            foreach (var row in rows)
            {
                var dataRow = new VisualElement
                {
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        backgroundColor = new StyleColor(alt ? RowAlt : RowBase),
                        paddingLeft = 4, paddingRight = 4, paddingTop = 3, paddingBottom = 3
                    }
                };
                for (int c = 0; c < columns.Length && c < row.Length; c++)
                {
                    dataRow.Add(new Label(row[c] ?? "")
                    {
                        style =
                        {
                            width = new Length(columns[c].WidthFraction * 100f, LengthUnit.Percent),
                            fontSize = 9,
                            color = new StyleColor(TextPrimary),
                            overflow = Overflow.Hidden,
                            whiteSpace = WhiteSpace.NoWrap
                        }
                    });
                }
                container.Add(dataRow);
                alt = !alt;
            }

            return container;
        }
    }
}
