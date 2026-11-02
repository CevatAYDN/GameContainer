using System;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The development menu (#270): buttons and toggles a game adds for its own development actions ("give 1000 coins",
    /// "god mode"), grouped by section in the order they were added. It exists in the editor and development builds; in a
    /// release player the same calls store nothing and show nothing, so game code needs no <c>#if</c>.
    /// </summary>
    public interface IDebugMenu
    {
        /// <summary>Adds a button that runs <paramref name="action"/>; disposing the handle removes it.</summary>
        IDisposable AddButton(string section, string label, Action action);

        /// <summary>
        /// Adds a toggle that shows <paramref name="get"/> each time the menu draws it and passes the player's choice to
        /// <paramref name="set"/>; disposing the handle removes it.
        /// </summary>
        IDisposable AddToggle(string section, string label, Func<bool> get, Action<bool> set);
    }
}
