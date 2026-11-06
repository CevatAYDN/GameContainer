using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;

namespace Nexus.Editor
{
    /// <summary>
    /// Text rules over the project's scripts, listed by the Status tab (#193): hot-path allocations (NEXUS001, a warning),
    /// async-void declarations (NEXUS002), synchronous blocking calls (NEXUS003) and leftover debug trace logging (NEXUS004).
    /// </summary>
    internal static class CodeHealthRules
    {
        internal struct Issue
        {
            public string Code;
            public bool IsError;
            public string FilePath;
            public int LineNumber;
            public string Message;
            public string Recommendation;
        }

        /// <summary>
        /// Scans the scripts a game can change: Assets and the Nexus packages it has embedded or on a local path. A package
        /// consumed by git URL is read-only to the game, so its files are left out, as are generated files and Tests folders.
        /// </summary>
        internal static List<Issue> Scan()
        {
            var folders = new List<string> { "Assets" };
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                if (package.name.StartsWith("com.nexus.") && (package.source == PackageSource.Embedded || package.source == PackageSource.Local))
                    folders.Add(package.assetPath);
            var issues = new List<Issue>();
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript", folders.ToArray()))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.EndsWith(".g.cs") || path.Contains("/Tests/")) continue;
                AnalyzeScriptFile(path, issues);
            }
            return issues;
        }

        private static void AnalyzeScriptFile(string path, List<Issue> issues)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return;

            AnalyzeLines(path, File.ReadAllLines(fullPath), issues);
        }

        /// <summary>The rules over the lines of one script; <paramref name="path"/> is what the issues name and the path rules read.</summary>
        internal static void AnalyzeLines(string path, string[] lines, List<Issue> issues)
        {
            bool inHotPathMethod = false;
            int hotPathBraceDepth = 0;
            bool inBlockComment = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNum = i + 1;
                // A /* */ block's lines carry no leading '*' to skip: commented-out code in one was reported as an error (#315).
                if (inBlockComment)
                {
                    int close = line.IndexOf("*/", System.StringComparison.Ordinal);
                    if (close < 0) continue;
                    inBlockComment = false;
                    line = line.Substring(close + 2); // code after the close still counts
                }
                string trimmed = line.Trim();
                if (trimmed.StartsWith("/*") && !trimmed.Contains("*/")) inBlockComment = true;

                // Skip comment lines, docstrings, and recommendation string definitions to avoid false positives
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*") || trimmed.StartsWith("/*") ||
                    trimmed.StartsWith("Recommendation =") || trimmed.StartsWith("Message ="))
                    continue;

                // Track hot-path method entry/exit by brace depth instead of a
                // "line starts with }" heuristic: a nested block closed at column 0
                // (valid C# style) used to terminate hot-path tracking early and hide
                // later allocations, while an expression-bodied Update() never emitted a
                // brace line at all and kept the whole file "hot". Braces are counted on
                // comment/string-free code so doc text and string literals cannot skew it.
                if (inHotPathMethod)
                {
                    hotPathBraceDepth += CountBraces(StripCommentsAndStrings(line));
                    if (hotPathBraceDepth <= 0)
                    {
                        inHotPathMethod = false;
                        hotPathBraceDepth = 0;
                    }
                }
                else if (IsHotPathEntryLine(line))
                {
                    inHotPathMethod = true;
                    hotPathBraceDepth = CountBraces(StripCommentsAndStrings(line));
                    // A zero/negative depth on the entry line means a single-line or
                    // expression-bodied method: stay hot for THIS line (its allocations
                    // are in the hot path) and let the next line close the block.
                    if (hotPathBraceDepth < 0) hotPathBraceDepth = 0;
                }

                // Rule NEXUS001: Hot-Path Allocations
                if (inHotPathMethod && IsNexus001Violation(line))
                {
                    issues.Add(new Issue
                    {
                        Code = "NEXUS001",
                        IsError = false,
                        FilePath = path,
                        LineNumber = lineNum,
                        Message = "Allocation or LINQ detected inside Hot-Path method (Update/Tick/Execute).",
                        Recommendation = "Pre-allocate collections or use zero-GC Array/Span iterators to prevent GC spikes."
                    });
                }

                // Rule NEXUS002: Async Void
                if (IsNexus002Violation(line))
                {
                    issues.Add(new Issue
                    {
                        Code = "NEXUS002",
                        IsError = true,
                        FilePath = path,
                        LineNumber = lineNum,
                        Message = "Uncaught 'async void' method declaration.",
                        Recommendation = "Replace 'async void' with 'async ValueTask' or 'async Task' to prevent process crashes on unhandled exceptions. When the signature must stay void (an event handler, an interface method) and the body catches every exception, append '// NEXUS002-exempt: <reason>' to the line."
                    });
                }

                // Rule NEXUS003: Synchronous blocking calls (Thread.Sleep + sync-over-async
                // like Task.Delay(...).GetAwaiter().GetResult(), which still blocks the thread).
                // A trailing "// NEXUS003-exempt: <reason>" comment marks a deliberate,
                // documented blocking site (e.g. EncryptedStorageService's 1-2 ms IO backoff,
                // NexusTestHarness's rethrow-only GetResult) and opts it out.
                if (IsNexus003Violation(line, path))
                {
                    issues.Add(new Issue
                    {
                        Code = "NEXUS003",
                        IsError = true,
                        FilePath = path,
                        LineNumber = lineNum,
                        Message = "Synchronous blocking or sync-over-async call detected in runtime code.",
                        Recommendation = "Use 'await Task.Delay()' or a ValueTask timer so the thread yields instead of blocking. For a deliberate sync site, append '// NEXUS003-exempt: <reason>' to the line."
                    });
                }

                // Rule NEXUS004: leftover debug trace logging in runtime code. A
                // Debug.Log call whose string carries a trace marker ([SLOCK] or a
                // "*-TRACE]" tag such as [RESET-TRACE]/[CTX-TRACE]) is the signature of
                // lock/flow instrumentation left behind after a debugging session — it
                // allocates on hot paths, spams the console, and ships to production
                // builds. A trailing "// NEXUS004-exempt: <reason>" comment marks a
                // deliberate, documented trace site and opts it out.
                if (IsNexus004Violation(line, path))
                {
                    issues.Add(new Issue
                    {
                        Code = "NEXUS004",
                        IsError = true,
                        FilePath = path,
                        LineNumber = lineNum,
                        Message = "Debug trace logging detected in runtime code ([SLOCK] / [*-TRACE] marker).",
                        Recommendation = "Remove leftover instrumentation before committing. For a deliberate trace site, append '// NEXUS004-exempt: <reason>' to the line."
                    });
                }

            }
        }

        /// <summary>
        /// NEXUS002 predicate: true when a line declares an uncaught <c>async void</c>
        /// method. Unity event callbacks (<c>OnClick</c>/<c>OnEvent</c> and the deliberate
        /// <c>async void Start()</c> Unity entry point) are exempt because their signatures
        /// are fixed by the engine and cannot be changed to Task. Comments and string
        /// literals are stripped first (same as NEXUS001) so the predicate's own
        /// implementation — which necessarily contains the <c>"async void"</c> literal —
        /// and any doc text cannot trigger the rule (self-scan false positive). Internal so
        /// the editor test assembly can lock the rule.
        /// </summary>
        internal static bool IsNexus002Violation(string line)
        {
            // an async void whose signature is fixed and whose body catches every exception says so in a trailing comment (raw line)
            if (line.Contains("NEXUS002-exempt")) return false;
            string codeOnly = StripCommentsAndStrings(line);
            return codeOnly.Contains("async void")
                   && !codeOnly.Contains("OnClick")
                   && !codeOnly.Contains("OnEvent")
                   && !codeOnly.Contains("async void Start()");
        }

        /// <summary>
        /// NEXUS001 predicate: true when a line inside a hot-path method (Update/Tick/Execute)
        /// allocates a new collection or uses LINQ. Comments and string literals are stripped
        /// first so documentation text cannot trigger the rule. Internal so the editor test
        /// assembly can lock the rule.
        /// </summary>
        internal static bool IsNexus001Violation(string line)
        {
            string codeOnly = StripCommentsAndStrings(line);
            return codeOnly.Contains("new List<")
                || codeOnly.Contains("new Dictionary<")
                || codeOnly.Contains(".Where(")
                || codeOnly.Contains(".Select(");
        }

        /// <summary>
        /// Removes double-quoted string literals, char literals, and trailing <c>//</c>
        /// comments from a source line so the text-based rules (NEXUS001) match CODE tokens
        /// only — a trailing comment like <c>// pre-allocate: a new List&lt;int&gt;() here
        /// would be flagged</c> must not count as an allocation, and a char literal like
        /// <c>char c = '}'</c> must not skew brace-depth counting. Escaped characters and a
        /// <c>//</c> inside a string (e.g. a URL) are handled.
        /// </summary>
        internal static string StripCommentsAndStrings(string line)
        {
            char[] result = new char[line.Length];
            int n = 0;
            bool inString = false;
            bool inChar = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inString)
                {
                    if (c == '\\') i++;            // skip escaped character inside the string
                    else if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') i++;            // skip escaped character inside the char literal
                    else if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break; // trailing comment
                result[n++] = c;
            }
            return new string(result, 0, n);
        }

        /// <summary>
        /// True when a line declares (or mentions declaring) a hot-path method. Matches
        /// Unity callbacks (Update/Tick/OnUpdate) and Nexus command entry points (Execute).
        /// </summary>
        internal static bool IsHotPathEntryLine(string line)
        {
            return line.Contains("void Update()") || line.Contains("void Tick(")
                || line.Contains("void OnUpdate()") || line.Contains("void Execute(");
        }

        /// <summary>
        /// Net brace depth contributed by a single code line ({ = +1, } = -1). Callers pass
        /// the comment/string-stripped line so braces inside string literals and doc text
        /// do not skew hot-path tracking. Internal so the editor test assembly can lock it.
        /// </summary>
        internal static int CountBraces(string line)
        {
            int depth = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return depth;
        }

        /// <summary>
        /// NEXUS003 predicate: true when a runtime (non-Editor) line contains a synchronous
        /// blocking call (Thread.Sleep or sync-over-async <c>GetAwaiter().GetResult()</c>,
        /// which still blocks the thread) that is not explicitly exempted via a trailing
        /// <c>// NEXUS003-exempt: &lt;reason&gt;</c> comment. Full-line comments are filtered
        /// by the caller (AnalyzeScriptFile); string literals and trailing
        /// comments are stripped here (like NEXUS001/NEXUS002) so doc text and logging
        /// cannot false-positive. Internal so the editor test assembly can lock the rule.
        /// </summary>
        internal static bool IsNexus003Violation(string line, string path)
        {
            if (path.Contains("/Editor/")) return false;
            // The exemption marker is a trailing comment (// NEXUS003-exempt: <reason>),
            // so it must be evaluated on the RAW line — StripCommentsAndStrings would
            // erase it before the check.
            if (line.Contains("NEXUS003-exempt")) return false;
            // NEXUS001/NEXUS002 strip comments and string literals before matching; do
            // the same here so a runtime line like Debug.Log("Thread.Sleep") or a
            // trailing comment mentioning Thread.Sleep cannot false-positive.
            string codeOnly = StripCommentsAndStrings(line);
            return codeOnly.Contains("Thread.Sleep") || codeOnly.Contains("GetAwaiter().GetResult()");
        }

        /// <summary>
        /// NEXUS004 predicate: true when a runtime (non-Editor) line calls Debug.Log with
        /// a debug trace marker in its string — <c>[SLOCK]</c> or a <c>*-TRACE]</c> tag
        /// such as <c>[RESET-TRACE]</c>/<c>[CTX-TRACE]</c> — the signature of leftover
        /// lock/flow instrumentation. The marker lives INSIDE the string literal, so it is
        /// matched on the RAW line (StripCommentsAndStrings would erase it), while an
        /// actual <c>Debug.Log</c> call is required in the stripped code so a comment that
        /// merely mentions the pattern cannot trigger. Editor paths are exempt (same as
        /// NEXUS003) so the analyzer's own rule text cannot self-flag; a deliberate trace
        /// log opts out with a trailing <c>// NEXUS004-exempt: &lt;reason&gt;</c> comment.
        /// Internal so the editor test assembly can lock the rule.
        /// </summary>
        internal static bool IsNexus004Violation(string line, string path)
        {
            if (path.Contains("/Editor/")) return false;
            // The exemption marker is a trailing comment; evaluated on the RAW line for
            // the same reason as NEXUS003.
            if (line.Contains("NEXUS004-exempt")) return false;
            // Require a real Debug.Log call in CODE (comments/strings stripped) — a
            // comment mentioning the marker pattern must never trigger.
            string codeOnly = StripCommentsAndStrings(line);
            if (!codeOnly.Contains("Debug.Log")) return false;
            // The marker lives inside the string literal, so it is matched on the raw line.
            return line.Contains("-TRACE]") || line.Contains("[SLOCK]");
        }
    }
}
