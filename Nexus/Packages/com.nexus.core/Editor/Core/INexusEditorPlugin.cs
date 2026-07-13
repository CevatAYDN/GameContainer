using UnityEngine.UIElements;
using System.Collections.Generic;

namespace Nexus.Editor
{
    /// <summary>
    /// Contract for all Nexus editor plugins hosted inside <see cref="NexusWindow"/>.
    /// </summary>
    public interface INexusEditorPlugin
    {
        string Id { get; }
        string DisplayName { get; }
        int Order { get; }
        void Initialize(NexusWindow window);
        VisualElement CreateView();
        void OnEnable();
        void OnDisable();

        /// <summary>
        /// Called approximately every 200 ms by the window scheduler while this plugin is active.
        /// Use for lightweight periodic updates (e.g. refreshing live data labels).
        /// Heavy work should be deferred to coroutines or scheduled elements inside the view.
        /// </summary>
        void OnUpdate();

        /// <summary>
        /// Returns the list of (label, action, color) tuples to show in the context action bar
        /// while this plugin is active. Returning null or an empty list hides the bar buttons.
        /// </summary>
        IReadOnlyList<(string Label, System.Action Action, UnityEngine.Color Color)> GetContextActions();
    }

    public abstract class NexusEditorPlugin : INexusEditorPlugin
    {
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        public abstract int Order { get; }

        protected NexusWindow Window { get; private set; }

        public virtual void Initialize(NexusWindow window)
        {
            Window = window;
        }

        public abstract VisualElement CreateView();
        
        public virtual void OnEnable() {}
        public virtual void OnDisable() {}
        public virtual void OnUpdate() {}

        public virtual IReadOnlyList<(string Label, System.Action Action, UnityEngine.Color Color)> GetContextActions()
            => null;
    }
}
