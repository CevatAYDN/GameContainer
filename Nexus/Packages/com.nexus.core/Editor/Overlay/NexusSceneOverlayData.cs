using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// One context's visual footprint in the Scene view: the owning <see cref="Root"/>
    /// GameObject, its merged hierarchy bounds ("context boundary"), and its live
    /// signal wiring (signal → command links) when a context is bound.
    /// </summary>
    public sealed class NexusSceneOverlayContextNode
    {
        /// <summary>Live context (non-null only in Play Mode after <see cref="Root.Awake"/>).</summary>
        public IContext Context;

        /// <summary>The Root MonoBehaviour this node was built from. Never null.</summary>
        public Root Root;

        public GameObject GameObject => Root != null ? Root.gameObject : null;

        /// <summary>Scope tag from the live context, the serialized ContextData, or the GameObject name.</summary>
        public string ScopeTag;

        /// <summary>Merged renderer/collider bounds of the Root's GameObject hierarchy (unit box fallback).</summary>
        public Bounds Bounds;

        /// <summary>Parent context's scope tag (hierarchy link target), or null for root contexts.</summary>
        public string ParentScopeTag;

        /// <summary>Parent Root's GameObject, used to draw the context-hierarchy link line.</summary>
        public GameObject ParentGameObject;

        /// <summary>Index into <see cref="NexusSceneOverlayData.Palette"/> (stable per scope tag).</summary>
        public int ColorIndex;

        /// <summary>Registered signal → command links (empty in Edit Mode, no live registrations yet).</summary>
        public List<NexusSceneOverlaySignalLink> Links;
    }

    /// <summary>A registered signal → command handler mapping, drawn as a labeled link.</summary>
    public sealed class NexusSceneOverlaySignalLink
    {
        public string SignalName;
        public string HandlerName;
        public ExecutionMode Mode;
        /// <summary>True when the signal type carries <see cref="CrossContextAttribute"/> (drawn as an inter-context link).</summary>
        public bool IsCrossContext;
        /// <summary>Optional [CrossContext] scope restriction; null/empty = broadcast to all other contexts.</summary>
        public string CrossContextScope;
        public int Priority;
    }

    /// <summary>
    /// Pure data collection for the Nexus Scene-view overlay. Kept free of any drawing or
    /// SceneView/Handles API so the mapping (Root GameObject → context boundary, context →
    /// signal links) is unit-testable in Edit Mode.
    /// </summary>
    public static class NexusSceneOverlayData
    {
        /// <summary>
        /// Distinct colors (readable on the default dark Scene view background), assigned
        /// round-robin by the node's scope tag hash. Palette order is part of the drawing
        /// contract — tests assert index ranges, not specific colors.
        /// </summary>
        public static readonly Color[] Palette =
        {
            new(0.31f, 0.76f, 0.97f), // light blue
            new(0.51f, 0.78f, 0.52f), // green
            new(1.00f, 0.72f, 0.30f), // orange
            new(0.90f, 0.45f, 0.45f), // red
            new(0.73f, 0.41f, 0.78f), // purple
            new(1.00f, 0.95f, 0.46f), // yellow
            new(0.30f, 0.82f, 0.88f), // cyan
            new(0.94f, 0.38f, 0.57f), // pink
        };

        /// <summary>
        /// Merged world-space bounds of the Root GameObject's active hierarchy: every
        /// Renderer bounds plus every Collider bounds. Falls back to a unit box centered on
        /// the Root's transform when the hierarchy has no geometry (pure wiring nodes).
        /// </summary>
        public static Bounds ComputeHierarchyBounds(GameObject root)
        {
            if (root == null) return new Bounds(Vector3.zero, Vector3.one);

            var bounds = new Bounds(root.transform.position, Vector3.zero);
            bool hasAny = false;

            var renderers = root.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.bounds.size == Vector3.zero) continue;
                if (!hasAny) { bounds = r.bounds; hasAny = true; }
                else bounds.Encapsulate(r.bounds);
            }

            var colliders = root.GetComponentsInChildren<Collider>(false);
            for (int i = 0; i < colliders.Length; i++)
            {
                var c = colliders[i];
                if (c == null) continue;
                if (!hasAny) { bounds = c.bounds; hasAny = true; }
                else bounds.Encapsulate(c.bounds);
            }

            if (!hasAny) bounds = new Bounds(root.transform.position, Vector3.one);
            return bounds;
        }

        /// <summary>
        /// All registered signal → command handler links on a bus, sorted deterministically
        /// (signal name, then handler name) so the overlay and tests see a stable order.
        /// A signal type carrying <see cref="CrossContextAttribute"/> is flagged for the
        /// inter-context rendering pass.
        /// </summary>
        public static List<NexusSceneOverlaySignalLink> CollectSignalLinks(ISignalBusIntrospection bus)
        {
            var links = new List<NexusSceneOverlaySignalLink>();
            if (bus?.RegisteredHandlers == null) return links;

            foreach (var kvp in bus.RegisteredHandlers)
            {
                var signalType = kvp.Key;
                if (signalType == null) continue;
                var crossContext = signalType.GetCustomAttribute<CrossContextAttribute>();

                var handlers = kvp.Value;
                if (handlers == null) continue;
                for (int i = 0; i < handlers.Count; i++)
                {
                    var handler = handlers[i];
                    if (handler == null) continue;
                    links.Add(new NexusSceneOverlaySignalLink
                    {
                        SignalName = signalType.Name,
                        HandlerName = handler.CommandType != null ? handler.CommandType.Name : "(unknown)",
                        Mode = handler.Mode,
                        IsCrossContext = crossContext != null,
                        CrossContextScope = crossContext?.ScopeTag,
                        Priority = handler.Priority,
                    });
                }
            }

            links.Sort(static (a, b) =>
            {
                int c = string.CompareOrdinal(a.SignalName, b.SignalName);
                if (c != 0) return c;
                return string.CompareOrdinal(a.HandlerName, b.HandlerName);
            });
            return links;
        }

        /// <summary>
        /// Builds the overlay model from every <see cref="Root"/> found in the scene.
        /// In Play Mode the live context (Root.Context) drives the scope tag, parent link,
        /// and signal links; in Edit Mode the serialized ContextData is used for the
        /// boundary preview (no links — nothing is registered yet).
        /// </summary>
        public static List<NexusSceneOverlayContextNode> CollectContextNodes(IReadOnlyList<Root> roots)
        {
            var nodes = new List<NexusSceneOverlayContextNode>();
            if (roots == null) return nodes;

            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (root == null || root.gameObject == null) continue;

                var live = root.Context;
                var data = root.ContextData;

                string scope = live?.ScopeTag;
                if (string.IsNullOrEmpty(scope) && data != null) scope = data.ScopeTag;
                if (string.IsNullOrEmpty(scope)) scope = root.gameObject.name;

                string parentScope = live?.Parent?.ScopeTag;
                GameObject parentGo = null;
                var parentRoot = root.ParentRoot;
                if (parentRoot != null)
                {
                    parentGo = parentRoot.gameObject;
                    if (string.IsNullOrEmpty(parentScope) && parentRoot.ContextData != null)
                        parentScope = parentRoot.ContextData.ScopeTag;
                }

                List<NexusSceneOverlaySignalLink> links = null;
                if (live != null && live.SignalBus != null)
                {
                    try { links = CollectSignalLinks(live.SignalBus); }
                    catch (Exception)
                    {
                        // Best-effort: a context torn down between the root snapshot and this
                        // read must never crash the overlay refresh.
                        links = new List<NexusSceneOverlaySignalLink>();
                    }
                }
                links ??= new List<NexusSceneOverlaySignalLink>();

                nodes.Add(new NexusSceneOverlayContextNode
                {
                    Context = live,
                    Root = root,
                    ScopeTag = scope,
                    Bounds = ComputeHierarchyBounds(root.gameObject),
                    ParentScopeTag = parentScope,
                    ParentGameObject = parentGo,
                    // Mask the sign instead of Mathf.Abs: Abs(int.MinValue) throws.
                    ColorIndex = (scope.GetHashCode() & int.MaxValue) % Palette.Length,
                    Links = links,
                });
            }

            return nodes;
        }
    }
}
