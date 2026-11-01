using System.Linq;
using Nexus.Ads.Editor;
using NUnit.Framework;

namespace Nexus.Ads.Tests
{
    public sealed class AdsStatusSourceTests
    {
        [Test]
        public void AFailedCheckIsAnError_AWarnedOneAWarning_EachWithItsFix_APassedOneIsNotListed()
        {
            var report = new AdsValidationReport { tab = "Release" };
            report.Add(new AdsCheck("consent.form", CheckResult.Fail, "no consent flow", "set Consent to GoogleUmp"));
            report.Add(new AdsCheck("android.linkxml", CheckResult.Warn, "no link.xml"));
            report.Add(new AdsCheck("mediation.single", CheckResult.Pass, "one mediation"));

            var problems = AdsStatusSource.Problems(report);

            Assert.AreEqual(2, problems.Count);
            Assert.IsTrue(problems[0].IsError);
            StringAssert.Contains("consent.form", problems[0].Message);
            StringAssert.Contains("set Consent to GoogleUmp", problems[0].Message);
            Assert.IsFalse(problems[1].IsError);
            Assert.IsTrue(problems.All(p => p.Fix != null && !string.IsNullOrEmpty(p.FixLabel)), "each opens the ads setup");
        }
    }
}
