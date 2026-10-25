using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Core.Services
{
    /// <summary>
    /// How <see cref="LocalizationService"/> picks the first-run language and formats text. Optional: bind one
    /// with <c>builder.BindInstance(new LocalizationOptions { ... })</c>. Without it the first run is English and
    /// the culture comes from the language code.
    /// </summary>
    public sealed class LocalizationOptions
    {
        /// <summary>On the first run (no saved language) use the device language when a table exists for it.</summary>
        public bool UseDeviceLanguage = true;

        /// <summary>The first-run language when the device language has no table or <see cref="UseDeviceLanguage"/> is off.</summary>
        public string DefaultLanguage = "en";

        /// <summary>
        /// The culture per language code (lower case, e.g. "pt-br"), for a game that fixes its separators.
        /// A language not listed uses the specific culture of its code, or the invariant culture.
        /// </summary>
        public IDictionary<string, CultureInfo> Cultures;
    }
}
