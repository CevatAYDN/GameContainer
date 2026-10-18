using NUnit.Framework;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Tests
{
    /// <summary>Which Firebase parameter type a value becomes. A value of a type Firebase has no parameter for must be skipped, not sent as a string of its type name.</summary>
    public sealed class AnalyticsParameterValueTests
    {
        [Test]
        public void KindOf_TextAndWholeNumbersAndFractionsKeepTheirType()
        {
            Assert.AreEqual(AnalyticsParameterKind.Text, AnalyticsParameterValue.KindOf("night"));
            Assert.AreEqual(AnalyticsParameterKind.Integer, AnalyticsParameterValue.KindOf(3));
            Assert.AreEqual(AnalyticsParameterKind.Integer, AnalyticsParameterValue.KindOf(3L));
            Assert.AreEqual(AnalyticsParameterKind.Real, AnalyticsParameterValue.KindOf(1.5));
            Assert.AreEqual(AnalyticsParameterKind.Real, AnalyticsParameterValue.KindOf(1.5f));
        }

        [Test]
        public void KindOf_BooleanIsSentAsOneOrZero()
        {
            Assert.AreEqual(AnalyticsParameterKind.Integer, AnalyticsParameterValue.KindOf(true));
            Assert.AreEqual(1L, AnalyticsParameterValue.ToInteger(true));
            Assert.AreEqual(0L, AnalyticsParameterValue.ToInteger(false));
        }

        [Test]
        public void KindOf_NothingElseIsSent()
        {
            Assert.AreEqual(AnalyticsParameterKind.Unsupported, AnalyticsParameterValue.KindOf(null));
            Assert.AreEqual(AnalyticsParameterKind.Unsupported, AnalyticsParameterValue.KindOf(new object()));
            Assert.AreEqual(AnalyticsParameterKind.Unsupported, AnalyticsParameterValue.KindOf(double.NaN));
            Assert.AreEqual(AnalyticsParameterKind.Unsupported, AnalyticsParameterValue.KindOf(float.PositiveInfinity));
        }
    }
}