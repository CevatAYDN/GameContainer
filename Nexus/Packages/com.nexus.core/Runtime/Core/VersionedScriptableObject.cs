using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Nexus.Core
{
    public abstract class VersionedScriptableObject : ScriptableObject
    {
        [SerializeField, HideInInspector]
        private int _version = 0;

        public int Version
        {
            get => _version;
            protected set => _version = value;
        }

        public abstract int CurrentVersion { get; }

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
                Debug.Log($"[Nexus] Migrated {name} ({GetType().Name}) from version {oldVersion} to {_version}.");
            }
        }

        protected virtual void Migrate(int fromVersion)
        {
        }
    }
}
