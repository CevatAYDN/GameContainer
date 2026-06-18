using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Editor window that visualizes active Nexus contexts and their parent-child hierarchy.
    /// Displays context relationships, signal buses, and registered command handlers.
    /// Accessed via Window/Nexus/Context Graph.
    /// </summary>
    public class ContextGraphWindow : EditorWindow
    {
        private ScrollView _scrollView;
        private int _lastContextVersion;

        [MenuItem("Window/Nexus/Context Graph")]
        public static void ShowWindow()
        {
            var window = GetWindow<ContextGraphWindow>("Nexus Context Graph");
            window.minSize = new Vector2(400, 450);
            window.Show();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.14f));

            // Toolbar
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.paddingLeft = 10;
            toolbar.style.paddingRight = 10;
            toolbar.style.paddingTop = 8;
            toolbar.style.paddingBottom = 8;
            toolbar.style.borderBottomWidth = 1;
            toolbar.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.22f));
            toolbar.style.alignItems = Align.Center;

            var titleLabel = new Label("CONTEXT HIERARCHY GRAPH");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 12;
            titleLabel.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            titleLabel.style.marginRight = 20;
            toolbar.Add(titleLabel);

            var refreshButton = new Button(RebuildGraph) { text = "Refresh Graph" };
            refreshButton.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            refreshButton.style.borderTopLeftRadius = 4;
            refreshButton.style.borderTopRightRadius = 4;
            refreshButton.style.borderBottomLeftRadius = 4;
            refreshButton.style.borderBottomRightRadius = 4;
            refreshButton.style.color = Color.white;
            refreshButton.style.paddingLeft = 10;
            refreshButton.style.paddingRight = 10;
            toolbar.Add(refreshButton);

            root.Add(toolbar);

            // Scrollview for graph cards
            _scrollView = new ScrollView();
            _scrollView.style.flexGrow = 1;
            _scrollView.style.paddingLeft = 15;
            _scrollView.style.paddingRight = 15;
            _scrollView.style.paddingTop = 15;
            _scrollView.style.paddingBottom = 15;
            root.Add(_scrollView);

            // Schedule auto-refresh every 1.0s in Play Mode
            root.schedule.Execute(OnScheduledRefresh).Every(1000);

            RebuildGraph();
        }

        private void OnScheduledRefresh()
        {
            if (!Application.isPlaying || _scrollView == null) return;

            var activeContexts = NexusRuntime.ActiveContexts;
            int versionHash = ComputeContextVersion(activeContexts);
            if (versionHash != _lastContextVersion)
            {
                _lastContextVersion = versionHash;
                RebuildGraph();
            }
        }

        private static int ComputeContextVersion(System.Collections.Generic.IReadOnlyList<IContext> contexts)
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < contexts.Count; i++)
                {
                    if (contexts[i] is Context ctx)
                    {
                        hash = hash * 31 + (ctx.ScopeTag?.GetHashCode() ?? 0);
                        hash = hash * 31 + (ctx.Parent?.GetHashCode() ?? 0);
                        if (ctx.Container != null)
                        {
                            var singletons = ctx.Container.GetActiveSingletons();
                            int count = 0;
                            using (var e = singletons.GetEnumerator())
                            {
                                while (e.MoveNext()) count++;
                            }
                            hash = hash * 31 + count;
                        }
                    }
                }
                return hash;
            }
        }

        private void RebuildGraph()
        {
            if (_scrollView == null) return;
            _scrollView.Clear();

            var activeContexts = NexusRuntime.ActiveContexts;
            if (activeContexts == null || activeContexts.Count == 0)
            {
                var container = new VisualElement();
                container.style.paddingLeft = 15;
                container.style.paddingRight = 15;
                container.style.paddingTop = 15;
                container.style.paddingBottom = 15;
                container.style.marginTop = 10;

                NexusEditorStyles.CreateInfoCard(container, "NEXUS CONTEXT GRAPH \u2014 OFFLINE", NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBg,
                    "No active Nexus Contexts found. Enter <b>Play Mode</b> to visualize context hierarchy, parent-child relationships, and registered DI singletons.\n\n" +
                    "Each active Context appears as a card showing its <b>ScopeTag</b>, handler count, and resolved singletons. Child contexts are nested inside their parent card.");

                int sceneRootCount = CountSceneRoots();
                if (sceneRootCount > 0)
                {
                    NexusEditorStyles.CreateInfoCard(container, $"SCENE ROOTS DETECTED ({sceneRootCount})", NexusEditorStyles.AccentYellow, NexusEditorStyles.CardBgYellow,
                        $"Found <b>{sceneRootCount}</b> Root GameObject{(sceneRootCount > 1 ? "s" : "")} in the scene. " +
                        "These will become active contexts when you enter Play Mode.\n\n" +
                        "Select a Root in the Hierarchy and use the <b>Nexus Inspector</b> to inspect its signal flow.");
                }
                else
                {
                    NexusEditorStyles.CreateInfoCard(container, "NO ROOTS IN SCENE", NexusEditorStyles.AccentOrange, NexusEditorStyles.CardBgRed,
                        "Use <b>Window/Nexus/Root Wizard</b> or <b>GameObject/Nexus/Create Root</b> " +
                        "to create a Root GameObject. Each Root creates a Context in Play Mode.\n\n" +
                        "After creating a Root, enter Play Mode and return here to see the context graph.");
                }

                var actionsCard = NexusEditorStyles.CreateActionGroup(container, "QUICK ACTIONS");
                NexusEditorStyles.AddActionButton(actionsCard, "Open Root Wizard", () => RootWizard.ShowWindow(), NexusEditorStyles.BtnBlue);
                NexusEditorStyles.AddActionButton(actionsCard, "Open Nexus Inspector", () => NexusInspectorWindow.ShowWindow(), NexusEditorStyles.BtnPurple);
                NexusEditorStyles.AddActionButton(actionsCard, "Open Signal Tester", () => NexusSignalTesterWindow.ShowWindow(), NexusEditorStyles.BtnPurple);
                NexusEditorStyles.AddActionButton(actionsCard, "Open Signal Explorer", () => SignalExplorerWindow.ShowWindow(), NexusEditorStyles.BtnTeal);

                _scrollView.Add(container);
                return;
            }

            // Build dependency tree (separate roots)
            var rootContexts = new List<Context>();
            var childMap = new Dictionary<Context, List<Context>>();

            for (int i = 0; i < activeContexts.Count; i++)
            {
                if (activeContexts[i] is Context ctx)
                {
                    if (ctx.Parent == null)
                    {
                        rootContexts.Add(ctx);
                    }
                    else if (ctx.Parent is Context parentCtx)
                    {
                        if (!childMap.ContainsKey(parentCtx))
                        {
                            childMap[parentCtx] = new List<Context>();
                        }
                        childMap[parentCtx].Add(ctx);
                    }
                }
            }

            foreach (var rootCtx in rootContexts)
            {
                var card = RenderContextCard(rootCtx, childMap, 0);
                _scrollView.Add(card);
            }
        }

        private VisualElement RenderContextCard(Context ctx, Dictionary<Context, List<Context>> childMap, int depth)
        {
            var card = new VisualElement();
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            var borderColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            card.style.borderTopColor = borderColor;
            card.style.borderBottomColor = borderColor;
            card.style.borderLeftColor = borderColor;
            card.style.borderRightColor = borderColor;
            card.style.borderTopLeftRadius = 6;
            card.style.borderTopRightRadius = 6;
            card.style.borderBottomLeftRadius = 6;
            card.style.borderBottomRightRadius = 6;
            card.style.backgroundColor = new StyleColor(new Color(0.16f, 0.16f, 0.18f));
            card.style.paddingLeft = 12;
            card.style.paddingRight = 12;
            card.style.paddingTop = 10;
            card.style.paddingBottom = 10;
            card.style.marginTop = 10;
            card.style.marginBottom = 10;

            // Context Title Header
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = new StyleColor(new Color(0.22f, 0.22f, 0.25f));
            header.style.paddingBottom = 6;

            var titleContainer = new VisualElement();
            titleContainer.style.flexDirection = FlexDirection.Row;
            titleContainer.style.alignItems = Align.Center;

            var title = new Label(ctx.ScopeTag ?? "Context");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13;
            title.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            titleContainer.Add(title);

            if (ctx.Parent != null)
            {
                var parentTagText = (ctx.Parent as Context)?.ScopeTag ?? "Unknown";
                var parentTag = new Label($" (Parent: {parentTagText})");
                parentTag.style.fontSize = 10;
                parentTag.style.color = Color.gray;
                titleContainer.Add(parentTag);
            }

            int handlerCount = 0;
            if (ctx.SignalBusInternal != null)
            {
                var handlers = ctx.SignalBusInternal.CommandHandlers;
                if (handlers != null)
                {
                    foreach (var kvp in handlers)
                    {
                        if (kvp.Value != null)
                        {
                            handlerCount += kvp.Value.Count;
                        }
                    }
                }
            }

            var handlerPill = new Label($"{handlerCount} Handlers") {
                style = {
                    fontSize = 9,
                    backgroundColor = new StyleColor(new Color(0.2f, 0.35f, 0.2f)),
                    color = new StyleColor(new Color(0.6f, 1f, 0.6f)),
                    paddingLeft = 4,
                    paddingRight = 4,
                    paddingTop = 1,
                    paddingBottom = 1,
                    marginLeft = 8,
                    borderTopLeftRadius = 3,
                    borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3,
                    borderBottomRightRadius = 3,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
            titleContainer.Add(handlerPill);
            header.Add(titleContainer);

            if (ctx.ContextData != null)
            {
                var soLink = new Button(() => {
                    Selection.activeObject = ctx.ContextData;
                    EditorGUIUtility.PingObject(ctx.ContextData);
                }) { text = "Config SO ↗" };
                soLink.style.backgroundColor = new StyleColor(new Color(0.2f, 0.22f, 0.25f));
                soLink.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
                soLink.style.fontSize = 9;
                soLink.style.borderTopLeftRadius = 3;
                soLink.style.borderTopRightRadius = 3;
                soLink.style.borderBottomLeftRadius = 3;
                soLink.style.borderBottomRightRadius = 3;
                soLink.style.paddingLeft = 5;
                soLink.style.paddingRight = 5;
                soLink.style.paddingTop = 1;
                soLink.style.paddingBottom = 1;
                soLink.style.marginLeft = StyleKeyword.Auto;
                header.Add(soLink);
            }

            card.Add(header);

            // Active Singletons in DI Container
            var singletonsList = new VisualElement();
            singletonsList.style.marginTop = 8;

            var sectionTitle = new Label("Resolved DI Singletons:") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 10, color = Color.gray } };
            singletonsList.Add(sectionTitle);

            var activeSingletons = ctx.Container.GetActiveSingletons();
            int singletonCount = 0;

            foreach (var instance in activeSingletons)
            {
                if (instance == null || instance is NexusDI || instance is IContext || instance is ISignalBus) continue;

                var type = instance.GetType();
                var item = new Label($"• {type.Name}");
                item.style.fontSize = 11;
                item.style.color = Color.white;
                item.style.paddingLeft = 8;
                singletonsList.Add(item);
                singletonCount++;
            }

            if (singletonCount == 0)
            {
                var noItems = new Label("  None resolved yet.") { style = { fontSize = 10, color = new Color(0.5f, 0.5f, 0.5f) } };
                singletonsList.Add(noItems);
            }

            card.Add(singletonsList);

            // Render Child Contexts recursively inside parent
            if (childMap.TryGetValue(ctx, out var children) && children.Count > 0)
            {
                var childrenContainer = new VisualElement();
                childrenContainer.style.marginTop = 12;
                childrenContainer.style.paddingLeft = 15;
                childrenContainer.style.borderLeftWidth = 1;
                childrenContainer.style.borderLeftColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));

                var childTitle = new Label("Child Contexts:") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 10, color = Color.gray } };
                childrenContainer.Add(childTitle);

                foreach (var child in children)
                {
                    childrenContainer.Add(RenderContextCard(child, childMap, depth + 1));
                }

                card.Add(childrenContainer);
            }

            return card;
        }

        private static int CountSceneRoots()
        {
            var roots = UnityEngine.Object.FindObjectsByType<Root>();
            return roots?.Length ?? 0;
        }
    }
}
