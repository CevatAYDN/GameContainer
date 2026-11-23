using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Nexus.Editor.Ios
{
    /// <summary>
    /// Runs <see cref="IosShipCheck"/> on an <c>.xcarchive</c> (#436). macOS only (<c>plutil</c>, <c>xcodebuild</c>). Batch entry:
    /// <c>-executeMethod Nexus.Editor.Ios.IosShipCheckCli.Run -iosArchive &lt;path.xcarchive&gt; -iosProject &lt;exported project folder&gt;
    /// [-iosLastBuild &lt;n&gt;] [-iosRequireClass &lt;Class&gt;]...</c>; exit 0 all checks pass, 1 a check failed, 2 could not run.
    /// Not compiled or run when it was written, see the changelog.
    /// </summary>
    public static class IosShipCheckCli
    {
        public static void Run()
        {
            try
            {
                string archive = Argument("-iosArchive"), project = Argument("-iosProject");
                if (string.IsNullOrEmpty(archive) || string.IsNullOrEmpty(project)) throw new InvalidOperationException("-iosArchive and -iosProject are required.");
                var settings = IosKitSettings.Find();
                var input = new IosShipInput { DeclaresTracking = settings != null && settings.declaresTracking };
                input.MustBeFalse.AddRange(new[] { "FIREBASE_ANALYTICS_COLLECTION_ENABLED", "FirebaseCrashlyticsCollectionEnabled" }.Where(KeyIsExpected));
                if (long.TryParse(Argument("-iosLastBuild"), out long last)) input.LastUploadedBuild = last;
                input.RequiredBinaryClasses.AddRange(ArgumentsOf("-iosRequireClass"));

                string apps = Path.Combine(archive, "Products", "Applications");
                string app = Directory.Exists(apps) ? FirstApp(apps) : null;
                if (app == null) throw new FileNotFoundException("No .app in " + apps);
                input.InfoPlistXml = Plutil(Path.Combine(app, "Info.plist"));
                string manifest = Path.Combine(app, "PrivacyInfo.xcprivacy");
                input.PrivacyManifestXml = File.Exists(manifest) ? Plutil(manifest) : null;
                input.UnityFrameworkLdFlags = LdFlags(project);
                string binaryPath = Path.Combine(app, "Frameworks", "UnityFramework.framework", "UnityFramework");
                string binary = File.Exists(binaryPath) ? Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(binaryPath)) : string.Empty;
                input.BinaryContains = text => binary.Contains(text, StringComparison.Ordinal);

                bool failed = false;
                foreach (var check in IosShipCheck.Evaluate(input))
                {
                    Debug.Log("[Nexus iOS ship check] " + (check.Passed ? "PASS " : "FAIL ") + check.Name + (check.Passed ? string.Empty : ": " + check.Detail));
                    failed |= !check.Passed;
                }
                EditorApplication.Exit(failed ? 1 : 0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Nexus iOS ship check] could not run: " + ex.Message);
                EditorApplication.Exit(2);
            }
        }

        // A Firebase key is expected only when the project has that SDK loaded.
        private static bool KeyIsExpected(string key)
        {
            string assembly = key.StartsWith("FIREBASE_ANALYTICS", StringComparison.Ordinal) ? "Firebase.Analytics" : "Firebase.Crashlytics";
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name == assembly) return true;
            }
            return false;
        }

        private static string FirstApp(string apps)
        {
            foreach (string dir in Directory.GetDirectories(apps, "*.app")) return dir;
            return null;
        }

        private static string Plutil(string path)
        {
            var result = Exec("plutil", "-convert xml1 -o - \"" + path + "\"", null);
            if (result.code != 0) throw new InvalidOperationException("plutil failed on " + path + ": " + result.output);
            return result.output;
        }

        private static string LdFlags(string project)
        {
            var result = Exec("xcodebuild", "-project Unity-iPhone.xcodeproj -target UnityFramework -configuration Release -showBuildSettings", project);
            if (result.code != 0) return null;
            foreach (string line in result.output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("OTHER_LDFLAGS", StringComparison.Ordinal)) return trimmed.Substring(trimmed.IndexOf('=') + 1);
            }
            return string.Empty;
        }

        private static (int code, string output) Exec(string file, string args, string cwd)
        {
            var info = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (cwd != null) info.WorkingDirectory = cwd;
            using (var process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, process.ExitCode == 0 ? output : output + error);
            }
        }

        internal static string Argument(string name)
        {
            foreach (string value in ArgumentsOf(name)) return value;
            return null;
        }

        private static IEnumerable<string> ArgumentsOf(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) yield return args[i + 1];
            }
        }
    }
}
