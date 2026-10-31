#if UNITY_ENABLE_CHECKS || UNITY_EDITOR
using Nexus.Core;

namespace NexusVerify.DevOnly
{
    /// <summary>
    /// Release-build fixture for issue #85: public, constructible and injectable, but compiled only in the Editor
    /// and in players whose Managed Code Variant is Checked or Debug (#140: Unity 6000.6 deprecates DEVELOPMENT_BUILD). The binder is generated from the Editor's assemblies, so it names this type;
    /// the release player builds of tools/unity-verify fail with CS0234 unless the binder repeats this condition.
    /// </summary>
    public sealed class DevOnlyProbe
    {
        [Inject] public ISignalBus Signals;

        public DevOnlyProbe() { }
    }
}
#endif
