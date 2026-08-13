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

        [Test]
        public void NEXUS003_StringLiteralThreadSleepIsNotFlagged()
        {
            // Parity with NEXUS001/NEXUS002: string literals are stripped before matching,
            // so a log message that merely mentions Thread.Sleep must not false-positive.
            const string line = "            Debug.Log(\"Thread.Sleep blocks the caller\");";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }

        [Test]
        public void NEXUS003_TrailingCommentMentioningBlockingIsNotFlagged()
        {
            // A trailing comment that documents blocking behavior must not trigger the rule
            // (the code itself does not block).
            const string line = "            ProcessQueue(); // Thread.Sleep would block here";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }

        [Test]
        public void NEXUS003_RealCallStillFlaggedWithTrailingComment()
        {
            // The stripped line still contains the real blocking call.
            const string line = "            System.Threading.Thread.Sleep(10); // backoff";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }

        [Test]
        public void NEXUS003_ExemptMarkerBeatsStrippedViolation()
        {
            // The exempt marker lives in the trailing comment; it must win over the
            // stripped code that still contains the blocking call.
            const string line = "            System.Threading.Thread.Sleep(2); // NEXUS003-exempt: 1-2 ms sync IO backoff";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus003Violation(line, "Assets/Scripts/EconomyService.cs"));
        }

        // ── Brace-depth hot-path tracking (NEXUS001 helper) ─────────────────────────

        [Test]
        public void CountBraces_OpenAndCloseCancelOut()
        {
            Assert.AreEqual(0, NexusArchitectureAnalyzer.CountBraces("void Update() { }"));
        }

        [Test]
        public void CountBraces_NestedBodyDeepens()
        {
            Assert.AreEqual(2, NexusArchitectureAnalyzer.CountBraces("if (x) { foreach (var y in z) {"));
        }

        [Test]
        public void CountBraces_ClosingDeeperThanOpeningGoesNegative()
        {
            Assert.AreEqual(-2, NexusArchitectureAnalyzer.CountBraces("} }"));
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

        [Test]
        public void StripCommentsAndStrings_RemovesCharLiterals()
        {
            // Char literals are removed so a '}' inside a char literal cannot skew
            // brace-depth hot-path tracking (NEXUS001) inside Update/Tick/Execute.
            // A '}' inside a char literal must be dropped without closing the block.
            Assert.AreEqual("char c = ;", NexusArchitectureAnalyzer.StripCommentsAndStrings("char c = '}';"));
            // Escaped char literals: the input line contains the 4-char C# literal '\\'
            // (quote, backslash, backslash, quote). The single backslash in the C# test
            // string must not mislead — here the literal is written correctly so the
            // escape-skip consumes the second backslash and the closing quote survives.
            Assert.AreEqual("if (c == ) i++;", NexusArchitectureAnalyzer.StripCommentsAndStrings("if (c == '\\\\') i++;"));
        }

        [Test]
        public void StripCommentsAndStrings_CharLiteralInsideStringIsNotStrippedTwice()
        {
            // The ' inside the double-quoted string belongs to the string, not a char
            // literal — the string is removed wholesale and the surrounding code survives.
            Assert.AreEqual("var msg = ;", NexusArchitectureAnalyzer.StripCommentsAndStrings("var msg = \"it's fine\";"));
        }

        // ── NEXUS004 (leftover debug trace logging) ───────────────────────────────

        [Test]
        public void NEXUS004_FlagsSlockTraceLogInRuntimeCode()
        {
            const string line = "            UnityEngine.Debug.Log($\"[SLOCK] got ActiveContexts t={System.Threading.Thread.CurrentThread.ManagedThreadId}\");";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus004Violation(line, "Assets/Scripts/GameService.cs"));
        }

        [Test]
        public void NEXUS004_FlagsDashTraceMarkerInRuntimeCode()
        {
            const string line = "            Debug.Log(\"[RESET-TRACE] clear: NexusDI.ClearCaches\");";
            Assert.IsTrue(NexusArchitectureAnalyzer.IsNexus004Violation(line, "Assets/Scripts/GameService.cs"));
        }

        [Test]
        public void NEXUS004_EditorPathIsExempt()
        {
            // The analyzer's own rule text carries these literals; Editor paths are
            // exempt (same as NEXUS003) so a self-scan cannot flag the rule itself.
            const string line = "            Debug.Log(\"[CTX-TRACE] something\");";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus004Violation(line, "Packages/com.nexus.core/Editor/Core/NexusWindow.cs"));
        }

        [Test]
        public void NEXUS004_CommentMentioningMarkerIsNotFlagged()
        {
            // No Debug.Log call in code — a comment that merely mentions the pattern
            // must not trigger (the stripped line has no Debug.Log).
            const string line = "            // remove the [SLOCK] marker before committing";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus004Violation(line, "Assets/Scripts/GameService.cs"));
        }

        [Test]
        public void NEXUS004_PlainDebugLogIsNotFlagged()
        {
            const string line = "            Debug.Log(\"Player died\");";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus004Violation(line, "Assets/Scripts/GameService.cs"));
        }

        [Test]
        public void NEXUS004_ExemptMarkerSkipsLine()
        {
            const string line = "            Debug.Log(\"[SLOCK] trace\"); // NEXUS004-exempt: deliberate deadlock tracing";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus004Violation(line, "Assets/Scripts/GameService.cs"));
        }

        [Test]
        public void NEXUS004_MarkerInStringWithoutLogCallIsNotFlagged()
        {
            // The marker must be attached to an actual Debug.Log call — a data string
            // that happens to contain "-TRACE]" must not be flagged.
            const string line = "            var label = name + \"-TRACE]\";";
            Assert.IsFalse(NexusArchitectureAnalyzer.IsNexus004Violation(line, "Assets/Scripts/GameService.cs"));
        }
    }
}
