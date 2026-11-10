using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Nexus.Ads.Editor
{
    /// <summary>#420: compile-time fault API inclusion follows build options, not Unity's retired development symbol.</summary>
    public sealed class AdsFaultBuildSymbols : BuildPlayerProcessor, IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public const string Symbol = "NEXUS_ADS_FAULT_TESTS";
        public override int callbackOrder => -1100;
        private string _previous;
        private NamedBuildTarget _target;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            var options = context.BuildPlayerOptions;
            if ((options.options & BuildOptions.Development) == 0
                && Array.IndexOf(options.extraScriptingDefines ?? Array.Empty<string>(), Symbol) >= 0)
                throw new BuildFailedException("NEXUS_ADS_FAULT_TESTS cannot be forced into release extraScriptingDefines.");
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            _target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(report.summary.platform));
            _previous = PlayerSettings.GetScriptingDefineSymbols(_target);
            PlayerSettings.SetScriptingDefineSymbols(_target,
                ForBuild(_previous, (report.summary.options & BuildOptions.Development) != 0));
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (_previous != null) PlayerSettings.SetScriptingDefineSymbols(_target, _previous);
        }

        /// <summary>Also used by script-only build verification. Other game defines are retained.</summary>
        public static string ForBuild(string symbols, bool development)
        {
            var result = new List<string>();
            foreach (string value in (symbols ?? "").Split(';'))
                if (!string.IsNullOrWhiteSpace(value) && value.Trim() != Symbol) result.Add(value.Trim());
            if (development) result.Add(Symbol);
            return string.Join(";", result);
        }
    }
}
