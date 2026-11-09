using System;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Tests.Runtime
{
    public sealed class FullReviewUnityServicePrefabProbe : MonoBehaviour, IPoolable
    {
        public static Action<GameObject> Activated;
        public int SpawnCallbacks { get; private set; }

        private void OnEnable() => Activated?.Invoke(gameObject);
        public void OnSpawned() => SpawnCallbacks++;
        public void OnDespawned() { }
    }
}
