using NUnit.Framework;
using Nexus.Editor;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class LocalizationValidationTests
    {
        [SetUp]
        public void SetUp()
        {
            NexusLang.LoadLocale("en");
        }

        [Test]
        public void L3_TracerStatusKeys_ReturnLocalizedValues()
        {
            NexusLang.LoadLocale("en");
            Assert.AreEqual("OK", NexusLang.Get("tracer_status_ok"));
            Assert.AreEqual("FAIL", NexusLang.Get("tracer_status_fail"));
            Assert.AreEqual("CANCEL", NexusLang.Get("tracer_status_cancel"));

            NexusLang.LoadLocale("tr");
            Assert.AreEqual("BAŞARILI", NexusLang.Get("tracer_status_ok"));
            Assert.AreEqual("HATA", NexusLang.Get("tracer_status_fail"));
            Assert.AreEqual("İPTAL", NexusLang.Get("tracer_status_cancel"));
        }

        [TearDown]
        public void TearDown()
        {
            NexusLang.LoadLocale("en");
        }
    }
}
