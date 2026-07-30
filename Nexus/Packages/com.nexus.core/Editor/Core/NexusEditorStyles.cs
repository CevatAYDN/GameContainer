using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    /// <summary>
    /// Design system constants and helpers for the Nexus Editor.
    /// Palette is deliberately small to ensure visual consistency.
    /// </summary>
    internal static class NexusEditorStyles
    {
        // ─── Simplified Color Palette ───
        // Surfaces
        internal static readonly Color SurfaceDark   = new(0.10f, 0.10f, 0.12f); // Sidebar, toolbar bg
        internal static readonly Color Surface       = new(0.14f, 0.14f, 0.16f); // Main background
        internal static readonly Color SurfaceAlt    = new(0.17f, 0.17f, 0.19f); // Card bg
        internal static readonly Color SurfaceHover  = new(0.21f, 0.21f, 0.23f); // Hover state
        internal static readonly Color SurfaceActive = new(0.23f, 0.26f, 0.31f); // Active/selected
        internal static readonly Color Border        = new(0.20f, 0.20f, 0.22f); // Borders
        internal static readonly Color BorderLight   = new(0.24f, 0.24f, 0.26f); // Subtle borders

        // Text
        internal static readonly Color TextPrimary   = new(0.88f, 0.88f, 0.88f);
        internal static readonly Color TextSecondary = new(0.60f, 0.60f, 0.60f);
        internal static readonly Color TextDim       = new(0.40f, 0.40f, 0.40f);

        // Accents — only 6 core colors for semantic meaning
        internal static readonly Color AccentBlue    = new(0.30f, 0.80f, 1.00f); // Primary info
        internal static readonly Color AccentGreen   = new(0.40f, 1.00f, 0.40f); // Success, live
        internal static readonly Color AccentYellow  = new(1.00f, 0.85f, 0.30f); // Warning
        internal static readonly Color AccentRed     = new(1.00f, 0.30f, 0.30f); // Error
        internal static readonly Color AccentPurple  = new(0.78f, 0.61f, 0.90f); // Signal, handler
        internal static readonly Color AccentOrange  = new(1.00f, 0.70f, 0.28f); // Command

        // Semantic button colors — derive from accent palette
        internal static readonly Color BtnPrimary    = new(0.22f, 0.38f, 0.52f);
        internal static readonly Color BtnSecondary  = new(0.25f, 0.25f, 0.28f);
        internal static readonly Color BtnSuccess    = new(0.20f, 0.40f, 0.20f);
        internal static readonly Color BtnDanger     = new(0.45f, 0.20f, 0.20f);

        // Legacy aliases — kept for backward compatibility
        internal static readonly Color Background    = Surface;
        internal static readonly Color CardBg        = SurfaceAlt;
        internal static readonly Color CardBgAlt     = Surface;
        internal static readonly Color BorderColor   = Border;
        internal static readonly Color DimText       = TextDim;
        internal static readonly Color DarkPanel     = SurfaceDark;
        internal static readonly Color SidebarBg     = SurfaceDark;
        internal static readonly Color ToolbarBg     = SurfaceDark;
        internal static readonly Color HighlightBg   = SurfaceActive;
        internal static readonly Color RowBase       = SurfaceAlt;
        internal static readonly Color RowAlt        = new(0.15f, 0.15f, 0.17f);
        internal static readonly Color TableHeaderBg = new(0.16f, 0.16f, 0.18f);
        internal static readonly Color SelectedRow   = SurfaceActive;
        internal static readonly Color BtnBlue       = BtnPrimary;
        internal static readonly Color BtnPurple     = new(0.30f, 0.22f, 0.40f);
        internal static readonly Color BtnTeal       = new(0.22f, 0.30f, 0.30f);
        internal static readonly Color BtnGray       = BtnSecondary;
        internal static readonly Color BtnGreen      = BtnSuccess;
        internal static readonly Color BtnRed        = BtnDanger;
        internal static readonly Color BtnYellow     = new(0.45f, 0.35f, 0.12f);
        internal static readonly Color AccentBlueText  = new(0.70f, 0.90f, 1.00f);
        internal static readonly Color AccentPurpleText = new(0.90f, 0.70f, 1.00f);
        internal static readonly Color AccentGreenText  = new(0.60f, 1.00f, 0.60f);
        internal static readonly Color CardBgGreen   = new(0.14f, 0.18f, 0.14f);
        internal static readonly Color CardBgYellow  = new(0.20f, 0.18f, 0.14f);
        internal static readonly Color CardBgRed     = new(0.20f, 0.14f, 0.14f);
        internal static readonly Color CardBgBlue    = new(0.14f, 0.16f, 0.20f);
        internal static readonly Color TitleColor    = AccentBlue;
        internal static readonly Color SignalBlue    = AccentBlue;

        internal const float CardRadius = 6f;
        internal const float BtnRadius = 4f;
        internal const float ToolbarPadding = 8f;
        internal const float CardPadding = 12f;

        // ─── USS Class Name Constants ───
        internal const string ClassSidebar = "nexus-sidebar";
        internal const string ClassSidebarBtn = "nexus-sidebar-btn";
        internal const string ClassActiveSidebar = "nexus-sidebar-btn active";
        internal const string ClassSidebarLabel = "nexus-sidebar-label";
        internal const string ClassBrandTitle = "nexus-brand-title";
        internal const string ClassBrandSubtitle = "nexus-brand-subtitle";
        internal const string ClassSidebarSep = "nexus-sidebar-separator";
        internal const string ClassCategoryHeader = "nexus-category-header";
        internal const string ClassCard = "nexus-card";
        internal const string ClassPill = "nexus-pill";
        internal const string ClassPillGreen = "nexus-pill-green";
        internal const string ClassPillBlue = "nexus-pill-blue";
        internal const string ClassPillPurple = "nexus-pill-purple";
        internal const string ClassPillYellow = "nexus-pill-yellow";
        internal const string ClassFilterBtn = "nexus-filter-btn";
        internal const string ClassActionBtn = "nexus-action-btn";
        internal const string ClassToolbar = "nexus-toolbar";
        internal const string ClassToolbarTitle = "nexus-toolbar-title";
        internal const string ClassEmptyState = "nexus-empty-state";
        internal const string ClassSectionTitle = "nexus-section-title";
        internal const string ClassDashboardActionCard = "nexus-dashboard-action-card";
        internal const string ClassStatBox = "nexus-stat-box";
        internal const string ClassStatValue = "nexus-stat-value";
        internal const string ClassStatLabel = "nexus-stat-label";
        internal const string ClassMetricBox = "nexus-metric-box";
        internal const string ClassMetricValue = "nexus-metric-value";
        internal const string ClassMetricLabel = "nexus-metric-label";
        internal const string ClassBtn = "nexus-btn";
        internal const string ClassStatusDot = "nexus-status-dot";
        internal const string ClassStatusBar = "nexus-statusbar";
        internal const string ClassWarningBox = "nexus-warning-box";
        internal const string ClassRow = "nexus-table-row";
        internal const string ClassHeader = "nexus-table-header";
        internal const string ClassBarBg = "nexus-bar-bg";
        internal const string ClassBarFill = "nexus-bar-fill";
        internal const string ClassMetricBtn = "nexus-metric-btn";
        internal const string ClassSearchField = "nexus-search-field";
        internal const string ClassQfRow = "nexus-qf-row";
        internal const string ClassQfBtn = "nexus-qf-btn";
        internal const string ClassSectionTab = "nexus-section-tab";
        internal const string ClassSectionTabText = "nexus-section-tab-text";
        internal const string ClassBreadcrumb = "nexus-breadcrumb";
        internal const string ClassQuickbar = "nexus-quickbar";

        // ─── USS Loading ───
        internal static void LoadTheme(VisualElement root)
        {
            var theme = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.nexus.core/Editor/Styles/NexusTheme.uss");
            if (theme != null)
                root.styleSheets.Add(theme);
        }

        /// <summary>Apply a class list to a VisualElement. Convenience for readability.</summary>
        internal static void AddClasses(VisualElement el, params string[] classes)
        {
            foreach (var c in classes)
                el.AddToClassList(c);
        }

        // ─── Icon Helpers ───
        internal static Texture2D GetIcon(string iconName)
        {
            return EditorGUIUtility.Load($"Editor Default Resources/Icons/{iconName}.png") as Texture2D;
        }

        internal static VisualElement CreateColorIcon(Color color, int size = 16)
        {
            var icon = new VisualElement();
            icon.AddToClassList("nexus-plugin-icon");
            icon.style.width = size;
            icon.style.height = size;
            icon.style.borderTopLeftRadius = size / 2;
            icon.style.borderTopRightRadius = size / 2;
            icon.style.borderBottomLeftRadius = size / 2;
            icon.style.borderBottomRightRadius = size / 2;
            icon.style.backgroundColor = new StyleColor(color);
            return icon;
        }

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
            txt.AddToClassList(ClassSidebarLabel);
            btn.Add(txt);

            return btn;
        }

        /// <summary>Creates a standardized stat tile card.</summary>
        internal static VisualElement CreateStatTile(string label, string value, Color accent, string description = null)
        {
            var card = new VisualElement();
            card.AddToClassList("nexus-stat-tile");
            card.style.borderLeftColor = new StyleColor(accent);

            var valLabel = new Label(value);
            valLabel.AddToClassList("nexus-stat-tile-value");
            valLabel.style.color = new StyleColor(accent);
            card.Add(valLabel);

            var nameLabel = new Label(label);
            nameLabel.AddToClassList("nexus-stat-tile-label");
            card.Add(nameLabel);

            if (!string.IsNullOrEmpty(description))
            {
                var descLabel = new Label(description);
                descLabel.AddToClassList("nexus-stat-tile-desc");
                card.Add(descLabel);
            }

            return card;
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
            var lbl = new Label(text);
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.fontSize = fontSize;
            lbl.style.color = new StyleColor(color);
            lbl.style.marginBottom = 6;
            return lbl;
        }

        internal static Label CreateBody(string text, int fontSize = 11)
        {
            var lbl = new Label(text);
            lbl.style.color = new StyleColor(TextPrimary);
            lbl.style.fontSize = fontSize;
            lbl.style.whiteSpace = WhiteSpace.Normal;
            return lbl;
        }

        internal static Label CreateHint(string text)
        {
            var lbl = new Label(text);
            lbl.style.color = new StyleColor(TextSecondary);
            lbl.style.fontSize = 10;
            lbl.style.whiteSpace = WhiteSpace.Normal;
            return lbl;
        }

        internal static Button CreateButton(string label, System.Action onClick, Color bgColor)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList(ClassBtn);
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
            titleLabel.AddToClassList(ClassToolbarTitle);
            toolbar.Add(titleLabel);

            return toolbar;
        }

        internal static void AddToolbarButton(VisualElement toolbar, string label, System.Action onClick)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList("nexus-toolbar-btn");
            toolbar.Add(btn);
        }

        internal static Label CreateStatusBar()
        {
            var lbl = new Label();
            lbl.AddToClassList(ClassStatusBar);
            return lbl;
        }

        internal static Label CreateEmptyState(string text)
        {
            var lbl = new Label(text);
            lbl.AddToClassList(ClassEmptyState);
            return lbl;
        }

        internal static VisualElement CreatePill(string text, Color bgColor, Color textColor)
        {
            var pill = new Label(text);
            pill.AddToClassList(ClassPill);
            pill.style.backgroundColor = new StyleColor(bgColor);
            pill.style.color = new StyleColor(textColor);
            return pill;
        }

        internal static VisualElement CreateFilterButton(string label, System.Action onClick, Color activeColor)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList(ClassFilterBtn);
            btn.style.backgroundColor = new StyleColor(activeColor);
            return btn;
        }

        internal static VisualElement CreateStatusDot(Color dotColor, int size = 6)
        {
            var dot = new VisualElement();
            dot.AddToClassList(ClassStatusDot);
            dot.style.width = size;
            dot.style.height = size;
            dot.style.borderTopLeftRadius = size / 2;
            dot.style.borderTopRightRadius = size / 2;
            dot.style.borderBottomLeftRadius = size / 2;
            dot.style.borderBottomRightRadius = size / 2;
            dot.style.backgroundColor = new StyleColor(dotColor);
            return dot;
        }

        internal static VisualElement CreateTag(string text, Color bgColor, Color textColor)
        {
            var tag = new Label(text);
            tag.AddToClassList(ClassPill);
            tag.style.backgroundColor = new StyleColor(bgColor);
            tag.style.color = new StyleColor(textColor);
            return tag;
        }

        internal static VisualElement CreateWarningBox(string text)
        {
            var box = new Label(text);
            box.AddToClassList(ClassWarningBox);
            return box;
        }

        internal static VisualElement CreateSectionTitle(string text)
        {
            var label = new Label(text);
            label.AddToClassList(ClassSectionTitle);
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

        // ─── Stat Row ───
        internal static VisualElement CreateStatRow(string key, string value,
            Color valueColor = default, float fontSize = 10f)
        {
            if (valueColor == default) valueColor = TextPrimary;
            var row = new VisualElement();
            row.AddToClassList("nexus-stat-row");
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

        // ─── Live Badge ───
        internal static Label CreateLiveBadge()
        {
            var label = new Label("● LIVE");
            label.AddToClassList("nexus-live-badge");
            return label;
        }

        // ─── Data Table ───
        internal static VisualElement CreateDataTable(
            (string Header, float WidthFraction)[] columns,
            System.Collections.Generic.IEnumerable<string[]> rows,
            float tableWidth = 400f)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Column;

            var header = new VisualElement();
            header.AddToClassList(ClassHeader);
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
                dataRow.AddToClassList(ClassRow);
                if (alt)
                    dataRow.AddToClassList("alt");
                for (int c = 0; c < columns.Length && c < row.Length; c++)
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
    }
}
