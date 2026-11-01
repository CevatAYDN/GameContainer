using System.Collections.Generic;
using System.Linq;
using Nexus.Core;
using UnityEditor;
using UnityEngine;

namespace Nexus.Editor
{
    // The core's checks for the Status tab (#193). Each is an INexusStatusSource, as a package's own check is.

    /// <summary>Architecture validation, the check a player build runs (<see cref="BuildValidation"/>).</summary>
    internal sealed class ValidationStatusSource : INexusStatusSource
    {
        public string Name => NexusLang.Get("status_src_validation");

        public IEnumerable<NexusStatusProblem> Collect()
        {
            BuildValidation.RunSilent();
            foreach (var entry in BuildValidation.LastResults)
            {
                string script = BuildValidation.FindScriptNamedIn(entry.Message);
                yield return new NexusStatusProblem
                {
                    IsError = entry.IsError,
                    Message = entry.Message,
                    FixLabel = script != null ? NexusLang.Get("status_fix_open") : null,
                    Fix = script != null ? () => StatusSourceActions.OpenScript(script, 0) : null,
                };
            }
        }
    }

    /// <summary>The <see cref="CodeHealthRules"/> over the scripts the game can change.</summary>
    internal sealed class CodeHealthStatusSource : INexusStatusSource
    {
        public string Name => NexusLang.Get("status_src_code_health");

        public IEnumerable<NexusStatusProblem> Collect()
        {
            foreach (var issue in CodeHealthRules.Scan())
                yield return new NexusStatusProblem
                {
                    IsError = issue.IsError,
                    Message = $"[{issue.Code}] {issue.Message} {issue.FilePath}:{issue.LineNumber}. {issue.Recommendation}",
                    FixLabel = NexusLang.Get("status_fix_open"),
                    Fix = () => StatusSourceActions.OpenScript(issue.FilePath, issue.LineNumber),
                };
        }
    }

    /// <summary>
    /// A binder or link.xml that no longer matches the code, and injectors the container dropped as written for other members.
    /// A player build regenerates both, so they are warnings, unless NEXUS_DISABLE_AUTOGEN=1 makes the build stop on them.
    /// </summary>
    internal sealed class BinderStatusSource : INexusStatusSource
    {
        public string Name => NexusLang.Get("status_src_binder");

        public IEnumerable<NexusStatusProblem> Collect()
        {
            bool buildStops = NexusBinderBuildGuard.IsSet("NEXUS_DISABLE_AUTOGEN");
            if (NexusCodeGenerator.BinderExists() && !NexusCodeGenerator.IsBinderCurrent())
                yield return Regenerate(buildStops, NexusLang.Get(buildStops ? "status_binder_stale_build_stops" : "status_binder_stale"));
            var dropped = NexusDI.GetStaleGeneratedInjectorTypes();
            if (dropped.Length > 0)
                yield return Regenerate(false, string.Format(NexusLang.Get("status_binder_dropped_injectors"), string.Join(", ", dropped.Select(t => t.FullName))));
        }

        private static NexusStatusProblem Regenerate(bool isError, string message) => new NexusStatusProblem
        {
            IsError = isError,
            Message = message,
            FixLabel = NexusLang.Get("status_fix_generate"),
            Fix = NexusCodeGenerator.GenerateBinder,
        };
    }

    /// <summary>A Nexus package whose optional code is compiled out although the package it needs is installed (<see cref="VersionDefineGuard"/>).</summary>
    internal sealed class VersionDefineStatusSource : INexusStatusSource
    {
        public string Name => NexusLang.Get("status_src_package_versions");

        public IEnumerable<NexusStatusProblem> Collect()
            => VersionDefineGuard.FindInProject().Select(finding => new NexusStatusProblem
            {
                IsError = true,
                Message = finding + " " + VersionDefineGuard.Fix,
                FixLabel = NexusLang.Get("status_fix_package_manager"),
                Fix = () => UnityEditor.PackageManager.UI.Window.Open(string.Empty),
            });
    }

    /// <summary>
    /// The errors and warnings recorded at run time; the Errors tab lists them. <see cref="ErrorCollection"/> also records
    /// every ordinary log line, as Info, which is not a problem.
    /// </summary>
    internal sealed class RuntimeErrorStatusSource : INexusStatusSource
    {
        public string Name => NexusLang.Get("status_src_runtime");

        public IEnumerable<NexusStatusProblem> Collect()
        {
            var counts = ErrorCollection.GetSeverityCounts();
            int errors = Count(counts, ErrorCollection.ErrorSeverity.Error) + Count(counts, ErrorCollection.ErrorSeverity.Critical);
            int warnings = Count(counts, ErrorCollection.ErrorSeverity.Warning);
            if (errors > 0)
                yield return Problem(true, "status_runtime_errors", errors);
            if (warnings > 0)
                yield return Problem(false, "status_runtime_warnings", warnings);
        }

        private static int Count(Dictionary<ErrorCollection.ErrorSeverity, int> counts, ErrorCollection.ErrorSeverity severity)
            => counts.TryGetValue(severity, out int count) ? count : 0;

        private static NexusStatusProblem Problem(bool isError, string key, int count)
        {
            var newest = ErrorCollection.GetRecentErrors(int.MaxValue).FirstOrDefault(e => isError
                ? e.Severity >= ErrorCollection.ErrorSeverity.Error
                : e.Severity == ErrorCollection.ErrorSeverity.Warning);
            return new NexusStatusProblem
            {
                IsError = isError,
                Message = string.Format(NexusLang.Get(key), count, newest?.Message),
                FixLabel = NexusLang.Get("status_fix_errors"),
                Fix = () => NexusWindow.Open("ErrorDashboard"),
            };
        }
    }

    /// <summary>In Play Mode, the bindings of each running context that cannot resolve (<see cref="NexusDI.ValidateBindings"/>).</summary>
    internal sealed class DiBindingStatusSource : INexusStatusSource
    {
        public string Name => NexusLang.Get("status_src_di");

        public IEnumerable<NexusStatusProblem> Collect()
        {
            if (!Application.isPlaying) yield break;
            foreach (var context in NexusRuntime.ActiveContexts.ToArray())
            {
                if (context?.Container == null) continue;
                string label = string.IsNullOrEmpty(context.ScopeTag) ? context.GetType().Name : context.ScopeTag;
                foreach (var issue in context.Container.ValidateBindings())
                    yield return new NexusStatusProblem
                    {
                        IsError = true,
                        Message = label + ": " + issue.Message,
                        FixLabel = NexusLang.Get("status_fix_inspect"),
                        Fix = () => NexusWindow.Open("ContextInspector"),
                    };
            }
        }
    }

    internal static class StatusSourceActions
    {
        internal static void OpenScript(string path, int line)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (script != null) AssetDatabase.OpenAsset(script, line);
        }
    }
}
