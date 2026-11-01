using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Nexus.Editor;

namespace Nexus.Editor.Tests
{
    // Locks the Status tab's contract (#193): the checks of every editor assembly are listed under their names, a check that
    // throws is an error because it checked nothing, and a stale binder is listed with the fix that regenerates it.
    [TestFixture]
    public class StatusTests
    {
        public sealed class ReportingSource : INexusStatusSource
        {
            public static bool Enabled;
            public string Name => "Reporting check";

            public IEnumerable<NexusStatusProblem> Collect()
            {
                if (Enabled) yield return new NexusStatusProblem { IsError = false, Message = "probe warning" };
            }
        }

        public sealed class ThrowingSource : INexusStatusSource
        {
            public string Name => "Throwing check";

            public IEnumerable<NexusStatusProblem> Collect()
            {
                if (ReportingSource.Enabled) throw new InvalidOperationException("check crashed on purpose");
                yield break;
            }
        }

        [Test]
        public void EveryCheck_IsListedUnderItsName_AndACheckThatThrows_IsAnErrorThatSaysWhy()
        {
            ReportingSource.Enabled = true;
            try
            {
                var problems = NexusStatus.Collect(includeTestAssemblies: true);

                var reported = problems.Single(p => p.Source == "Reporting check");
                Assert.IsFalse(reported.IsError);
                Assert.AreEqual("probe warning", reported.Message);
                var crashed = problems.Single(p => p.Source == "Throwing check");
                Assert.IsTrue(crashed.IsError, "a check that could not run has not checked anything");
                StringAssert.Contains("check crashed on purpose", crashed.Message);

                Assert.IsFalse(NexusStatus.Collect().Any(p => p.Source == "Reporting check" || p.Source == "Throwing check"),
                    "a test assembly's checks run only when the caller asks for them");
            }
            finally { ReportingSource.Enabled = false; }
        }

        [Test]
        public void AStaleBinder_IsAWarningWhileTheBuildRegeneratesIt_AnErrorWhenTheBuildStops_AndItsFixRegeneratesIt()
        {
            var settings = NexusEditorSettings.GetOrCreateSettings();
            string binderPath = Path.Combine(settings.BinderOutputPath, "NexusGeneratedBinder.g.cs");
            string linkXmlPath = Path.Combine(settings.LinkXmlOutputPath, "link.xml");
            string binderBefore = File.Exists(binderPath) ? File.ReadAllText(binderPath) : null;
            string linkXmlBefore = File.Exists(linkXmlPath) ? File.ReadAllText(linkXmlPath) : null;
            string autogenBefore = Environment.GetEnvironmentVariable("NEXUS_DISABLE_AUTOGEN");
            try
            {
                Environment.SetEnvironmentVariable("NEXUS_DISABLE_AUTOGEN", null);
                NexusCodeGenerator.GenerateBinder();
                File.AppendAllText(binderPath, "// stale\n");

                var stale = new BinderStatusSource().Collect().Single(p => p.Message == NexusLang.Get("status_binder_stale"));
                Assert.IsFalse(stale.IsError, "a player build regenerates the binder");

                Environment.SetEnvironmentVariable("NEXUS_DISABLE_AUTOGEN", "1");
                var stops = new BinderStatusSource().Collect().Single(p => p.Message == NexusLang.Get("status_binder_stale_build_stops"));
                Assert.IsTrue(stops.IsError, "NEXUS_DISABLE_AUTOGEN=1 makes a player build stop on it");

                stops.Fix();
                Assert.IsTrue(NexusCodeGenerator.IsBinderCurrent());
                Assert.IsFalse(new BinderStatusSource().Collect().Any(p => p.Message == NexusLang.Get("status_binder_stale_build_stops")));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NEXUS_DISABLE_AUTOGEN", autogenBefore);
                Restore(binderPath, binderBefore);
                Restore(linkXmlPath, linkXmlBefore);
            }
        }

        private static void Restore(string path, string content)
        {
            if (content != null) File.WriteAllText(path, content);
            else if (File.Exists(path)) File.Delete(path);
        }
    }
}
