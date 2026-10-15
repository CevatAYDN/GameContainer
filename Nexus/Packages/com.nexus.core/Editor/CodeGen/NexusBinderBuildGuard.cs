using System;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Nexus.Editor
{
    /// <summary>
    /// Stops a player build whose generated binder or link.xml no longer matches the code. The binder is git-ignored and is
    /// regenerated only on request, so a build from a fresh clone, or after an [Inject] member was added, would otherwise ship
    /// injectors that leave the new members null (the runtime falls back to reflection, but IL2CPP stripping follows the stale
    /// link.xml). Projects without a generated binder use reflection injection and are not checked.
    /// Fix: Nexus > Generate AOT Binder, or run <see cref="NexusCodeGenerator.GenerateBinder"/> headless before the build.
    /// Set NEXUS_SKIP_BINDER_GUARD=1 to build anyway.
    /// </summary>
    public sealed class NexusBinderBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (Environment.GetEnvironmentVariable("NEXUS_SKIP_BINDER_GUARD") == "1") return;
            if (!NexusCodeGenerator.BinderExists()) return;
            if (!NexusCodeGenerator.IsBinderCurrent())
                throw new BuildFailedException("[Nexus] The generated binder or link.xml is out of date. Run Nexus > Generate AOT Binder " +
                    "(headless: -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder) and build again.");
        }
    }
}
