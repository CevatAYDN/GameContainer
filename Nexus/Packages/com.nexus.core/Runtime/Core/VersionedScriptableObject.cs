using System;
using System.Collections.Generic;
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
    /// A new instance (<c>CreateInstance</c>, a new asset) starts at <see cref="CurrentVersion"/> and never runs
    /// <see cref="Migrate"/>: its field defaults are already the current layout. Only data Unity deserialized (an asset,
    /// an <c>Instantiate</c> copy) migrates (issue #297). A subclass that needs the serialization callbacks overrides
    /// <see cref="OnBeforeSerialize"/> and <see cref="OnAfterDeserialize"/>; one that implements
    /// <see cref="ISerializationCallbackReceiver"/> itself hides this class's hook, so none of its instances is taken for
    /// new: each migrates from its stored version as before #297, and an error names the type once.
    /// </summary>
    public abstract class VersionedScriptableObject : ScriptableObject, ISerializationCallbackReceiver
    {
        [SerializeField, HideInInspector]
        private int _version = 0;

        // Guards against re-running migration for the same loaded instance: OnEnable and
        // OnValidate can both fire in the Editor, and a domain reload re-invokes OnEnable.
        [System.NonSerialized]
        private bool _migrationChecked;

        // Set when Unity deserializes this instance, which it does before OnEnable. A new instance is not deserialized:
        // it took the field initializer's 0 and ran Migrate(0) over the current layout's defaults (issue #297).
        [System.NonSerialized]
        private bool _deserialized;

        void ISerializationCallbackReceiver.OnBeforeSerialize() => OnBeforeSerialize();

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            _deserialized = true;
            OnAfterDeserialize();
        }

        /// <summary>Unity's <see cref="ISerializationCallbackReceiver.OnBeforeSerialize"/> for a subclass.</summary>
        protected virtual void OnBeforeSerialize() { }

        /// <summary>Unity's <see cref="ISerializationCallbackReceiver.OnAfterDeserialize"/> for a subclass.</summary>
        protected virtual void OnAfterDeserialize() { }

        // Per type: true when the type re-implements ISerializationCallbackReceiver.OnAfterDeserialize, which hides the
        // hook above, so _deserialized never says "loaded" (#297). OnEnable runs on the main thread; the lock is insurance.
        internal static readonly Dictionary<Type, bool> s_deserializeHookHidden = new Dictionary<Type, bool>();

        private bool DeserializeHookHidden()
        {
            Type type = GetType();
            lock (s_deserializeHookHidden)
            {
                if (s_deserializeHookHidden.TryGetValue(type, out bool hidden)) return hidden;
                var map = type.GetInterfaceMap(typeof(ISerializationCallbackReceiver));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Name == nameof(ISerializationCallbackReceiver.OnAfterDeserialize))
                        hidden = map.TargetMethods[i].DeclaringType != typeof(VersionedScriptableObject);
                }
                s_deserializeHookHidden[type] = hidden;
                if (hidden)
                {
                    string error = $"[Nexus] {type.Name} implements ISerializationCallbackReceiver itself, which hides VersionedScriptableObject's: " +
                        "every instance of it is migrated from its stored version, a new one too. Override OnAfterDeserialize and OnBeforeSerialize instead of implementing the interface.";
                    NexusRuntime.Logger.LogError(error); // never null: falls back to Debug.LogError
                }
                return hidden;
            }
        }

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
            // A new instance is already current (#297); a type that hides the hook cannot tell new from loaded.
            if (!_deserialized && !_migrationChecked && !DeserializeHookHidden()) _version = CurrentVersion;
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
            // Log only for named objects (persisted assets). A CreateInstance'd object starts at the current version
            // (OnEnable, #297), so it reaches here only when code lowers its version; logging unnamed ones spammed
            // test runs with "Migrated (ContextData) from version 0 to 1".
            if (!string.IsNullOrEmpty(name))
            {
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
        }

        /// <summary>
        /// Override to implement data migration logic from an older version to the current one. It runs only for data
        /// serialized at an older version, never for a new instance, whose fields already hold the current layout.
        /// </summary>
        /// <param name="fromVersion">The version being migrated from.</param>
        protected virtual void Migrate(int fromVersion)
        {
        }
    }
}
