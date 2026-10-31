using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    /// <summary>What the history needs from a screen; <c>ToolkitScreen</c> is the engine side.</summary>
    internal interface IStackScreen
    {
        UILayer Layer { get; }
        bool BlocksBack { get; }
        /// <summary>First call of a back press while the screen is on top; true = handled, nothing is popped.</summary>
        bool OnBack();
        /// <summary>The screen becomes shown, or is shown again with other arguments; completes when its transition ends.</summary>
        ValueTask Show(object args);
        /// <summary>Completes when the screen's transition ends; already complete without one.</summary>
        ValueTask Hide();
    }

    /// <summary>
    /// The screen stack's history, without the engine (ADR-0003). A screen is shown exactly while it holds the last
    /// entry of its layer, so the screen below on the same layer is hidden and screens on lower layers stay shown.
    /// The history changes first and the screens are told after, one at a time, so a handler that changes the history
    /// sees a consistent one. No static state: each context's stack has its own history.
    /// </summary>
    internal sealed class ScreenHistory
    {
        private readonly Dictionary<Type, IStackScreen> _screens = new Dictionary<Type, IStackScreen>();
        private readonly List<ScreenRoute> _entries = new List<ScreenRoute>();
        private readonly List<ScreenRoute> _shown = new List<ScreenRoute>();
        private readonly List<ValueTask> _transitions = new List<ValueTask>();
        private bool _applying;

        public ObservableProperty<ScreenRoute> Current { get; } = new ObservableProperty<ScreenRoute>();

        public int Depth => _entries.Count;

        public void Register(Type type, IStackScreen screen)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (screen == null) throw new ArgumentNullException(nameof(screen));
            if (_screens.TryGetValue(type, out var existing) && existing != screen)
                throw new InvalidOperationException($"[Nexus] A screen of type {type.Name} is already registered with this context's stack.");
            _screens[type] = screen;
        }

        /// <summary>A screen that goes away (destroyed) leaves the history; the one beneath it is shown.</summary>
        public void Unregister(Type type)
        {
            if (type == null || !_screens.Remove(type)) return;
            for (int i = _shown.Count - 1; i >= 0; i--)
                if (_shown[i].Screen == type) _shown.RemoveAt(i);
            if (RemoveAll(type)) Changed();
        }

        /// <summary>The route already on top does nothing; the same screen on top with other arguments takes them.</summary>
        public void Push(Type type, object args)
        {
            Require(type);
            var route = new ScreenRoute(type, args);
            int top = _entries.Count - 1;
            // The same route on top changes neither Current nor what is shown.
            if (top >= 0 && _entries[top].Screen == type) _entries[top] = route;
            else _entries.Add(route);
            Changed();
        }

        public void ReplaceRoot(Type type, object args)
        {
            Require(type);
            _entries.Clear();
            _entries.Add(new ScreenRoute(type, args));
            Changed();
        }

        /// <summary>Takes every entry of that screen out of the history, wherever it is.</summary>
        public void Close(Type type)
        {
            if (RemoveAll(type)) Changed();
        }

        /// <summary>The top screen's own handler first, then its block, then a pop.</summary>
        public BackResult Back()
        {
            int top = _entries.Count - 1;
            if (top < 0) return BackResult.AtRoot;
            var screen = _screens[_entries[top].Screen];
            if (screen.OnBack()) return BackResult.HandledByScreen;
            if (screen.BlocksBack) return BackResult.Blocked;
            if (top == 0) return BackResult.AtRoot;
            _entries.RemoveAt(top);
            Changed();
            return BackResult.Popped;
        }

        /// <summary>
        /// Forgets the transitions started so far, before an operation whose own transitions are awaited. A call made
        /// from a screen's handler while a change is being applied forgets nothing: the running change collects the
        /// transitions of both.
        /// </summary>
        public void DropTransitions()
        {
            if (!_applying) _transitions.Clear();
        }

        /// <summary>
        /// The transitions started since <see cref="DropTransitions"/>, as one task; already complete without any. A call
        /// made from a screen's handler gets a completed task: its screens are told after it returns, by the running
        /// change, whose task covers them.
        /// </summary>
        public ValueTask TakeTransitions()
        {
            if (_applying || _transitions.Count == 0) return default;
            if (_transitions.Count == 1)
            {
                var only = _transitions[0];
                _transitions.Clear();
                return only;
            }
            var all = new Task[_transitions.Count];
            for (int i = 0; i < all.Length; i++) all[i] = _transitions[i].AsTask();
            _transitions.Clear();
            return new ValueTask(Task.WhenAll(all));
        }

        public bool IsShown(Type type)
        {
            for (int i = 0; i < _shown.Count; i++)
                if (_shown[i].Screen == type) return true;
            return false;
        }

        private void Require(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!_screens.ContainsKey(type))
                throw new InvalidOperationException($"[Nexus] No screen of type {type.Name} is registered with this context's stack; it registers when its view binds.");
        }

        private bool RemoveAll(Type type)
        {
            bool removed = false;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Screen != type) continue;
                _entries.RemoveAt(i);
                removed = true;
            }
            return removed;
        }

        private void Changed()
        {
            Current.Value = _entries.Count == 0 ? default : _entries[_entries.Count - 1];
            if (_applying) return; // the running loop below reads the new history on its next step
            _applying = true;
            try
            {
                // One screen call per step, the shown list updated before it, the target read again after it.
                while (ApplyOne()) { }
            }
            finally { _applying = false; }
        }

        private bool ApplyOne()
        {
            for (int i = 0; i < _shown.Count; i++)
            {
                if (TargetFor(_screens[_shown[i].Screen].Layer) == _shown[i].Screen) continue;
                var hidden = _shown[i];
                _shown.RemoveAt(i);
                Track(_screens[hidden.Screen].Hide());
                return true;
            }
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var route = _entries[i];
                if (!IsLastOfItsLayer(i)) continue;
                int at = IndexOfShown(route.Screen);
                if (at >= 0 && _shown[at] == route) continue;
                if (at >= 0) _shown[at] = route; else _shown.Add(route);
                Track(_screens[route.Screen].Show(route.Args));
                return true;
            }
            return false;
        }

        private void Track(ValueTask transition)
        {
            if (!transition.IsCompletedSuccessfully) _transitions.Add(transition);
        }

        private Type TargetFor(UILayer layer)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (_screens[_entries[i].Screen].Layer == layer) return _entries[i].Screen;
            return null;
        }

        private bool IsLastOfItsLayer(int index)
        {
            var layer = _screens[_entries[index].Screen].Layer;
            for (int i = _entries.Count - 1; i > index; i--)
                if (_screens[_entries[i].Screen].Layer == layer) return false;
            return true;
        }

        private int IndexOfShown(Type type)
        {
            for (int i = 0; i < _shown.Count; i++)
                if (_shown[i].Screen == type) return i;
            return -1;
        }
    }
}
