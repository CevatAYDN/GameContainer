using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Reusable UI-building helpers extracted from <see cref="DashboardPlugin"/>.
    /// Each method returns the VisualElement tree and any updatable labels so the
    /// plugin can store references and refresh them without reaching into component internals.
    /// </summary>
    internal static class DashboardSections
    {
        // ─── Stat Box ────────────────────────────────────────────
        internal static Label CreateStatBox(VisualElement parent, string value, string label, Color accentColor)
        {
            var box = new VisualElement();
            box.AddToClassList(NexusEditorStyles.ClassStatBox);

            var valLabel = new Label(value);
            valLabel.AddToClassList(NexusEditorStyles.ClassStatValue);
            valLabel.style.color = new StyleColor(accentColor);
            box.Add(valLabel);

            var descLabel = new Label(label);
            descLabel.AddToClassList(NexusEditorStyles.ClassStatLabel);
            box.Add(descLabel);

            parent.Add(box);
            return valLabel;
        }

        // ─── Metric Jump Button ─────────────────────────────────
        internal static Button CreateMetricJumpButton(string label, string tooltip, Color accentColor, Action onClick)
        {
            var button = new Button(onClick) { text = label, tooltip = tooltip };
            button.AddToClassList(NexusEditorStyles.ClassMetricBtn);
            button.style.backgroundColor = new StyleColor(new Color(accentColor.r, accentColor.g, accentColor.b, 0.18f));
            return button;
        }

        // ─── Section Title ──────────────────────────────────────
        internal static Label CreateSectionTitle(string titleText, Color accentColor)
        {
            var lbl = new Label(titleText);
            lbl.style.fontSize = 11;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.color = new StyleColor(accentColor);
            lbl.style.marginBottom = 8;
            return lbl;
        }

        // ─── Action Card ────────────────────────────────────────
        internal static void AddActionCard(VisualElement parent, string title, string description, Color btnColor, Action onClick)
        {
            var card = new VisualElement();
            card.AddToClassList(NexusEditorStyles.ClassDashboardActionCard);

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("nexus-action-card-title");
            card.Add(titleLabel);

            var descLabel = new Label(description);
            descLabel.AddToClassList("nexus-action-card-desc");
            card.Add(descLabel);

            var btn = NexusEditorStyles.CreateButton(NexusLang.Get("open"), onClick, btnColor);
            btn.style.marginTop = 0;
            btn.style.marginBottom = 0;
            btn.style.alignSelf = Align.FlexStart;
            card.Add(btn);

            parent.Add(card);
        }

        // ─── Health text builder ────────────────────────────────
        internal static string BuildHealthText()
        {
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();

            string readiness = Application.isPlaying ? NexusLang.Get("db_play_mode") : NexusLang.Get("db_edit_mode");

            string healthText;
            if (!Application.isPlaying && rootCount == 0)
                healthText = NexusLang.Get("db_health_no_root");
            else if (Application.isPlaying && contextCount == 0)
                healthText = NexusLang.Get("db_health_no_context");
            else
                healthText = string.Format(NexusLang.Get("db_health_counts"), contextCount, handlerCount, rootCount);

            return string.Format(NexusLang.Get("db_health_line"), readiness, healthText);
        }

        /// <summary>Builds a status hint string for the given runtime state.</summary>
        internal static string GetStatusHintText(bool playing, int rootCount, int contextCount)
        {
            if (!playing && rootCount == 0) return NexusLang.Get("no_roots");
            if (!playing) return NexusLang.Get("ready");
            return string.Format(NexusLang.Get("live_hint"), contextCount);
        }
    }
}
