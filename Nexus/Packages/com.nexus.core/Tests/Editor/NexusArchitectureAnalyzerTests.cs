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

        // ── NEXUS002 (async void) ──────────────────────────────────────────────────

        [Test]
        public void NEXUS002_FlagsAsyncVoidDeclaration()
        {
            const string line = "        public async void FireAndForget()";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus002Violation(line));
        }

        [Test]
        public void NEXUS002_UnityOnClickCallbackIsExempt()
        {
            const string line = "        private async void OnClickButton()";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus002Violation(line));
        }

        [Test]
        public void NEXUS002_UnityStartEntryPointIsExempt()
        {
            const string line = "        private async void Start()";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus002Violation(line));
        }

        [Test]
        public void NEXUS002_AsyncValueTaskIsNotFlagged()
        {
            const string line = "        public async ValueTask InitializeAsync(CancellationToken ct)";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus002Violation(line));
        }

        [Test]
        public void NEXUS002_OnEventCallbackIsExempt()
        {
            const string line = "        private async void OnEventOccurred(string payload)";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus002Violation(line));
        }

        [Test]
        public void NEXUS002_StringLiteralAsyncVoidIsNotFlagged()
        {
            // The predicate's own implementation contains the "async void" literal;
            // string literals are stripped so a self-scan cannot flag it.
            const string line = "            return line.Contains(\"async void\");";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus002Violation(line));
        }

        [Test]
        public void NEXUS002_DocCommentAsyncVoidIsNotFlagged()
        {
            const string line = "        /// Replaces uncaught <c>async void</c> declarations.";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus002Violation(line));
        }

        // ── NEXUS001 (hot-path allocations) ───────────────────────────────────────

        [Test]
        public void NEXUS001_FlagsNewListInsideHotPath()
        {
            const string line = "                (due ??= new List<SaveSlot>(2)).Add(slot);";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus001Violation(line));
        }

        [Test]
        public void NEXUS001_FlagsLinqInsideHotPath()
        {
            const string line = "            var filtered = items.Where(x => x > 0).Select(y => y * 2);";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus001Violation(line));
        }

        [Test]
        public void NEXUS001_AllocationInCommentIsNotFlagged()
        {
            const string line = "            // pre-allocate: a new List<int>() here would be flagged";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus001Violation(line));
        }

        [Test]
        public void NEXUS001_NoAllocationLineIsNotFlagged()
        {
            const string line = "            var due = GetTickDueBuffer();";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus001Violation(line));
        }

        // ── String/comment stripping (shared by NEXUS001) ─────────────────────────

        [Test]
        public void StripCommentsAndStrings_RemovesTrailingComment()
        {
            Assert.AreEqual("builder.Bind<SomeService>(); ", NexusArchitectureAnalyzer.StripCommentsAndStrings("builder.Bind<SomeService>(); // trailing"));
        }

        [Test]
        public void StripCommentsAndStrings_RemovesStringLiterals()
        {
            Assert.AreEqual("s_strings[] = ;", NexusArchitectureAnalyzer.StripCommentsAndStrings("s_strings[\"some_key\"] = \"(localized text)\";"));
        }

        [Test]
        public void StripCommentsAndStrings_KeepsUrlInsideString()
        {
            // The // inside "http://example.com" must survive (it is inside a string), and
            // only the real trailing comment is dropped.
            Assert.AreEqual("var url = ; ", NexusArchitectureAnalyzer.StripCommentsAndStrings("var url = \"http://example.com\"; // note"));
        }
    }
}
