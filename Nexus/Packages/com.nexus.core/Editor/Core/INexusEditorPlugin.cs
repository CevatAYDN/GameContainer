using UnityEngine.UIElements;

namespace Nexus.Editor
{
    public interface INexusEditorPlugin
    {
        string Id { get; }
        string DisplayName { get; }
        int Order { get; }
        void Initialize(NexusWindow window);
        VisualElement CreateView();
        void OnEnable();
        void OnDisable();
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
    }
}
