using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    public class LocalizationService : ILocalizationService, INexusService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        public string CurrentLanguage { get; private set; } = "en";
        public event Action<string> OnLanguageChanged;
        public bool IsRTL => CurrentLanguage == "ar";

        private readonly Dictionary<string, Dictionary<string, string>> _localizedTable = new Dictionary<string, Dictionary<string, string>>();

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            LoadSavedLanguage();
            BuildLocalizationDictionary();
            return default;
        }

        public void OnDispose()
        {
            _localizedTable.Clear();
        }

        private void LoadSavedLanguage()
        {
            if (PlayerPrefsService != null)
            {
                CurrentLanguage = PlayerPrefsService.GetString("NT_Language", "en");
            }
        }

        public void SetLanguage(string langCode)
        {
            if (string.IsNullOrEmpty(langCode) || CurrentLanguage == langCode) return;
            CurrentLanguage = langCode.ToLower();
            if (PlayerPrefsService != null)
            {
                PlayerPrefsService.SetString("NT_Language", CurrentLanguage);
                PlayerPrefsService.Save();
            }
            OnLanguageChanged?.Invoke(CurrentLanguage);
        }

        public string GetString(string key, string fallback = "")
        {
            if (string.IsNullOrEmpty(key)) return fallback;

            if (_localizedTable.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var val))
            {
                return FormatRTLIfNeeded(val);
            }
            if (_localizedTable.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enVal))
            {
                return FormatRTLIfNeeded(enVal);
            }
            return FormatRTLIfNeeded(!string.IsNullOrEmpty(fallback) ? fallback : key);
        }

        public string FormatRTLIfNeeded(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsRTL) return text;
            char[] chars = text.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        private void BuildLocalizationDictionary()
        {
            // EN table
            var en = new Dictionary<string, string>
            {
                { "app_name", "Neon Transit" },
                { "btn_play", "Play" },
                { "btn_undo", "Undo" },
                { "btn_redo", "Redo" },
                { "btn_viaduct", "Viaduct" },
                { "btn_hint", "Hint" },
                { "btn_hub", "Return to Hub" },
                { "crisis_title", "Traffic Crisis! 🚨" },
                { "win_title", "Level Completed! 🎉" },
                { "daily_contracts", "Daily Contracts" },
                { "overclock_title", "Rush Hour (Overclock)" },
                { "welcome_offline", "Welcome Back!" },
                { "collect_tax", "Collect Tax" },
                { "hud_score_format", "SCORE: {0}" },
                { "hud_hint_count_format", "HINT ({0})" },
                { "hud_simulation_timer_format", "Simulation: {0:F1}s" },
                { "level_completed_title", "CONGRATULATIONS!" },
                { "level_completed_score_format", "Score: {0}" },
                { "level_completed_stars_label", "Stars" },
                { "crisis_desc", "Place a viaduct to resolve collision!" },
                { "crisis_viaducts_format", "Viaducts Left: {0}" },
                { "crisis_viaduct_btn", "Use Viaduct" },
                { "crisis_undo_btn", "Undo / Revert" },
                { "crisis_exhausted_msg", "Out of viaducts!" }
            };

            // TR table
            var tr = new Dictionary<string, string>
            {
                { "app_name", "Neon Transit" },
                { "btn_play", "Oyna" },
                { "btn_undo", "Geri Al" },
                { "btn_redo", "İleri Al" },
                { "btn_viaduct", "Viyadük" },
                { "btn_hint", "İpucu" },
                { "btn_hub", "Şehre Dön" },
                { "crisis_title", "Trafik Krizi! 🚨" },
                { "win_title", "Bölüm Tamamlandı! 🎉" },
                { "daily_contracts", "Günlük Kontratlar" },
                { "overclock_title", "Yoğun Saat (Overclock)" },
                { "welcome_offline", "Tekrar Hoş Geldin!" },
                { "collect_tax", "Vergi Topla" },
                { "hud_score_format", "SKOR: {0}" },
                { "hud_hint_count_format", "İPUCU ({0})" },
                { "hud_simulation_timer_format", "Simülasyon: {0:F1}s" },
                { "level_completed_title", "TEBRİKLER!" },
                { "level_completed_score_format", "Skor: {0}" },
                { "level_completed_stars_label", "Yıldız" },
                { "crisis_desc", "Çarpışmayı çözmek için viyadük yerleştirin!" },
                { "crisis_viaducts_format", "Kalan Viyadük: {0}" },
                { "crisis_viaduct_btn", "Viyadük Kullan" },
                { "crisis_undo_btn", "Geri Al / Vazgeç" },
                { "crisis_exhausted_msg", "Viyadük hakkınız bitti!" }
            };

            _localizedTable["en"] = en;
            _localizedTable["tr"] = tr;
        }
    }
}
