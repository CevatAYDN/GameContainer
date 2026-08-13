using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Asset-loading seam (studio evaluation P0: Addressables integration point).
    ///
    /// The framework itself stays dependency-free: <see cref="ResourcesAssetLoadService"/>
    /// is the shipped default (Resources API). Teams using Addressables bind their own
    /// implementation — see <c>docs/ADDRESSABLES_ADAPTER.md</c> for a ready-to-paste
    /// <c>AddressablesAssetLoadService</c> adapter that satisfies this same interface
    /// (async handle + <c>Addressables.Release</c> instead of no-op release).
    ///
    /// Bind in lifecycle:
    /// <code>builder.BindService&lt;IAssetLoadService, ResourcesAssetLoadService&gt;();</code>
    /// </summary>
    [Preserve]
    public interface IAssetLoadService
    {
        /// <summary>
        /// Loads an asset asynchronously. Throws <see cref="System.InvalidOperationException"/>
        /// when <paramref name="key"/> does not resolve to an asset of type <typeparamref name="T"/>.
        /// </summary>
        Task<T> LoadAsync<T>(string key, CancellationToken ct = default) where T : Object;

        /// <summary>Loads an asset synchronously (blocking — never call from a hot path).</summary>
        T LoadSync<T>(string key) where T : Object;

        /// <summary>
        /// Releases the loaded asset. The Resources default is a documented no-op (Resources
        /// has no per-key unload); the Addressables adapter releases the handle here.
        /// </summary>
        void Release(string key);
    }
}
