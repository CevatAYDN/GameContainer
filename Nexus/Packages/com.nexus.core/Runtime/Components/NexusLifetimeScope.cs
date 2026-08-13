using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Drag-and-drop MonoBehaviour component for defining a DI lifetime scope directly in the Unity hierarchy.
    /// Provides VContainer-style LifetimeScope workflow while leveraging Nexus's zero-GC SignalBus,
    /// anti-cheat reactive models, and reverse disposal order.
    /// </summary>
    [AddComponentMenu("Nexus/Nexus Lifetime Scope")]
    [DisallowMultipleComponent]
    public class NexusLifetimeScope : Root
    {
        [Header("Hierarchy Scoping")]
        [Tooltip("Optional parent scope. If left null, Nexus auto-discovers the nearest parent scope/Root in the scene hierarchy.")]
        [SerializeField] private Root _parentScope;

        /// <summary>The parent scope or Root component in the hierarchy.</summary>
        public Root ParentScope => _parentScope;

        protected override void Awake()
        {
            // Auto-discover parent scope/Root in hierarchy if not manually assigned
            if (_parentScope == null && transform.parent != null)
            {
                _parentScope = transform.parent.GetComponentInParent<Root>();
            }

            if (_parentScope != null && _parentScope != this)
            {
                SetUp(ContextData, _parentScope, InitializationPriority);
            }

            base.Awake();
        }
    }
}
