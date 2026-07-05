using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Nexus.Core
{
    /// <summary>
    /// Abstract base class for <see cref="ScriptableObject"/> types that require versioned data migration.
    /// Automatically detects version changes in <c>OnValidate</c> and calls <see cref="Migrate"/>.
    /// </summary>
    public abstract class VersionedScriptableObject : ScriptableObject
    {
        [SerializeField, HideInInspector]
        private int _version = 0;

        /// <summary>The currently stored version of this object's data.</summary>
        public int Version
        {
            get => _version;
            protected set => _version = value;
        }

        /// <summary>The latest version that this class expects. Override to bump when data layout changes.</summary>
        public abstract int CurrentVersion { get; }

        /// <summary>
        /// Called in the Editor when the object is loaded or modified.
        /// If <see cref="Version"/> is less than <see cref="CurrentVersion"/>, triggers migration.
        /// </summary>
        protected virtual void OnValidate()
        {
            if (_version < CurrentVersion)
            {
                int oldVersion = _version;
                Migrate(oldVersion);
                _version = CurrentVersion;
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    EditorUtility.SetDirty(this);
                }
#endif
                if (NexusRuntime.Logger != null)
                {
                    NexusRuntime.Logger.Log($"[Nexus] Migrated {name} ({GetType().Name}) from version {oldVersion} to {_version}.");
                }
                else
                {
                    Debug.Log($"[Nexus] Migrated {name} ({GetType().Name}) from version {oldVersion} to {_version}.");
                }
            }
        }

        /// <summary>
        /// Override to implement data migration logic from an older version to the current one.
        /// </summary>
        /// <param name="fromVersion">The version being migrated from.</param>
        protected virtual void Migrate(int fromVersion)
        {
        }
    }
}
