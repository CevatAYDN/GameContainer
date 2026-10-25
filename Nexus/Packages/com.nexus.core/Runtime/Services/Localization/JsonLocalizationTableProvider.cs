using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Supplies a game's tables to <see cref="LocalizationService"/>: one flat <c>{"key": "text"}</c> JSON per
    /// language. Values that are not strings are skipped. Bind it with
    /// <c>builder.BindInstance&lt;ILocalizationTableProvider&gt;(JsonLocalizationTableProvider.FromResources("Localization", "en", "tr"))</c>.
    /// </summary>
    [Preserve]
    public sealed class JsonLocalizationTableProvider : ILocalizationTableProvider
    {
        private readonly IReadOnlyDictionary<string, string> _jsonByLanguage;

        /// <param name="jsonByLanguage">The JSON text of each language's table, keyed by language code.</param>
        public JsonLocalizationTableProvider(IReadOnlyDictionary<string, string> jsonByLanguage)
        {
            _jsonByLanguage = jsonByLanguage ?? throw new ArgumentNullException(nameof(jsonByLanguage));
        }

        /// <summary>Loads <c>Resources/{folder}/{language}.json</c> for each language; a missing table throws.</summary>
        public static JsonLocalizationTableProvider FromResources(string folder, params string[] languages)
        {
            if (languages == null || languages.Length == 0) throw new ArgumentException("At least one language is required.", nameof(languages));
            var tables = new Dictionary<string, string>(languages.Length);
            foreach (var language in languages)
            {
                var path = string.IsNullOrEmpty(folder) ? language : folder.TrimEnd('/') + "/" + language;
                var asset = Resources.Load<TextAsset>(path);
                if (asset == null) throw new InvalidOperationException($"[Nexus] Localization table 'Resources/{path}.json' was not found.");
                tables[language] = asset.text;
            }
            return new JsonLocalizationTableProvider(tables);
        }

        public IEnumerable<string> GetAvailableLanguages() => _jsonByLanguage.Keys;

        public bool TryGetTable(string langCode, out IDictionary<string, string> table)
        {
            table = null;
            if (langCode == null || !_jsonByLanguage.TryGetValue(langCode, out var json)) return false;
            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var document = JsonDocument.Parse(json))
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String) parsed[property.Name] = property.Value.GetString();
            }
            table = parsed;
            return true;
        }
    }
}
