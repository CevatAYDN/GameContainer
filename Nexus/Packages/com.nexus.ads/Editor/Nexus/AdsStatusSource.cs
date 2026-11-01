using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nexus.Ads.Unity;
using Nexus.Editor;
using UnityEditor;

[assembly: InternalsVisibleTo("com.nexus.ads.editor.nexus.tests")]

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// The ads validator in the Nexus window's Status list (#193), compiled only with <c>com.nexus.core</c>: the check the next
    /// player build runs (<see cref="AdsBuildGuard"/>), on the active build target, the Test tab for a development build and
    /// the Release tab otherwise. A project without the settings asset, or on a target other than Android and iOS, is left
    /// alone, as the build guard leaves it.
    /// </summary>
    internal sealed class AdsStatusSource : INexusStatusSource
    {
        public string Name => NexusLang.Get("status_src_ads");

        public IEnumerable<NexusStatusProblem> Collect()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.Android && target != BuildTarget.iOS) return new NexusStatusProblem[0];
            var settings = AdsSettings.Load();
            if (settings == null) return new NexusStatusProblem[0];
            var tab = EditorUserBuildSettings.development ? AdsValidator.Tab.Test : AdsValidator.Tab.Release;
            var platform = target == BuildTarget.iOS ? AdPlatform.Ios : AdPlatform.Android;
            return Problems(AdsValidator.Validate(settings, tab, platform, new UnityProjectProbe(), PlatformCatalog.Load()));
        }

        /// <summary>A failed check is an error and a warned one a warning, each with its fix and the Ads section's Setup tab.</summary>
        internal static List<NexusStatusProblem> Problems(AdsValidationReport report)
        {
            var problems = new List<NexusStatusProblem>();
            foreach (var check in report.checks)
            {
                if (check.Result == CheckResult.Pass) continue;
                problems.Add(new NexusStatusProblem
                {
                    IsError = check.Result == CheckResult.Fail,
                    Message = report.tab + " tab, " + check.id + ": " + check.message + (string.IsNullOrEmpty(check.fix) ? "" : " Fix: " + check.fix),
                    FixLabel = NexusLang.Get("status_fix_ads_setup"),
                    Fix = () => NexusWindow.Open("AdsSetup"),
                });
            }
            return problems;
        }
    }
}
