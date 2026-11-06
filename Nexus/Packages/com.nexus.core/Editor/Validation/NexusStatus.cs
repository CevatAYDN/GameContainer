using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nexus.Editor
{
    /// <summary>One problem the Status tab lists: what is wrong and, when there is one, the action that fixes it.</summary>
    public sealed class NexusStatusProblem
    {
        /// <summary>The <see cref="INexusStatusSource.Name"/> of the check that found it; <see cref="NexusStatus.Collect"/> sets it.</summary>
        public string Source;
        public bool IsError;
        public string Message;
        /// <summary>The fix button's label; null when the problem has no fix action.</summary>
        public string FixLabel;
        public Action Fix;
    }

    /// <summary>
    /// A check whose problems the Status tab and <see cref="NexusStatus.RunCli"/> list. Every non-abstract implementation with
    /// a public parameterless constructor, in any editor assembly, runs on each check; one in a test assembly only when the caller
    /// asks for test assemblies. An implementation without such a constructor is not run and is listed as a warning naming it.
    /// </summary>
    public interface INexusStatusSource
    {
        string Name { get; }
        IEnumerable<NexusStatusProblem> Collect();
    }

    /// <summary>Runs every <see cref="INexusStatusSource"/> (#193).</summary>
    public static class NexusStatus
    {
        /// <summary>
        /// The problems of every source, in source type-name order. A source that throws is listed as an error under its name:
        /// a check that could not run has not checked anything.
        /// </summary>
        public static List<NexusStatusProblem> Collect(bool includeTestAssemblies = false)
        {
            var problems = new List<NexusStatusProblem>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<INexusStatusSource>().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!includeTestAssemblies && AssemblyCatalog.IsTestAssembly(AssemblyCatalog.GetSimpleName(type.Assembly))) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    // the check it describes never runs; a silent skip left the project believing it was checked (#315)
                    problems.Add(new NexusStatusProblem { Source = type.Name, IsError = false, Message = string.Format(NexusLang.Get("status_source_skipped"), type.FullName) });
                    continue;
                }
                string name = type.Name;
                try
                {
                    var source = (INexusStatusSource)Activator.CreateInstance(type);
                    name = source.Name;
                    foreach (var problem in source.Collect() ?? Enumerable.Empty<NexusStatusProblem>())
                    {
                        if (problem == null) continue;
                        problem.Source = name;
                        problems.Add(problem);
                    }
                }
                catch (Exception ex)
                {
                    problems.Add(new NexusStatusProblem
                    {
                        Source = name,
                        IsError = true,
                        Message = string.Format(NexusLang.Get("status_source_failed"), ex.GetType().Name, ex.Message),
                    });
                }
            }
            return problems;
        }

        /// <summary>
        /// Batch entry point: runs every check, logs each problem, and exits 0 when none is an error, 1 when one is, 2 when the
        /// checks could not run.
        /// <c>Unity.exe -batchmode -nographics -quit -projectPath &lt;project&gt; -executeMethod Nexus.Editor.NexusStatus.RunCli</c>
        /// </summary>
        public static void RunCli()
        {
            int exitCode;
            try
            {
                var problems = Collect();
                foreach (var problem in problems)
                {
                    string line = $"[Nexus Status] {problem.Source}: {problem.Message}";
                    if (problem.IsError) Debug.LogError(line);
                    else Debug.LogWarning(line);
                }
                int errors = problems.Count(p => p.IsError);
                Debug.Log($"[Nexus Status] {errors} error(s), {problems.Count - errors} warning(s).");
                exitCode = errors > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Nexus Status] The checks could not run: " + ex);
                exitCode = 2;
            }
            EditorApplication.Exit(exitCode);
        }
    }
}
