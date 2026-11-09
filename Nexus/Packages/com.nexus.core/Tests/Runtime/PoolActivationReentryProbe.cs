using System;
using UnityEngine;

namespace Nexus.Tests.Runtime
{
    public sealed class PoolActivationReentryProbe : MonoBehaviour
    {
        public Action Activated;
        private void OnEnable() => Activated?.Invoke();
    }
}
