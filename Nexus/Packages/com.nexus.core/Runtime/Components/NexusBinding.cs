using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Components
{
    /// <summary>
    /// Determines the target scope for automatic dependency injection.
    /// </summary>
    public enum InjectionScope
    {
        /// <summary>Injects only components on the current GameObject.</summary>
        Self = 0,
        /// <summary>Injects components on child GameObjects.</summary>
        Children = 1,
        /// <summary>Injects components on this GameObject and all child GameObjects.</summary>
        Hierarchy = 2
    }

    /// <summary>
    /// Determines when automatic dependency injection is executed.
    /// </summary>
    public enum InjectionTime
    {
        /// <summary>Injects in MonoBehaviour Awake.</summary>
        Awake = 0,
        /// <summary>Injects in MonoBehaviour Start.</summary>
        Start = 1
    }

    /// <summary>
    /// Attach to scene GameObjects or Prefabs to automatically trigger Nexus dependency injection
    /// without writing manual <c>Container.Inject(gameObject)</c> calls in code.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Nexus/Nexus Binding")]
    [Preserve]
    public class NexusBinding : MonoBehaviour
    {
        [SerializeField] private InjectionScope _scope = InjectionScope.Self;
        [SerializeField] private InjectionTime _time = InjectionTime.Awake;
        [SerializeField] private MonoBehaviour[] _customTargets = System.Array.Empty<MonoBehaviour>();

        private bool _hasInjected;
        private IContext _requestedContext;
        private bool _injecting;

        /// <summary>Gets or sets the injection scope.</summary>
        public InjectionScope Scope { get => _scope; set => _scope = value; }

        /// <summary>Gets or sets the injection trigger time.</summary>
        public InjectionTime Time { get => _time; set => _time = value; }

        private void Awake()
        {
            if (_time == InjectionTime.Awake)
            {
                InjectNow();
            }
        }

        private void Start()
        {
            if (_time == InjectionTime.Start)
            {
                InjectNow();
            }
        }

        private void OnDestroy()
        {
            NexusRuntime.OnContextConfigured -= OnContextConfigured;
        }

        /// <summary>
        /// Manually triggers dependency injection on target components using the specified or active context.
        /// Safe to call multiple times (injection is executed once).
        /// </summary>
        public void InjectNow(IContext context = null)
        {
            if (_hasInjected || _injecting) return;

            if (context != null) _requestedContext = context;
            IContext targetContext = _requestedContext ?? FindActiveContext();
            if (!ContextAvailability.CanInject(targetContext))
            {
                // Fall back to waiting for a context to register if scene initialization order varies
                NexusRuntime.OnContextConfigured -= OnContextConfigured;
                NexusRuntime.OnContextConfigured += OnContextConfigured;
                return;
            }

            NexusRuntime.OnContextConfigured -= OnContextConfigured;
            _injecting = true;
            try { PerformInjection(targetContext); }
            catch
            {
                if (this != null) NexusRuntime.OnContextConfigured += OnContextConfigured;
                throw;
            }
            finally { _injecting = false; }
        }

        private void OnContextConfigured(IContext context)
        {
            if (_hasInjected) return;
            InjectNow();
        }

        private void PerformInjection(IContext context)
        {
            if (_hasInjected || context == null) return;
            var container = context.Container;
            if (container == null) return;

            if (_customTargets != null && _customTargets.Length > 0)
            {
                for (int i = 0; i < _customTargets.Length; i++)
                {
                    if (_customTargets[i] != null)
                        container.Inject(_customTargets[i]);
                }
            }
            else
            {
                switch (_scope)
                {
                    case InjectionScope.Self:
                        var selfComponents = GetComponents<MonoBehaviour>();
                        for (int i = 0; i < selfComponents.Length; i++)
                        {
                            if (selfComponents[i] != null && selfComponents[i] != this)
                                container.Inject(selfComponents[i]);
                        }
                        break;

                    case InjectionScope.Children:
                        var childComponents = GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
                        for (int i = 0; i < childComponents.Length; i++)
                        {
                            if (childComponents[i] != null && childComponents[i].gameObject != gameObject)
                                container.Inject(childComponents[i]);
                        }
                        break;

                    case InjectionScope.Hierarchy:
                        var allComponents = GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
                        for (int i = 0; i < allComponents.Length; i++)
                        {
                            if (allComponents[i] != null && allComponents[i] != this)
                                container.Inject(allComponents[i]);
                        }
                        break;
                }
            }

            _hasInjected = true;
        }

        private IContext FindActiveContext() => ContextAvailability.Find(this);
    }
}
