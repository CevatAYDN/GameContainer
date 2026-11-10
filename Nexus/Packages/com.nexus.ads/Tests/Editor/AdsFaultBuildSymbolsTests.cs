using Nexus.Ads.Editor;
using NUnit.Framework;

namespace Nexus.Ads.Tests
{
    public sealed class AdsFaultBuildSymbolsTests
    {
        [Test]
        public void ReleaseRemovesStaleFaultDefine_AndDevelopmentAddsItOnce_PreservingGameSymbols()
        {
            const string previous = "GAME;NEXUS_ADS_FAULT_TESTS;OTHER;NEXUS_ADS_FAULT_TESTS";
            Assert.AreEqual("GAME;OTHER", AdsFaultBuildSymbols.ForBuild(previous, false));
            Assert.AreEqual("GAME;OTHER;NEXUS_ADS_FAULT_TESTS", AdsFaultBuildSymbols.ForBuild(previous, true));
            Assert.AreEqual("", AdsFaultBuildSymbols.ForBuild(null, false));
        }
    }
}
