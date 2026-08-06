using NUnit.Framework;
using Nexus.Editor;

namespace Nexus.Tests.Editor
{
    /// <summary>
    /// Locks the NEXUS003 rule of <see cref="NexusArchitectureAnalyzer"/>: it must flag
    /// sync-over-async <c>GetAwaiter().GetResult()</c> and <c>Thread.Sleep</c> in runtime
    /// (non-Editor) code, and must honor the trailing <c>// NEXUS003-exempt: &lt;reason&gt;</c>
    /// marker convention used by GameSaveManager, EncryptedStorageService and NexusTestHarness.
    /// </summary>
    [TestFixture]
    public class NexusArchitectureAnalyzerTests
    {
        [Test]
        public void NEXUS003_FlagsUnmarkedGetAwaiterInRuntimeCode()
        {
            const string line = "            Task.Delay(100, ct).GetAwaiter().GetResult();";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }

        [Test]
        public void NEXUS003_FlagsThreadSleepInRuntimeCode()
        {
            const string line = "            System.Threading.Thread.Sleep(10);";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }

        [Test]
        public void NEXUS003_ExemptMarkerSkipsLine()
        {
            const string line = "            Task.Delay(1 << attempt).GetAwaiter().GetResult(); // NEXUS003-exempt: 1-2 ms sync IO backoff (sync API + quit path cannot await)";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }

        [Test]
        public void NEXUS003_EditorPathIsExempt()
        {
            const string line = "            Task.Delay(100, ct).GetAwaiter().GetResult();";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Packages/com.nexus.core/Editor/Analyzer/NexusArchitectureAnalyzer.cs"));
        }

        [Test]
        public void NEXUS003_BenignAsyncCallIsNotFlagged()
        {
            const string line = "            await Task.Delay(100, ct);";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }
    }
}
