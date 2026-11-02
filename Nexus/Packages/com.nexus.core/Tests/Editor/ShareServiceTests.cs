using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class ShareServiceTests
    {
        // #134 acceptance: in the editor a share is logged with its path and reports that no sheet opened.
        [Test]
        public void ShareFile_InTheEditor_LogsThePath_AndReportsNothingShared()
        {
            string path = Path.GetTempFileName();
            try
            {
                LogAssert.Expect(LogType.Log, new Regex(Regex.Escape(path) + ".*editor preview"));
                Assert.IsFalse(new ShareService().ShareFile(path, "text/plain", "Share"));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
