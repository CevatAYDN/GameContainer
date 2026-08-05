using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Nexus.Core
{
    /// <summary>
    /// Abstract base class for <see cref="ScriptableObject"/> types that require versioned data migration.
    /// Migration runs on <c>OnEnable</c> (every build target, including players) and on
    /// <c>OnValidate</c> in the Editor, so an asset that ships at an older version — loaded from a
    /// build, an AssetBundle or Addressables and never opened in the Editor — is still migrated
    /// before use. Player-side migration is in-memory only; the asset on disk is read-only there.
    /// </summary>
    public abstract class VersionedScriptableObject : ScriptableObject
    {
        [SerializeField, HideInInspector]
        private int _version = 0;

        // Guards against re-running migration for the same loaded instance: OnEnable and
        // OnValidate can both fire in the Editor, and a domain reload re-invokes OnEnable.
        [System.NonSerialized]
        private bool _migrationChecked;

        /// <summary>The currently stored version of this object's data.</summary>
        public int Version
        {
            get => _version;
            protected set => _version = value;
        }

        /// <summary>The latest version that this class expects. Override to bump when data layout changes.</summary>
        public abstract int CurrentVersion { get; }

        /// <summary>
        /// Runs on load in every build target. This is the only migration trigger that fires in
        /// a player — <c>OnValidate</c> is Editor-only, so relying on it alone left assets
        /// shipped at an older version unmigrated at runtime.
        /// </summary>
        protected virtual void OnEnable()
        {
            EnsureMigrated();
        }

        /// <summary>
        /// Called in the Editor when the object is loaded or modified.
        /// If <see cref="Version"/> is less than <see cref="CurrentVersion"/>, triggers migration.
        /// </summary>
        protected virtual void OnValidate()
        {
            // A layout change in the Editor can lower the stored version again, so the
            // once-only guard is reset here (unlike the player, where assets never change).
            _migrationChecked = false;
            EnsureMigrated();
        }

        /// <summary>
        /// Migrates this instance to <see cref="CurrentVersion"/> if it is behind. Safe to call
        /// repeatedly and from consumer code that loads assets dynamically (AssetBundles,
        /// Addressables) before reading their data.
        /// </summary>
        public void EnsureMigrated()
        {
            if (_migrationChecked) return;
            _migrationChecked = true;
            if (_version >= CurrentVersion) return;

            int oldVersion = _version;
            try
            {
                Migrate(oldVersion);
            }
            catch (System.Exception ex)
            {
                // A failed migration must be loud but must not prevent the asset from loading:
                // the version is left untouched so a later fix can retry.
                _migrationChecked = false;
                string failure = $"[Nexus] Migration of {name} ({GetType().Name}) from version {oldVersion} to {CurrentVersion} failed: {ex.Message}\n{ex.StackTrace}";
                if (NexusRuntime.Logger != null) NexusRuntime.Logger.LogError(failure);
                else Debug.LogError(failure);
                return;
            }
            _version = CurrentVersion;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorUtility.SetDirty(this);
            }
#endif
            string message = $"[Nexus] Migrated {name} ({GetType().Name}) from version {oldVersion} to {_version}.";
            if (NexusRuntime.Logger != null)
            {
                NexusRuntime.Logger.Log(message);
            }
            else
            {
                Debug.Log(message);
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
