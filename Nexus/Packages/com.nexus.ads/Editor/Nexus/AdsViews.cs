using System;
using Nexus.Ads.Unity;
using Nexus.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Ads.Editor
{
    /// <summary>The pieces the Ads tabs share: a card, a switch, a provider badge, the build outlook, a validation report and the Android plan.</summary>
    internal static class AdsViews
    {
        internal static bool Turkish => NexusLang.CurrentLocale == "tr";

        internal static VisualElement Card(VisualElement parent, string title)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 10;
            card.Add(NexusEditorStyles.CreateTitle(title, NexusEditorStyles.AccentBlue));
            parent.Add(card);
            return card;
        }

        internal static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            parent.Add(row);
            return row;
        }

        /// <summary>One button per choice, the selected one highlighted; a click selects it and calls <paramref name="select"/>.</summary>
        internal static VisualElement Switch(string[] choices, int selected, Action<int> select)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4, marginBottom = 4 } };
            for (int i = 0; i < choices.Length; i++)
            {
                int index = i;
                row.Add(NexusEditorStyles.CreateButton(choices[i], () => select(index), i == selected ? NexusEditorStyles.BtnBlue : NexusEditorStyles.BtnGray));
            }
            return row;
        }

        internal static Label Badge(string text, ProviderStatus status)
        {
            var color = status == ProviderStatus.Active ? NexusEditorStyles.AccentGreen
                : status == ProviderStatus.Invalid ? NexusEditorStyles.AccentRed
                : status == ProviderStatus.TestOnly ? NexusEditorStyles.AccentOrange
                : NexusEditorStyles.TextDim;
            var label = NexusEditorStyles.CreateBody(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new StyleColor(color);
            label.style.marginTop = 2;
            return label;
        }

        internal static ProviderStatusReport SelectedStatus(AdsSettings settings, AdPlatform platform, UnityProjectProbe probe)
        {
            bool sdk = probe.SdkInstalled(settings.SelectedMediation) && probe.ProviderPackageInstalled(settings.SelectedMediation);
            return ProviderStatusResolver.Resolve(settings, settings.SelectedMediation, platform, sdk, probe.SdkInstalled(AdProviderKind.LevelPlay) && probe.SdkInstalled(AdProviderKind.Max));
        }

        /// <summary>What each kind of build does with ads, as <see cref="BuildOutlook"/> decides it; the tabs show this and nothing else of the kind.</summary>
        internal static VisualElement Outlook(AdsSettings settings, AdPlatform platform, UnityProjectProbe probe)
        {
            var box = new VisualElement { style = { marginTop = 6 } };
            box.Add(NexusEditorStyles.CreateTitle(string.Format(NexusLang.Get("ads_outlook_title"), platform), NexusEditorStyles.TextSecondary));
            bool sdk = probe.SdkInstalled(settings.SelectedMediation) && probe.ProviderPackageInstalled(settings.SelectedMediation);
            bool googleConsent = probe.SdkInstalled(AdProviderKind.AdMob) && probe.ProviderPackageInstalled(AdProviderKind.AdMob);
            foreach (var row in BuildOutlook.Rows(settings, platform, sdk, SelectedStatus(settings, platform, probe), googleConsent))
                box.Add(new HelpBox(row.Where + ": " + row.Text, row.Problem ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info));
            box.Add(NexusEditorStyles.CreateHint(NexusLang.Get("ads_outlook_hint")));
            return box;
        }

        /// <summary>A validation result: the counts, then each failing or warning check with its fix.</summary>
        internal static void ShowReport(VisualElement target, AdsValidationReport report)
        {
            target.Clear();
            if (report == null) return;
            target.Add(new HelpBox(report.tab + " tab, " + report.platform + ", " + report.mediation + ": " + report.pass + " pass, " + report.warn + " warn, " + report.fail + " fail",
                report.fail > 0 ? HelpBoxMessageType.Error : report.warn > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info));
            foreach (var check in report.checks)
                if (check.Result != CheckResult.Pass)
                    target.Add(new HelpBox(check.message + (check.fix.Length > 0 ? "\nFix: " + check.fix : "") + "\n(" + check.id + ")",
                        check.Result == CheckResult.Fail ? HelpBoxMessageType.Error : HelpBoxMessageType.Warning));
        }

        /// <summary>
        /// The selected mediation's Android setup for a tab (<see cref="AndroidSetup.Plan"/>) and the Prepare Android button.
        /// The plan reads project and package files, so it is built when the view is and again after Prepare Android or <see cref="Rebuild"/>.
        /// </summary>
        internal static VisualElement AndroidPlan(AdsSettings settings, AdsValidator.Tab tab, IProjectProbe probe, PlatformCatalog catalog, Action refresh)
        {
            var box = new VisualElement();
            Rebuild(box, settings, tab, probe, catalog, refresh);
            return box;
        }

        internal static void Rebuild(VisualElement box, AdsSettings settings, AdsValidator.Tab tab, IProjectProbe probe, PlatformCatalog catalog, Action refresh)
        {
            box.Clear();
            var plan = AndroidSetup.Plan(settings, tab, probe, catalog);
            if (plan.Count == 0) box.Add(NexusEditorStyles.CreateHint(NexusLang.Get("ads_android_nothing")));
            foreach (var step in plan)
            {
                string status = NexusLang.Get(step.status == SetupStatus.Done ? "ads_step_done" : step.status == SetupStatus.Pending ? "ads_step_pending" : "ads_step_manual");
                var line = NexusEditorStyles.CreateBody("[" + status + "] " + step.what);
                if (step.status == SetupStatus.Pending) line.style.color = new StyleColor(NexusEditorStyles.AccentYellow);
                box.Add(line);
                if (!string.IsNullOrEmpty(step.how)) box.Add(NexusEditorStyles.CreateHint(step.how));
            }
            var prepare = NexusEditorStyles.CreateButton(NexusLang.Get("ads_prepare_android"), () =>
            {
                AdsProjectActions.PrepareAndroid(settings, tab, catalog);
                refresh();
            }, NexusEditorStyles.BtnBlue);
            prepare.style.alignSelf = Align.FlexStart;
            prepare.style.marginTop = 4;
            prepare.SetEnabled(plan.Exists(s => s.status == SetupStatus.Pending));
            box.Add(prepare);
        }
    }
}
