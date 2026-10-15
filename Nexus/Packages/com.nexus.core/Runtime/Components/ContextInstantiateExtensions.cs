using System;
using Nexus.Core.Components;
using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Instantiates prefabs and injects scene objects through a <see cref="NexusDI"/> container or an
    /// <see cref="IContext"/>: <c>context.Instantiate(enemyPrefab, parent)</c> clones the prefab with
    /// Awake and OnEnable deferred until every component in the clone's hierarchy (inactive children
    /// included) has received its <c>[Inject]</c> members. Self-managing components are left alone:
    /// a <see cref="NexusBehaviour"/> with <see cref="NexusBehaviour.AutoInject"/> injects itself when
    /// its own context is available, and a <see cref="NexusBinding"/> on the hierarchy is marked as
    /// handled so it does not inject its targets a second time.
    /// </summary>
    public static class ContextInstantiateExtensions
    {
        /// <summary>Injects every MonoBehaviour on <paramref name="gameObject"/> and its children, inactive ones included.</summary>
        public static void InjectGameObject(this NexusDI container, GameObject gameObject)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (gameObject == null) throw new ArgumentNullException(nameof(gameObject));
            var behaviours = gameObject.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue; // missing script
                if (behaviour is NexusBinding binding) { binding.MarkInjected(); continue; }
                if (behaviour is NexusBehaviour nexusBehaviour && nexusBehaviour.AutoInject) continue;
                container.Inject(behaviour);
            }
        }

        /// <summary>Injects every MonoBehaviour on <paramref name="gameObject"/> and its children through the context's container.</summary>
        public static void InjectGameObject(this IContext context, GameObject gameObject)
            => ContainerOf(context).InjectGameObject(gameObject);

        /// <summary>Clones <paramref name="prefab"/> under <paramref name="parent"/> and injects the clone before its Awake runs.</summary>
        public static GameObject Instantiate(this NexusDI container, GameObject prefab, Transform parent = null, bool worldPositionStays = false)
        {
            GuardPrefab(container, prefab);
            return InstantiateCore(container, prefab, () => UnityEngine.Object.Instantiate(prefab, parent, worldPositionStays));
        }

        /// <summary>Clones <paramref name="prefab"/> at a position and rotation and injects the clone before its Awake runs.</summary>
        public static GameObject Instantiate(this NexusDI container, GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GuardPrefab(container, prefab);
            return InstantiateCore(container, prefab, () => UnityEngine.Object.Instantiate(prefab, position, rotation, parent));
        }

        /// <summary>Clones the GameObject of <paramref name="prefab"/>, injects it, and returns the clone's <typeparamref name="T"/>.</summary>
        public static T Instantiate<T>(this NexusDI container, T prefab, Transform parent = null, bool worldPositionStays = false) where T : Component
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            return container.Instantiate(prefab.gameObject, parent, worldPositionStays).GetComponent<T>();
        }

        /// <summary>Clones the GameObject of <paramref name="prefab"/> at a position and rotation, injects it, and returns the clone's <typeparamref name="T"/>.</summary>
        public static T Instantiate<T>(this NexusDI container, T prefab, Vector3 position, Quaternion rotation, Transform parent = null) where T : Component
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            return container.Instantiate(prefab.gameObject, position, rotation, parent).GetComponent<T>();
        }

        public static GameObject Instantiate(this IContext context, GameObject prefab, Transform parent = null, bool worldPositionStays = false)
            => ContainerOf(context).Instantiate(prefab, parent, worldPositionStays);

        public static GameObject Instantiate(this IContext context, GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
            => ContainerOf(context).Instantiate(prefab, position, rotation, parent);

        public static T Instantiate<T>(this IContext context, T prefab, Transform parent = null, bool worldPositionStays = false) where T : Component
            => ContainerOf(context).Instantiate(prefab, parent, worldPositionStays);

        public static T Instantiate<T>(this IContext context, T prefab, Vector3 position, Quaternion rotation, Transform parent = null) where T : Component
            => ContainerOf(context).Instantiate(prefab, position, rotation, parent);

        private static NexusDI ContainerOf(IContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return context.Container ?? throw new InvalidOperationException("The context has no container; it is not configured or already disposed.");
        }

        private static void GuardPrefab(NexusDI container, GameObject prefab)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
        }

        /// <summary>
        /// Deactivates the prefab while cloning so Unity does not run the clone's Awake/OnEnable before
        /// injection, then restores both the prefab's and the clone's active state. If injection throws,
        /// the clone is still activated and the exception propagates; the caller decides what to do with it.
        /// In the editor, toggling a prefab asset marks it dirty; the dirty flag is cleared when this call set it.
        /// </summary>
        private static GameObject InstantiateCore(NexusDI container, GameObject prefab, Func<GameObject> clone)
        {
            bool wasActive = prefab.activeSelf;
#if UNITY_EDITOR
            bool wasDirty = UnityEditor.EditorUtility.IsDirty(prefab);
#endif
            GameObject instance = null;
            try
            {
                if (wasActive) prefab.SetActive(false);
                instance = clone();
                container.InjectGameObject(instance);
            }
            finally
            {
                if (wasActive) prefab.SetActive(true);
#if UNITY_EDITOR
                if (wasActive && !wasDirty) UnityEditor.EditorUtility.ClearDirty(prefab);
#endif
                if (instance != null && wasActive) instance.SetActive(true);
            }
            return instance;
        }
    }
}
