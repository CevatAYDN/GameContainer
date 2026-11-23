#if NEXUS_LEVELPLAY
using NUnit.Framework;
using Nexus.Ads.LevelPlay.Editor;

namespace Nexus.Ads.LevelPlay.Tests
{
    /// <summary>#435: the linker drops LevelPlay's adapters without -ObjC, so the post-process must tell a flag that is there from one that is not.</summary>
    public sealed class LevelPlayIosLinkerFlagsTests
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("-lc++ -framework Foo")]
        [TestCase("-ObjC++")]
        [TestCase("-force_load /x/-ObjC-not-a-flag.a")]
        public void FlagsWithoutObjC_AreReportedMissing_SoThePostProcessAddsIt(string flags)
        {
            Assert.IsFalse(LevelPlayIosLinkerFlags.HasObjC(flags));
        }

        [TestCase("-ObjC")]
        [TestCase("$(inherited) -ObjC -lc++")]
        [TestCase("\"-ObjC\"")]
        [TestCase("-lz\t-ObjC")]
        public void FlagsWithObjC_AreReportedPresent_SoItIsNotAddedTwice(string flags)
        {
            Assert.IsTrue(LevelPlayIosLinkerFlags.HasObjC(flags));
        }
    }
}
#endif
