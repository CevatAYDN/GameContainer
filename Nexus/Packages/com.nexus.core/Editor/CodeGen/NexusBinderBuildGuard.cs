using System;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Nexus.Editor
{
    /// <summary>
    /// The first step of a player build: it regenerates the binder and link.xml, so a build from a fresh clone, from CI or
    /// after an [Inject] member was added ships injectors and stripping rules that match the code. The binder is git-ignored,
    /// so a build is the one moment it must be current. With the Roslyn source generator on (Mode B), the compiler writes the
    /// binder and only link.xml is generated; an editor binder file left beside it fails the build with a message (#242).
    /// With NEXUS_DISABLE_AUTOGEN=1 nothing is generated, and the build stops while an existing binder or link.xml is out of
    /// date (a project without a binder uses reflection injection and is not stopped); NEXUS_SKIP_BINDER_GUARD=1 builds anyway.
    /// </summary>
    public sealed class NexusBinderBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!IsSet("NEXUS_DISABLE_AUTOGEN"))
            {
                try { NexusCodeGenerator.GenerateBinder(); }
                catch (Exception ex) { throw new BuildFailedException($"[Nexus] AOT Binder generation failed during the pre-build step: {ex.Message}"); }
                return;
            }
            if (IsSet("NEXUS_SKIP_BINDER_GUARD") || !NexusCodeGenerator.BinderExists()) return;
            if (!NexusCodeGenerator.IsBinderCurrent())
                throw new BuildFailedException("[Nexus] The generated binder or link.xml is out of date, and NEXUS_DISABLE_AUTOGEN=1 keeps the build from " +
                    "regenerating it. Run Nexus > Generate AOT Binder (headless: -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder) and build again.");
        }

        /// <summary>True when the environment variable is "1" or "true" (any case), the form every NEXUS_* build switch takes.</summary>
        internal static bool IsSet(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
