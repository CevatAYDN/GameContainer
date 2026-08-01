// Coverage report: which real Nexus runtime files the harness compiles (from the
// csproj Compile Includes) vs. every .cs file in the runtime package. Combined with
// --json, each gap lands in the JSON document as a failed entry under the "Coverage"
// suite, so a "ready" claim is measurable on every run instead of being a vibe.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NexusBench
{
    public static class CoverageReport
    {
        private const string PackageRelPrefix = "Nexus/Packages/com.nexus.core";

        public static int Run(bool json)
        {
            string projectDir = FindProjectDir();
            string repoRoot = Path.GetFullPath(Path.Combine(projectDir, "..", ".."));
            string runtimeDir = Path.Combine(repoRoot, "Nexus", "Packages", "com.nexus.core", "Runtime");

            var compiled = new HashSet<string>();
            foreach (var pattern in ReadCompilePatterns(projectDir))
                ExpandPattern(repoRoot, pattern, compiled);

            var all = new List<string>();
            foreach (var file in Directory.GetFiles(runtimeDir, "*.cs", SearchOption.AllDirectories))
                all.Add(Path.GetRelativePath(repoRoot, file).Replace('\\', '/'));

            var outOfScope = new List<string>();
            foreach (var file in all)
                if (!compiled.Contains(file)) outOfScope.Add(file);
            outOfScope.Sort(StringComparer.Ordinal);

            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine($"[Coverage] runtime files={all.Count}, compiled by harness={compiled.Count}, out of scope={outOfScope.Count}");
            Console.WriteLine("===============================================================================");
            foreach (var file in outOfScope)
                Console.WriteLine($"[Coverage]   OUT  {file}");
            Console.WriteLine("===============================================================================");

            ResultSink.Capture("Coverage", "CompiledFiles", true,
                $"{compiled.Count}/{all.Count} runtime files compiled by the harness ({outOfScope.Count} out of scope)");
            foreach (var file in outOfScope)
                ResultSink.Capture("Coverage", "NotCompiled_" + file, false,
                    "Unity-coupled or excluded; not exercised by this harness");
            return 0;
        }

        private static string FindProjectDir()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "NexusBenchmark.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            return Directory.GetCurrentDirectory();
        }

        private static List<string> ReadCompilePatterns(string projectDir)
        {
            var patterns = new List<string>();
            foreach (var line in File.ReadAllLines(Path.Combine(projectDir, "NexusBenchmark.csproj")))
            {
                var m = Regex.Match(line, @"Include=""\.\./\.\./Nexus/Packages/com\.nexus\.core/(Runtime/[^""]+)""");
                if (m.Success) patterns.Add(m.Groups[1].Value.Replace('\\', '/'));
            }
            return patterns;
        }

        private static void ExpandPattern(string repoRoot, string pattern, HashSet<string> into)
        {
            string full = PackageRelPrefix + "/" + pattern;
            if (!pattern.Contains('*'))
            {
                into.Add(full);
                return;
            }

            string dirPart = pattern.Substring(0, pattern.IndexOf('*'));
            int slash = dirPart.LastIndexOf('/');
            string fixedDir = slash >= 0 ? dirPart.Substring(0, slash) : "";
            string searchRoot = Path.Combine(repoRoot, PackageRelPrefix.Replace('/', Path.DirectorySeparatorChar),
                fixedDir.Replace('/', Path.DirectorySeparatorChar));

            var regex = PatternToRegex(full);
            foreach (var file in Directory.GetFiles(searchRoot, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (regex.IsMatch(rel)) into.Add(rel);
            }
        }

        /// <summary>MSBuild glob -> regex: `**/` matches zero or more directories (slash included).</summary>
        private static Regex PatternToRegex(string pattern)
        {
            string esc = Regex.Escape(pattern);
            esc = esc.Replace(@"\*\*/", "(.*/)?");
            esc = esc.Replace(@"\*\*", ".*");
            esc = esc.Replace(@"\*", "[^/]*");
            return new Regex("^" + esc + "$", RegexOptions.Compiled);
        }
    }
}
