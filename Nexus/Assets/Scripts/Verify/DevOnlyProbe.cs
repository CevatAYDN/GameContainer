#if DEVELOPMENT_BUILD || UNITY_EDITOR
using Nexus.Core;

namespace NexusVerify.DevOnly
{
    /// <summary>
    /// Release-build fixture for issue #85: public, constructible and injectable, but compiled only in the Editor
    /// and in development players. The binder is generated from the Editor's assemblies, so it names this type;
    /// the release player builds of tools/unity-verify fail with CS0234 unless the binder repeats this condition.
    /// </summary>
    public sealed class DevOnlyProbe
    {
        [Inject] public ISignalBus Signals;

        public DevOnlyProbe() { }
    }
}
#endif
