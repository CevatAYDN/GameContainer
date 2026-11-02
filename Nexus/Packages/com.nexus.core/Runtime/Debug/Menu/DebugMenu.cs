using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The development menu (#270). In the editor's play mode and in development players it draws a UI Toolkit overlay
    /// (<see cref="DebugMenuOverlay"/>) that three fingers on a touchscreen or the Backquote key (left of 1) open and close;
    /// in a release player it creates nothing and its calls store nothing. Bind it once, in the root context: each instance
    /// draws its own menu.
    /// </summary>
    [Preserve]
    public sealed class DebugMenu : NexusService<IDebugMenu>, IDebugMenu
    {
        private static readonly IDisposable Nothing = new Removal(null, null);

        private readonly List<DebugMenuEntry> _entries = new List<DebugMenuEntry>();
        private GameObject _host;

        internal IReadOnlyList<DebugMenuEntry> Entries => _entries;
        internal DebugMenuOverlay Overlay { get; private set; }

        /// <summary>Raised when an entry is added or removed.</summary>
        internal event Action Changed;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            // Edit mode runs no Update, and an object made there would stay in the open scene.
            if (!NexusBuild.IsDevelopment || !Application.isPlaying) return default;
            _host = new GameObject("NexusDebugMenu");
            UnityEngine.Object.DontDestroyOnLoad(_host); // the root context outlives the scenes
            Overlay = _host.AddComponent<DebugMenuOverlay>();
            Overlay.Menu = this;
            return default;
        }

        public IDisposable AddButton(string section, string label, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return Add(new DebugMenuEntry(section, label, action, null, null));
        }

        public IDisposable AddToggle(string section, string label, Func<bool> get, Action<bool> set)
        {
            if (get == null) throw new ArgumentNullException(nameof(get));
            if (set == null) throw new ArgumentNullException(nameof(set));
            return Add(new DebugMenuEntry(section, label, null, get, set));
        }

        public override void Dispose()
        {
            _entries.Clear();
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
            Overlay = null;
        }

        private IDisposable Add(DebugMenuEntry entry)
        {
            if (!NexusBuild.IsDevelopment) return Nothing;
            _entries.Add(entry);
            Changed?.Invoke();
            return new Removal(this, entry);
        }

        private void Remove(DebugMenuEntry entry)
        {
            if (_entries.Remove(entry)) Changed?.Invoke();
        }

        private sealed class Removal : IDisposable
        {
            private DebugMenu _menu;
            private readonly DebugMenuEntry _entry;

            public Removal(DebugMenu menu, DebugMenuEntry entry)
            {
                _menu = menu;
                _entry = entry;
            }

            public void Dispose()
            {
                _menu?.Remove(_entry);
                _menu = null;
            }
        }
    }

    /// <summary>A button (<see cref="Run"/>) or a toggle (<see cref="Get"/>, <see cref="Set"/>) of the development menu.</summary>
    internal sealed class DebugMenuEntry
    {
        public DebugMenuEntry(string section, string label, Action run, Func<bool> get, Action<bool> set)
        {
            if (string.IsNullOrEmpty(section)) throw new ArgumentException("A menu entry needs a section.", nameof(section));
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("A menu entry needs a label.", nameof(label));
            Section = section;
            Label = label;
            Run = run;
            Get = get;
            Set = set;
        }

        public string Section { get; }
        public string Label { get; }
        public Action Run { get; }
        public Func<bool> Get { get; }
        public Action<bool> Set { get; }
        public bool IsToggle => Get != null;
    }
}
