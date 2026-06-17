using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Editor
{
    public class ContextGraphWindow : EditorWindow
    {
        private ScrollView _scrollView;

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

            RebuildGraph();
        }

        private void RebuildGraph()
        {
            _scrollView.Clear();

            var activeContexts = new List<IContext>(NexusRuntime.ActiveContexts);
            if (activeContexts.Count == 0)
            {
                var label = new Label("No active Contexts running in Play Mode.") { style = { color = Color.gray, alignSelf = Align.Center, marginTop = 40 } };
                _scrollView.Add(label);
                return;
            }

            // Build dependency tree (separate roots)
            var rootContexts = new List<Context>();
            var childMap = new Dictionary<Context, List<Context>>();

            foreach (var context in activeContexts)
            {
                if (context is Context ctx)
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

            var title = new Label(ctx.ScopeTag ?? "Context");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13;
            title.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            header.Add(title);

            if (ctx.Parent != null)
            {
                var parentTagText = (ctx.Parent as Context)?.ScopeTag ?? "Unknown";
                var parentTag = new Label($" (Parent: {parentTagText})");
                parentTag.style.fontSize = 10;
                parentTag.style.color = Color.gray;
                header.Add(parentTag);
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
    }
}
