using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    public static class NexusSystemDiagnostic
    {
        [MenuItem("Nexus/Run System Diagnostic Audit %&d", false, 1)]
        public static void RunDiagnostic()
        {
            var sw = Stopwatch.StartNew();
            int totalErrors = 0;
            int totalWarnings = 0;

            UnityEngine.Debug.Log("===============================================================================");
            UnityEngine.Debug.Log("[Nexus System Diagnostic] Starting Comprehensive Architecture & Runtime Audit...");
            UnityEngine.Debug.Log("===============================================================================");

            // 1. AOT Binder Verification & Generation
            try
            {
                UnityEngine.Debug.Log("[Nexus Audit] 1/3 Verifying AOT Binder generation...");
                NexusCodeGenerator.GenerateBinder();
                UnityEngine.Debug.Log("[Nexus Audit] PASS  AOT Binder regenerated successfully (NexusGeneratedBinder.g.cs).");
            }
            catch (Exception ex)
            {
                totalErrors++;
                UnityEngine.Debug.LogError($"[Nexus Audit] FAIL  AOT Binder generation failed: {ex.Message}");
            }

            // 2. Build Architecture Rules Validation
            try
            {
                UnityEngine.Debug.Log("[Nexus Audit] 2/3 Running Build Architecture Validation rules engine...");
                bool passed = BuildValidation.Validate();
                totalErrors += BuildValidation.LastErrorCount;
                totalWarnings += BuildValidation.LastWarningCount;

                if (passed)
                {
                    UnityEngine.Debug.Log($"[Nexus Audit] PASS  Architecture Validation passed ({BuildValidation.LastRunSummary}).");
                }
                else
                {
                    UnityEngine.Debug.LogError($"[Nexus Audit] FAIL  Architecture Validation failed ({BuildValidation.LastRunSummary}). Check Console for details.");
                }
            }
            catch (Exception ex)
            {
                totalErrors++;
                UnityEngine.Debug.LogError($"[Nexus Audit] FAIL  Architecture Validation threw exception: {ex.Message}");
            }

            // 3. Play-Mode Active Context & Memory Audit
            if (Application.isPlaying)
            {
                UnityEngine.Debug.Log("[Nexus Audit] 3/3 Inspecting active Play-Mode Contexts and Services...");
                var activeContexts = NexusRuntime.ActiveContexts;
                UnityEngine.Debug.Log($"[Nexus Audit] Active Contexts: {activeContexts.Count}");
                for (int i = 0; i < activeContexts.Count; i++)
                {
                    var ctx = activeContexts[i];
                    int singletonsCount = (ctx as Context)?.Container?.ActiveSingletonsCount ?? 0;
                    UnityEngine.Debug.Log($"      [{i + 1}] Context: '{ctx.GetType().Name}', Registered Singletons: {singletonsCount}");
                }
            }
            else
            {
                UnityEngine.Debug.Log("[Nexus Audit] 3/3 Play-Mode Context inspection skipped (Editor in Edit-Mode).");
            }

            sw.Stop();
            UnityEngine.Debug.Log("===============================================================================");
            if (totalErrors == 0)
            {
                UnityEngine.Debug.Log($"[Nexus System Diagnostic] AUDIT COMPLETED IN {sw.ElapsedMilliseconds}ms — ALL CHECKS PASSED ✓ ({totalWarnings} warnings)");
                EditorUtility.DisplayDialog("Nexus Diagnostic Audit", $"All System Checks Passed! ✓\n\nExecution Time: {sw.ElapsedMilliseconds}ms\nWarnings: {totalWarnings}", "OK");
            }
            else
            {
                UnityEngine.Debug.LogError($"[Nexus System Diagnostic] AUDIT FAILED IN {sw.ElapsedMilliseconds}ms — {totalErrors} ERRORS, {totalWarnings} WARNINGS ✗");
                EditorUtility.DisplayDialog("Nexus Diagnostic Audit", $"System Diagnostic Found Issues! ✗\n\nErrors: {totalErrors}\nWarnings: {totalWarnings}\n\nPlease inspect Unity Console for details.", "OK");
            }
        }
    }
}
