using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    [Preserve]
    public class LocalizationService : NexusService<ILocalizationService>, ILocalizationService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        // Optional: built-in "en"/"tr" tables work without it; LoadExternalTables()
        // null-checks. A missing binding must not fail strict injection / validation.
        [OptionalInject] public ILocalizationTableProvider TableProvider { get; set; }
        // Optional: without it the first run is English and the culture follows the language code.
        [OptionalInject] public LocalizationOptions Options { get; set; }

        private const string LanguageKey = "NT_Language";

        // The device language as a code ("tr", "pt", "zh-hans"); tests replace it.
        internal Func<string> DeviceLanguageSource = DeviceLanguageCode;

        // CurrentLanguage is accessed from multiple threads (editor main, async tasks,
        // and potential worker threads). Mark volatile to ensure reads always observe the
        // most recent write. Accesses that rely on dictionary lookups are synchronized
        // via _tableLock to avoid TOCTOU races.
        private volatile string _currentLanguage = "en";
        public string CurrentLanguage { get => _currentLanguage; private set => _currentLanguage = value; }

        public event Action<string> OnLanguageChanged;

        private CultureInfo _culture;
        public CultureInfo Culture => _culture ?? (_culture = CultureFor(_currentLanguage));

        // The language part decides, so "ar-eg" and "he_il" (the codes a device locale gives) read right to left too (issue #228).
        public bool IsRTL
        {
            get
            {
                var lang = _currentLanguage;
                if (lang == null || lang.Length < 2 || (lang.Length > 2 && lang[2] != '-' && lang[2] != '_')) return false;
                return IsLanguage(lang, "ar") || IsLanguage(lang, "he") || IsLanguage(lang, "fa") || IsLanguage(lang, "ur");
            }
        }

        private static bool IsLanguage(string code, string language) => string.CompareOrdinal(code, 0, language, 0, 2) == 0;

        public LocalizationService() { }

        public LocalizationService(IPlayerPrefsService prefs, ILocalizationTableProvider tableProvider = null)
        {
            PlayerPrefsService = prefs;
            TableProvider = tableProvider;
        }

        private readonly Dictionary<string, Dictionary<string, string>> _localizedTable = new Dictionary<string, Dictionary<string, string>>();
        private readonly object _tableLock = new();
        private readonly object _saveLock = new();
        private HashSet<string> _badFormatKeys;
        // Each missing text is reported once (issue #187): (null, key) found nowhere, (language, null) no table for the
        // language, (language, key) only the English text. A value tuple, so a repeated miss allocates nothing.
        private HashSet<(string language, string key)> _reportedMisses;
        // In a right-to-left language, the reversed form of each text GetString returned, by the text: GetString keeps its
        // 0 B there too (issue #229). The reversal depends on the text alone; the cache is emptied when the language changes.
        private readonly Dictionary<string, string> _rtlShaped = new Dictionary<string, string>();
        private const int MaxRtlShaped = 4096;
        // Languages the game supplied (provider, RegisterLanguageTable, RegisterKey), not the built-in en/tr seeds.
        private readonly HashSet<string> _suppliedLanguages = new HashSet<string>();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            BuildLocalizationDictionary();
            LoadExternalTables();
            LoadSavedLanguage();
            return default;
        }

        public override void OnDispose()
        {
            OnLanguageChanged = null;
            lock (_tableLock)
            {
                _localizedTable.Clear();
                _suppliedLanguages.Clear();
                _rtlShaped.Clear();
            }
        }

        private void LoadSavedLanguage()
        {
            if (PlayerPrefsService != null && PlayerPrefsService.HasKey(LanguageKey))
                CurrentLanguage = PlayerPrefsService.GetString(LanguageKey, "en").ToLowerInvariant(); // tables are keyed lower-case (RegisterLanguageTable); a game may save "pt-BR"
            else if (Options != null)
                CurrentLanguage = FirstRunLanguage(Options); // not saved: the device language is followed until the player picks one
            _culture = CultureFor(_currentLanguage);
        }

        private string FirstRunLanguage(LocalizationOptions options)
        {
            string fallback = string.IsNullOrEmpty(options.DefaultLanguage) ? "en" : options.DefaultLanguage.ToLowerInvariant();
            if (!options.UseDeviceLanguage) return fallback;
            string device = null;
            try { device = DeviceLanguageSource?.Invoke(); }
            catch (Exception ex) { NexusLog.WarnAlways($"[LocalizationService] Reading the device language failed: {ex.Message}"); }
            if (string.IsNullOrEmpty(device)) return fallback;
            device = device.ToLowerInvariant();
            int dash = device.IndexOf('-');
            string baseLanguage = dash > 0 ? device.Substring(0, dash) : null;
            lock (_tableLock)
            {
                IEnumerable<string> languages = _suppliedLanguages.Count > 0 ? _suppliedLanguages : _localizedTable.Keys;
                string regional = null;
                foreach (var language in languages)
                {
                    if (language == device) return language;
                    if (regional == null && (language == baseLanguage || language.StartsWith(device + "-", StringComparison.Ordinal))) regional = language;
                }
                return regional ?? fallback;
            }
        }

        private CultureInfo CultureFor(string language)
        {
            var cultures = Options?.Cultures;
            if (cultures != null)
            {
                foreach (var entry in cultures)
                    if (entry.Value != null && string.Equals(entry.Key, language, StringComparison.OrdinalIgnoreCase)) return entry.Value;
            }
            try { return CultureInfo.CreateSpecificCulture(language); }
            catch (ArgumentException) { return CultureInfo.InvariantCulture; }
        }

        private static readonly Dictionary<string, string> s_systemLanguageCodes = new Dictionary<string, string>
        {
            { "Afrikaans", "af" }, { "Arabic", "ar" }, { "Basque", "eu" }, { "Belarusian", "be" }, { "Bulgarian", "bg" },
            { "Catalan", "ca" }, { "Chinese", "zh" }, { "ChineseSimplified", "zh-hans" }, { "ChineseTraditional", "zh-hant" },
            { "Czech", "cs" }, { "Danish", "da" }, { "Dutch", "nl" }, { "English", "en" }, { "Estonian", "et" },
            { "Faroese", "fo" }, { "Finnish", "fi" }, { "French", "fr" }, { "German", "de" }, { "Greek", "el" },
            { "Hebrew", "he" }, { "Hindi", "hi" }, { "Hungarian", "hu" }, { "Icelandic", "is" }, { "Indonesian", "id" },
            { "Italian", "it" }, { "Japanese", "ja" }, { "Korean", "ko" }, { "Latvian", "lv" }, { "Lithuanian", "lt" },
            { "Norwegian", "no" }, { "Polish", "pl" }, { "Portuguese", "pt" }, { "Romanian", "ro" }, { "Russian", "ru" },
            { "SerboCroatian", "sr" }, { "Slovak", "sk" }, { "Slovenian", "sl" }, { "Spanish", "es" }, { "Swedish", "sv" },
            { "Thai", "th" }, { "Turkish", "tr" }, { "Ukrainian", "uk" }, { "Vietnamese", "vi" },
        };

        // By name, so a SystemLanguage member an older or newer engine lacks cannot break compilation.
        private static string DeviceLanguageCode() =>
            s_systemLanguageCodes.TryGetValue(Application.systemLanguage.ToString(), out var code) ? code : null;

        public void SetLanguage(string langCode)
        {
            if (string.IsNullOrEmpty(langCode)) return;

            // Invariant: on a Turkish phone ToLower turns "ID" into "ıd" (issue #187)
            string normalized = langCode.ToLowerInvariant();
            // Fast path check
            if (_currentLanguage == normalized) return;

            Action<string> handlersToInvoke = null;
            lock (_tableLock)
            {
                if (_currentLanguage == normalized) return;
                _currentLanguage = normalized;
                _culture = CultureFor(normalized);
                _rtlShaped.Clear();
                // Capture delegate snapshot while under lock to ensure a consistent view
                handlersToInvoke = OnLanguageChanged;
            }
            // Saved outside the table lock, so no GetString waits for the disk (issue #187); the save lock writes the
            // language current when it runs, so two quick switches cannot leave the older one saved.
            if (PlayerPrefsService != null)
            {
                lock (_saveLock)
                {
                    PlayerPrefsService.SetString(LanguageKey, _currentLanguage);
                    PlayerPrefsService.Save();
                }
            }

            // Invoke per-subscriber: a throwing subscriber must not silence the rest of
            // the multicast list.
            if (handlersToInvoke != null)
            {
                foreach (var d in handlersToInvoke.GetInvocationList())
                {
                    try
                    {
                        ((Action<string>)d).Invoke(_currentLanguage);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[LocalizationService] OnLanguageChanged handler threw: {ex.Message}");
                    }
                }
            }
        }

        public string GetString(string key, string fallback = "")
        {
            if (string.IsNullOrEmpty(key)) return fallback;
            string text = TryLookup(key, out var found) ? found : (!string.IsNullOrEmpty(fallback) ? fallback : key);
            return IsRTL ? ShapedForRightToLeft(text) : text;
        }

        private string ShapedForRightToLeft(string text)
        {
            string shaped;
            lock (_tableLock)
            {
                if (_rtlShaped.TryGetValue(text, out shaped)) return shaped;
            }
            shaped = FormatRTLIfNeeded(text);
            lock (_tableLock)
            {
                // shortcut: a game that passes a new fallback text on every call would grow the cache without end, so it is
                // emptied at MaxRtlShaped entries; a table of more texts than that reverses some of them again.
                if (_rtlShaped.Count >= MaxRtlShaped) _rtlShaped.Clear();
                _rtlShaped[text] = shaped;
            }
            return shaped;
        }

        public string Format(string key, params object[] args)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            string pattern = TryLookup(key, out var text) ? text : key;
            string formatted;
            try { formatted = string.Format(Culture, pattern, args ?? Array.Empty<object>()); }
            catch (FormatException ex)
            {
                // once per key, in every build: Format can run every frame, and the text shows its raw pattern (#164)
                bool first;
                lock (_tableLock) first = (_badFormatKeys ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
                if (first) NexusLog.WarnAlways($"[LocalizationService] '{key}' is not a valid format for {(args == null ? 0 : args.Length)} argument(s): {ex.Message}");
                formatted = pattern;
            }
            // Formatted before the RTL reversal, which would otherwise turn "{0}" into "}0{".
            return FormatRTLIfNeeded(formatted);
        }

        private bool TryLookup(string key, out string text)
        {
            bool english;
            string shown = null, diagnostic = null;
            // Capture current language and use it within the lock to avoid TOCTOU.
            lock (_tableLock)
            {
                text = null;
                string language = _currentLanguage;
                bool hasTable = _localizedTable.TryGetValue(language, out var dict);
                if (hasTable && dict.TryGetValue(key, out text)) return true;
                english = _localizedTable.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out text);

                // The player sees the key itself, or English in a language that has no table: in every build. One English
                // text standing in a language's table is a diagnostic (issue #187, #164).
                _reportedMisses ??= new HashSet<(string language, string key)>();
                if (!english)
                {
                    if (_reportedMisses.Add((null, key)))
                        shown = $"[LocalizationService] '{key}' has no text in {(language == "en" ? "English" : "'" + language + "' or English")}: the fallback or the key itself is shown.";
                }
                else if (!hasTable)
                {
                    if (_reportedMisses.Add((language, null)))
                        shown = $"[LocalizationService] There is no '{language}' table: English texts are shown until one is registered.";
                }
                else if (_reportedMisses.Add((language, key)))
                    diagnostic = $"[LocalizationService] '{language}' has no text for '{key}': the English text is shown.";
            }
            if (shown != null) NexusLog.WarnAlways(shown);
            if (diagnostic != null) NexusRuntime.Logger?.LogWarning(diagnostic);
            return english;
        }

        public string FormatRTLIfNeeded(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsRTL) return text;

            // By text element (grapheme cluster), so surrogate pairs (emoji, CJK ext) and combining marks stay whole.
            int[] elements = StringInfo.ParseCombiningCharacters(text);
            if (elements.Length <= 1) return text;

            // Elements come out in reverse order, and a run of digits or Latin text keeps its own: a score of 125 read 521
            // (issue #187). shortcut: not the Unicode bidi algorithm (UAX #9): brackets are not mirrored, a sign or percent at
            // a number's edge is not joined to it, and a number joined to Latin text by spaces or punctuation stays in one
            // run with it; for full bidi shaping, use a text engine that has it.
            var result = new StringBuilder(text.Length);
            for (int last = elements.Length - 1; last >= 0;)
            {
                int first = IsLeftToRight(text[elements[last]]) ? LeftToRightRunStart(text, elements, last) : last;
                int end = last + 1 < elements.Length ? elements[last + 1] : text.Length;
                result.Append(text, elements[first], end - elements[first]);
                last = first - 1;
            }
            return result.ToString();
        }

        // Latin text takes the spaces and punctuation between its parts ("Level 12"); a number alone takes only its own
        // separators ("1,250.50"), so two numbers apart read right to left, as the bidi algorithm orders them.
        private static int LeftToRightRunStart(string text, int[] elements, int last)
        {
            int first = last;
            bool latin = false;
            for (int k = last; k >= 0; k--)
            {
                char c = text[elements[k]];
                if (IsLeftToRight(c)) { first = k; latin |= char.IsLetter(c); }
                else if (IsRightToLeft(c)) break;
            }
            if (latin) return first;

            first = last;
            for (int k = last - 1; k >= 0; k--)
            {
                char c = text[elements[k]];
                if (char.IsDigit(c)) first = k;
                else if (NumberSeparators.IndexOf(c) < 0) break;
            }
            return first;
        }

        private const string NumberSeparators = ".,:/\u066B\u066C"; // with the Arabic decimal and thousands separators

        private static bool IsLeftToRight(char c) => char.IsDigit(c) || (char.IsLetter(c) && !IsRightToLeft(c));

        // Hebrew, Arabic, Syriac, Thaana, N'Ko and the Hebrew and Arabic presentation forms
        private static bool IsRightToLeft(char c) =>
            (c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF');

        /// <summary>
        /// Seeds generic UI strings (buttons and common dialog titles) for the two built-in
        /// languages, so a project gets sensible text before it registers its own tables.
        /// </summary>
        /// <remarks>
        /// Deliberately limited to framework-generic keys. Game-specific content — titles,
        /// dialogue, item names — belongs in the consuming project and is supplied through
        /// <see cref="ILocalizationTableProvider"/> or <see cref="RegisterLanguageTable"/>;
        /// a reusable package must not ship one game's copy to every other game. Keys
        /// registered by the project win, because these defaults are only applied to
        /// languages that have no table yet, and per-key overrides merge on top.
        /// </remarks>
        private void BuildLocalizationDictionary()
        {
            lock (_tableLock)
            {
                if (!_localizedTable.ContainsKey("en"))
                {
                    _localizedTable["en"] = new Dictionary<string, string>
                    {
                        { "btn_undo", "Undo" },
                        { "btn_ok", "OK" },
                        { "btn_cancel", "Cancel" },
                        { "btn_play", "Play" },
                        { "btn_retry", "Retry" },
                        { "btn_close", "Close" },
                        { "btn_settings", "Settings" },
                        { "win_title", "Level Completed!" },
                        { "fail_title", "Game Over!" }
                    };
                }

                if (!_localizedTable.ContainsKey("tr"))
                {
                    _localizedTable["tr"] = new Dictionary<string, string>
                    {
                        { "btn_undo", "Geri Al" },
                        { "btn_ok", "Tamam" },
                        { "btn_cancel", "İptal" },
                        { "btn_play", "Oyna" },
                        { "btn_retry", "Tekrar Dene" },
                        { "btn_close", "Kapat" },
                        { "btn_settings", "Ayarlar" },
                        { "win_title", "Bölüm Tamamlandı!" },
                        { "fail_title", "Oyun Bitti!" }
                    };
                }
            }
        }

        private void LoadExternalTables()
        {
            if (TableProvider == null) return;

            // The provider enumerates its own languages (default implementation yields the
            // built-in "en"/"tr" pair); a misbehaving provider falls back to that pair so
            // the core languages always load.
            IEnumerable<string> languages = null;
            try
            {
                languages = TableProvider.GetAvailableLanguages();
            }
            catch (Exception ex)
            {
                NexusLog.WarnAlways($"[LocalizationService] GetAvailableLanguages threw: {ex.Message}. Falling back to en/tr.");
            }
            languages ??= new[] { "en", "tr" };

            foreach (var lang in languages)
            {
                if (string.IsNullOrEmpty(lang)) continue;
                IDictionary<string, string> table;
                bool found;
                // one malformed table is skipped, not the whole start (#314 R3-09)
                try { found = TableProvider.TryGetTable(lang, out table); }
                catch (Exception ex)
                {
                    NexusLog.WarnAlways($"[LocalizationService] The table of '{lang}' could not be read and is skipped: {ex.Message}");
                    continue;
                }
                if (found && table != null)
                {
                    RegisterLanguageTable(lang, table);
                }
            }
        }

        public void RegisterLanguageTable(string langCode, IDictionary<string, string> dictionary)
        {
            if (string.IsNullOrEmpty(langCode) || dictionary == null) return;
            string key = langCode.ToLowerInvariant();
            lock (_tableLock)
            {
                _suppliedLanguages.Add(key);
                if (!_localizedTable.TryGetValue(key, out var table))
                {
                    table = new Dictionary<string, string>();
                    _localizedTable[key] = table;
                }

                foreach (var kvp in dictionary)
                {
                    table[kvp.Key] = kvp.Value;
                }
            }
        }

        public void RegisterKey(string langCode, string key, string value)
        {
            if (string.IsNullOrEmpty(langCode) || string.IsNullOrEmpty(key)) return;
            string langKey = langCode.ToLowerInvariant();
            lock (_tableLock)
            {
                _suppliedLanguages.Add(langKey);
                if (!_localizedTable.TryGetValue(langKey, out var table))
                {
                    table = new Dictionary<string, string>();
                    _localizedTable[langKey] = table;
                }
                table[key] = value;
            }
        }
    }
}
