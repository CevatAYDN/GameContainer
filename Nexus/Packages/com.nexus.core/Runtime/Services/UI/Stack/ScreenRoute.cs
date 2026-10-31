using System;

namespace Nexus.Core.Services
{
    /// <summary>
    /// One entry of the screen stack's history: the screen's type and the arguments it was opened with (ADR-0003).
    /// Two routes are equal when they name the same type and their arguments are equal.
    /// </summary>
    public readonly struct ScreenRoute : IEquatable<ScreenRoute>
    {
        public readonly Type Screen;
        public readonly object Args;

        public ScreenRoute(Type screen, object args = null)
        {
            Screen = screen;
            Args = args;
        }

        public bool Equals(ScreenRoute other) => Screen == other.Screen && Equals(Args, other.Args);
        public override bool Equals(object obj) => obj is ScreenRoute other && Equals(other);
        public override int GetHashCode() => ((Screen?.GetHashCode() ?? 0) * 397) ^ (Args?.GetHashCode() ?? 0);
        public static bool operator ==(ScreenRoute left, ScreenRoute right) => left.Equals(right);
        public static bool operator !=(ScreenRoute left, ScreenRoute right) => !left.Equals(right);
        public override string ToString() => Screen == null ? "(none)" : Args == null ? Screen.Name : Screen.Name + "(" + Args + ")";
    }

    /// <summary>What one back press did to the screen stack.</summary>
    public enum BackResult
    {
        /// <summary>The top entry was taken off the history.</summary>
        Popped,
        /// <summary>The top screen's own back handler took the press; the history is unchanged.</summary>
        HandledByScreen,
        /// <summary>The top screen blocks back; the history is unchanged.</summary>
        Blocked,
        /// <summary>One entry or none: nothing to go back to.</summary>
        AtRoot
    }
}
