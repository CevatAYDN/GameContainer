using NUnit.Framework;
using Nexus.Editor;

namespace Nexus.Tests.Editor
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
        public void L2_GameManagerCountKeys_FormatCorrectly_InEnglish()
        {
            NexusLang.LoadLocale("en");
            
            Assert.AreEqual("5 commands", string.Format(NexusLang.Get("gm_count_commands"), 5));
            Assert.AreEqual("3 models", string.Format(NexusLang.Get("gm_count_models"), 3));
            Assert.AreEqual("2 services", string.Format(NexusLang.Get("gm_count_services"), 2));
            Assert.AreEqual("1 others", string.Format(NexusLang.Get("gm_count_others"), 1));
            Assert.AreEqual("4 handler(s)", string.Format(NexusLang.Get("gm_count_handlers"), 4));
        }

        [Test]
        public void L2_GameManagerCountKeys_FormatCorrectly_InTurkish()
        {
            NexusLang.LoadLocale("tr");

            Assert.AreEqual("5 komut", string.Format(NexusLang.Get("gm_count_commands"), 5));
            Assert.AreEqual("3 model", string.Format(NexusLang.Get("gm_count_models"), 3));
            Assert.AreEqual("2 servis", string.Format(NexusLang.Get("gm_count_services"), 2));
            Assert.AreEqual("1 diğer", string.Format(NexusLang.Get("gm_count_others"), 1));
            Assert.AreEqual("4 işleyici", string.Format(NexusLang.Get("gm_count_handlers"), 4));
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
