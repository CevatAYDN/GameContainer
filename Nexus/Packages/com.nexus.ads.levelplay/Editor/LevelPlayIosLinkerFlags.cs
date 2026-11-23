using System;

namespace Nexus.Ads.LevelPlay.Editor
{
    /// <summary>
    /// The linker flag LevelPlay needs on iOS (#435). LevelPlay creates its network adapters by class name
    /// (<c>NSClassFromString("ISUnityAdsAdapter")</c>), so nothing in the binary references them and the linker drops their static
    /// archives unless the target links with <c>-ObjC</c>. CocoaPods added the flag; the Swift packages the External Dependency
    /// Manager 2.1 resolves to do not. Every request then ends in <c>509 Mediation No fill</c>. Kept free of the Xcode API so it is
    /// testable on any build target.
    /// </summary>
    public static class LevelPlayIosLinkerFlags
    {
        /// <summary>The flag that keeps the adapters in the binary.</summary>
        public const string ObjC = "-ObjC";

        /// <summary>True when an <c>OTHER_LDFLAGS</c> value (space separated, entries possibly quoted) already holds <c>-ObjC</c> as a whole entry.</summary>
        public static bool HasObjC(string otherLdFlags)
        {
            if (string.IsNullOrWhiteSpace(otherLdFlags)) return false;
            foreach (string entry in otherLdFlags.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (entry.Trim('"', '\'') == ObjC) return true;
            }
            return false;
        }
    }
}
