using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Static C# Anti-Pattern & Architecture Analyzer for Nexus Core.
    /// Scans project scripts for hot-path allocations, lifecycle mismatches, and dangerous async void methods.
    /// Accessible via Editor Menu: Window -> Nexus -> Code Health Analyzer
    /// </summary>
    public class NexusArchitectureAnalyzer : EditorWindow
    {
        public enum IssueSeverity { Info, Warning, Error }

        public struct AnalysisIssue
        {
            public string Code;
            public IssueSeverity Severity;
            public string FilePath;
            public int LineNumber;
            public string Message;
            public string Recommendation;
        }

        private List<AnalysisIssue> _issues = new();
        private Vector2 _scrollPos;

        [MenuItem("Window/Nexus/Code Health Analyzer", false, 20)]
        public static void ShowWindow()
        {
            var win = GetWindow<NexusArchitectureAnalyzer>("Nexus Code Health Analyzer");
            win.minSize = new Vector2(650, 450);
            win.RunAnalysis();
        }

        /// <summary>
        /// Batch-mode (CI) entry point: runs the full analysis, logs every issue to the
        /// console, and exits with code 0 when clean / 1 when issues were found.
        /// Invoke via: Unity -batchmode -projectPath &lt;path&gt; -executeMethod
        /// Nexus.Editor.NexusArchitectureAnalyzer.RunHeadless
        /// </summary>
        public static void RunHeadless()
        {
            var analyzer = CreateInstance<NexusArchitectureAnalyzer>();
            analyzer.RunAnalysis();

            if (analyzer._issues.Count == 0)
            {
                Debug.Log($"[Nexus] Code Health Analyzer: clean — 0 issues across {s_scannedFilesCount} files.");
                EditorApplication.Exit(0);
                return;
            }

            Debug.LogError($"[Nexus] Code Health Analyzer: {analyzer._issues.Count} issue(s) across {s_scannedFilesCount} files:");
            foreach (var issue in analyzer._issues)
            {
                Debug.LogError($"[{issue.Code}] {issue.Severity}: {issue.Message} — {issue.FilePath}:{issue.LineNumber} | {issue.Recommendation}");
            }
            EditorApplication.Exit(1);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Nexus Code Health & Anti-Pattern Analyzer", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy All Logs", GUILayout.Width(110), GUILayout.Height(24)))
            {
                CopyIssuesToClipboard();
            }
            if (GUILayout.Button("Run Analysis", GUILayout.Width(110), GUILayout.Height(24)))
            {
                RunAnalysis();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox($"Analyzed {s_scannedFilesCount} script(s). Found {_issues.Count} issue(s).", _issues.Count == 0 ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.Space(5);
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            foreach (var issue in _issues)
            {
                Color boxColor = issue.Severity switch
                {
                    IssueSeverity.Error => new Color(0.4f, 0.1f, 0.1f, 0.4f),
                    IssueSeverity.Warning => new Color(0.4f, 0.3f, 0.1f, 0.4f),
                    _ => new Color(0.2f, 0.2f, 0.3f, 0.4f)
                };

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                
                string tag = issue.Severity == IssueSeverity.Error ? "❌ ERROR" : (issue.Severity == IssueSeverity.Warning ? "⚠️ WARN" : "ℹ️ INFO");
                GUILayout.Label($"[{issue.Code}] {tag}: {issue.Message}", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                
                if (!string.IsNullOrEmpty(issue.FilePath) && GUILayout.Button("Open", GUILayout.Width(50)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<MonoScript>(issue.FilePath);
                    if (obj != null) AssetDatabase.OpenAsset(obj, issue.LineNumber);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField("Location:", $"{issue.FilePath}:{issue.LineNumber}");
                EditorGUILayout.LabelField("Recommendation:", issue.Recommendation);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
        }

        private static int s_scannedFilesCount = 0;

        public void RunAnalysis()
        {
            _issues.Clear();
            s_scannedFilesCount = 0;

            string[] scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets", "Packages/com.nexus.core" });
            foreach (var guid in scriptGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.EndsWith(".g.cs") || path.Contains("/Tests/")) continue;

                s_scannedFilesCount++;
                AnalyzeScriptFile(path);
            }
        }

        private void CopyIssuesToClipboard()
        {
            if (_issues.Count == 0)
            {
                EditorGUIUtility.systemCopyBuffer = "=== Nexus Code Health Analyzer Report: No issues found. ===";
                ShowNotification(new GUIContent("No logs to copy!"));
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Nexus Code Health Analyzer Report ({_issues.Count} issue(s) found across {s_scannedFilesCount} files) ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            for (int i = 0; i < _issues.Count; i++)
            {
                var issue = _issues[i];
                sb.AppendLine($"[{i + 1}] [{issue.Code}] {issue.Severity.ToString().ToUpper()}: {issue.Message}");
                sb.AppendLine($"    Location: {issue.FilePath}:{issue.LineNumber}");
                sb.AppendLine($"    Recommendation: {issue.Recommendation}");
                sb.AppendLine();
            }

            EditorGUIUtility.systemCopyBuffer = sb.ToString();
            ShowNotification(new GUIContent($"Copied {_issues.Count} log(s) to clipboard!"));
        }

        private void AnalyzeScriptFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return;

            string[] lines = File.ReadAllLines(fullPath);
            bool inHotPathMethod = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNum = i + 1;
                string trimmed = line.Trim();

                // Skip comment lines, docstrings, and recommendation string definitions to avoid false positives
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*") || trimmed.StartsWith("/*") ||
                    trimmed.StartsWith("Recommendation =") || trimmed.StartsWith("Message ="))
                    continue;

                // Track hot-path method entry/exit
                if (line.Contains("void Update()") || line.Contains("void Tick(") || line.Contains("void OnUpdate()") || line.Contains("void Execute("))
                {
                    inHotPathMethod = true;
                }
                else if (inHotPathMethod && line.Trim().StartsWith("}"))
                {
                    inHotPathMethod = false;
                }

                // Rule NEXUS001: Hot-Path Allocations
                if (inHotPathMethod && IsNexus001Violation(line))
                {
                    _issues.Add(new AnalysisIssue
                    {
                        Code = "NEXUS001",
                        Severity = IssueSeverity.Warning,
                        FilePath = path,
                        LineNumber = lineNum,
                        Message = "Allocation or LINQ detected inside Hot-Path method (Update/Tick/Execute).",
                        Recommendation = "Pre-allocate collections or use zero-GC Array/Span iterators to prevent GC spikes."
                    });
                }

                // Rule NEXUS002: Async Void
                if (IsNexus002Violation(line))
                {
                    _issues.Add(new AnalysisIssue
                    {
                        Code = "NEXUS002",
                        Severity = IssueSeverity.Error,
                        FilePath = path,
                        LineNumber = lineNum,
                        Message = "Uncaught 'async void' method declaration.",
                        Recommendation = "Replace 'async void' with 'async ValueTask' or 'async Task' to prevent process crashes on unhandled exceptions."
                    });
                }

                // Rule NEXUS003: Synchronous blocking calls (Thread.Sleep + sync-over-async
                // like Task.Delay(...).GetAwaiter().GetResult(), which still blocks the thread).
                // A trailing "// NEXUS003-exempt: <reason>" comment marks a deliberate,
                // documented blocking site (e.g. EncryptedStorageService's 1-2 ms IO backoff,
                // NexusTestHarness's rethrow-only GetResult) and opts it out.
                if (IsNexus003Violation(line, path))
                {
                    _issues.Add(new AnalysisIssue
                    {
                        Code = "NEXUS003",
                        Severity = IssueSeverity.Error,
                        FilePath = path,
                        LineNumber = lineNum,
                        Message = "Synchronous blocking or sync-over-async call detected in runtime code.",
                        Recommendation = "Use 'await Task.Delay()' or a ValueTask timer so the thread yields instead of blocking. For a deliberate sync site, append '// NEXUS003-exempt: <reason>' to the line."
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
        /// Removes double-quoted string literals and trailing <c>//</c> comments from a source
        /// line so the text-based rules (NEXUS001) match CODE tokens only — a trailing comment
        /// like <c>// pre-allocate: a new List&lt;int&gt;() here would be flagged</c> must not
        /// count as an allocation. Escaped characters and a <c>//</c> inside a string
        /// (e.g. a URL) are handled.
        /// </summary>
        internal static string StripCommentsAndStrings(string line)
        {
            char[] result = new char[line.Length];
            int n = 0;
            bool inString = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inString)
                {
                    if (c == '\\') i++;            // skip escaped character inside the string
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break; // trailing comment
                result[n++] = c;
            }
            return new string(result, 0, n);
        }

        /// <summary>
        /// NEXUS003 predicate: true when a runtime (non-Editor) line contains a synchronous
        /// blocking call (Thread.Sleep or sync-over-async <c>GetAwaiter().GetResult()</c>,
        /// which still blocks the thread) that is not explicitly exempted via a trailing
        /// <c>// NEXUS003-exempt: &lt;reason&gt;</c> comment. Comment lines and Editor paths
        /// are filtered by the caller (<see cref="AnalyzeScriptFile"/>). Internal so the
        /// editor test assembly can lock the rule.
        /// </summary>
        internal static bool IsNexus003Violation(string line, string path)
        {
            return (line.Contains("Thread.Sleep") || line.Contains("GetAwaiter().GetResult()"))
                   && !path.Contains("/Editor/")
                   && !line.Contains("NEXUS003-exempt");
        }
    }
}
