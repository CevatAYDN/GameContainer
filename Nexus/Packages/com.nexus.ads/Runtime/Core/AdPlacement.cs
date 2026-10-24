using System;

namespace Nexus.Ads
{
    /// <summary>
    /// One place in the game where an ad can show. Game code names it through the generated
    /// <c>AdPlacements</c> constants; a name that is not declared is blocked
    /// (<see cref="PolicyRules.UnknownPlacement"/>). Caps and cooldowns here are per placement and
    /// come on top of the <see cref="AdPolicy"/> floors; a value of 0 means "no cap".
    /// </summary>
    public sealed class AdPlacement
    {
        public string Name { get; }
        public AdFormat Format { get; }
        /// <summary>The placement sits at a natural pause of play (level end, screen change). Interstitials show nowhere else.</summary>
        public bool NaturalBreak { get; }
        /// <summary>Seconds between two shows of this placement.</summary>
        public double CooldownSeconds { get; }
        /// <summary>Shows of this placement per session; 0 = no cap.</summary>
        public int SessionCap { get; }
        /// <summary>Shows of this placement per UTC day; 0 = no cap.</summary>
        public int DayCap { get; }
        /// <summary>Seconds after session start before the first show of this placement.</summary>
        public double FirstShowAfterSeconds { get; }

        public AdPlacement(string name, AdFormat format, bool naturalBreak = false, double cooldownSeconds = 0,
            int sessionCap = 0, int dayCap = 0, double firstShowAfterSeconds = 0)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A placement needs a name.", nameof(name));
            if (cooldownSeconds < 0 || sessionCap < 0 || dayCap < 0 || firstShowAfterSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(name), "Placement '" + name + "': caps and delays cannot be negative.");
            Name = name.Trim();
            Format = format;
            NaturalBreak = naturalBreak;
            CooldownSeconds = cooldownSeconds;
            SessionCap = sessionCap;
            DayCap = dayCap;
            FirstShowAfterSeconds = firstShowAfterSeconds;
        }
    }
}
