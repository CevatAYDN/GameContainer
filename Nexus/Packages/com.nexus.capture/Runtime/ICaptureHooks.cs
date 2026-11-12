using System.Collections.Generic;

namespace Nexus.Capture
{
    /// <summary>
    /// Game-specific hooks that Capture Mode triggers during promo video capture sessions.
    /// </summary>
    public interface ICaptureHooks
    {
        /// <summary>Gets the display name of the game for capture metadata.</summary>
        string GameName { get; }

        /// <summary>Enables or disables god mode or invulnerability in the game.</summary>
        void SetGodMode(bool on);

        /// <summary>Gets the total number of playable levels available for jumping.</summary>
        int LevelCount { get; }

        /// <summary>Jumps directly to the specified level index.</summary>
        void JumpToLevel(int index);

        /// <summary>Gets the list of available scenario or state presets.</summary>
        IReadOnlyList<string> Presets { get; }

        /// <summary>Applies the preset at the specified index.</summary>
        void ApplyPreset(int index);
    }
}
