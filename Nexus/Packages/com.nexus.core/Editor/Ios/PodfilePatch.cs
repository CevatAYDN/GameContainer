using System;

namespace Nexus.Editor.Ios
{
    /// <summary>
    /// Keeps every pod at the game's minimum iOS (#436): several pods (ironSource adapters, Firebase) still declare a deployment
    /// target older than the game's, which Wickling's Xcode 15+ build refused. Pure text function over the exported Podfile.
    /// </summary>
    public static class PodfilePatch
    {
        public const string Marker = "# NEXUS_PODS_MINIMUM_IOS";
        private const string PostInstallOpen = "post_install do |installer|";

        /// <summary>Returns the Podfile with the minimum applied (once; the marker makes a second call a no-op) and whether it changed.</summary>
        public static string EnsureMinimumIos(string podfile, string minimumIos, out bool changed)
        {
            if (string.IsNullOrWhiteSpace(minimumIos) || !System.Text.RegularExpressions.Regex.IsMatch(minimumIos, @"^\d+(\.\d+){0,2}$"))
                throw new ArgumentException("minimumIos must look like 15.0, got '" + minimumIos + "'.", nameof(minimumIos));
            if (podfile.Contains(Marker)) { changed = false; return podfile; }

            string newline = podfile.Contains("\r\n") ? "\r\n" : "\n";
            string body = Marker + ": every pod builds for the game's minimum iOS" + newline
                + "  installer.pods_project.targets.each do |t|" + newline
                + "    t.build_configurations.each { |c| c.build_settings['IPHONEOS_DEPLOYMENT_TARGET'] = '" + minimumIos + "' }" + newline
                + "  end" + newline;
            changed = true;
            int open = podfile.IndexOf(PostInstallOpen, StringComparison.Ordinal);
            if (open >= 0)
            {
                // The game already has a post_install block: the loop goes first inside it.
                int afterOpen = open + PostInstallOpen.Length;
                return podfile.Insert(afterOpen, newline + "  " + body.TrimEnd('\r', '\n'));
            }

            string prefix = podfile.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : newline;
            return podfile + prefix + newline + Marker + ": every pod builds for the game's minimum iOS" + newline + PostInstallOpen + newline
                + body.Substring(body.IndexOf("  installer", StringComparison.Ordinal)) + "end" + newline;
        }
    }
}
