using System.Collections.Generic;
using UnityEngine;
using System.IO;

namespace Nexus.Editor
{
    internal static class NexusLang
    {
        private static Dictionary<string, string> s_strings;
        private static readonly string[] s_supportedLocales = { "en", "tr", "ja", "zh", "ko" };
        private static string s_currentLocale = "en";

        public static string CurrentLocale => s_currentLocale;
        public static IReadOnlyList<string> SupportedLocales => s_supportedLocales;

        static NexusLang()
        {
            s_currentLocale = UnityEditor.EditorPrefs.GetString("Nexus_Locale", "en");
            LoadLocale(s_currentLocale);
        }

        public static void LoadLocale(string locale)
        {
            s_currentLocale = locale;
            s_strings = new Dictionary<string, string>();

            // Default English fallback
            AddDefaults();

            // Try to load locale-specific overrides from JSON
            string path = $"Packages/com.nexus.core/Editor/Locales/{locale}.json";
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var overrides = JsonUtility.FromJson<LocaleData>(json);
                    if (overrides?.entries != null)
                    {
                        foreach (var entry in overrides.entries)
                        {
                            if (!string.IsNullOrEmpty(entry.key))
                                s_strings[entry.key] = entry.value;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[NexusLang] Failed to load locale '{locale}': {ex.Message}");
                }
            }
        }

        public static string Get(string key)
        {
            if (s_strings != null && s_strings.TryGetValue(key, out var val))
                return val;
            return key;
        }

        private static void AddDefaults()
        {
            s_strings["dashboard"] = "Dashboard";
            s_strings["system_active"] = "SYSTEM ACTIVE";
            s_strings["system_standby"] = "SYSTEM STANDBY";
            s_strings["contexts"] = "Contexts";
            s_strings["handlers"] = "Handlers";
            s_strings["roots"] = "Roots";
            s_strings["models"] = "Models";
            s_strings["services"] = "Services";
            s_strings["commands"] = "Commands";
            s_strings["views"] = "Views";
            s_strings["build_validation"] = "BUILD VALIDATION";
            s_strings["run_validation"] = "Run Build Validation";
            s_strings["rerun_validation"] = "Re-run Validation";
            s_strings["not_run_yet"] = "Not run yet. Click below to validate.";
            s_strings["project_overview"] = "PROJECT OVERVIEW";
            s_strings["runtime_metrics"] = "RUNTIME METRICS";
            s_strings["live_models"] = "LIVE MODELS";
            s_strings["quick_actions"] = "ALL TOOLS";
            s_strings["framework"] = "FRAMEWORK";
            s_strings["signals"] = "Signals";
            s_strings["total_sigs"] = "Total Sigs";
            s_strings["gc_memory"] = "GC Memory";
            s_strings["pass"] = "PASS";
            s_strings["fail"] = "FAIL";
            s_strings["errors"] = "Errors";
            s_strings["warnings"] = "Warnings";
            s_strings["no_reactive_models"] = "No IReactiveModel instances found.";
            s_strings["more"] = "more...";
            s_strings["ready"] = "Ready. Enter Play Mode to activate the system.";
            s_strings["no_roots"] = "Create a Root via Context Wizard to get started.";
            s_strings["live_hint"] = "Live — {0} context(s) active.";
            s_strings["perf_signals"] = "Signals/s";
            s_strings["perf_commands"] = "Commands/s";
        }

        [System.Serializable]
        private class LocaleData
        {
            public LocaleEntry[] entries;
        }

        [System.Serializable]
        private class LocaleEntry
        {
            public string key;
            public string value;
        }
    }
}
