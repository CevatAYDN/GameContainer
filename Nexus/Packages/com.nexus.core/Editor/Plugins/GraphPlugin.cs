using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class GraphPlugin : NexusEditorPlugin, INexusTraceSink
    {
        public override string Id => "Graph";
        public override string DisplayName => "Signal Graph";
        public override int Order => 5;

        private const int MaxNodes = 50;

        private VisualElement _view;
        private SignalGraphView _graphView;
        private Label _statusLabel;

        private Dictionary<string, Node> _signalNodes = new();
        private Dictionary<string, Node> _handlerNodes = new();
        private int _totalEdgeCount;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("graph_title"));
            _view.Add(toolbar);

            // Status bar below toolbar
            _statusLabel = new Label(NexusLang.Get("graph_ready"))
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), paddingLeft = 10, paddingTop = 4, paddingBottom = 4,
                    borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor) }
            };
            _view.Add(_statusLabel);

            _graphView = new SignalGraphView();
            _graphView.style.flexGrow = 1;
            _view.Add(_graphView);

            var refreshBtn = NexusEditorStyles.CreateButton(NexusLang.Get("graph_refresh"), BuildGraph, NexusEditorStyles.BtnBlue);
            refreshBtn.style.position = Position.Absolute;
            refreshBtn.style.top = 58;
            refreshBtn.style.right = 10;
            _view.Add(refreshBtn);

            BuildGraph();
            NexusTrace.AddSink(this);

            return _view;
        }

        public override void OnDisable()
        {
            NexusTrace.RemoveSink(this);
            _signalNodes.Clear();
            _handlerNodes.Clear();
        }

        private void BuildGraph()
        {
            _graphView.ClearGraph();
            _signalNodes.Clear();
            _handlerNodes.Clear();
            _totalEdgeCount = 0;

            var runtimeMappings = CollectRuntimeMappings();
            if (runtimeMappings != null)
            {
                DrawGraph(runtimeMappings);
                return;
            }

            var attributeMappings = CollectAttributeMappings();
            DrawGraph(attributeMappings);
        }

        private static Dictionary<Type, List<Type>> CollectRuntimeMappings()
        {
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0) return null;

            var mappings = new Dictionary<Type, List<Type>>();
            foreach (var ctx in contexts)
            {
                var handlers = ctx.SignalBus.RegisteredHandlers;
                if (handlers == null || handlers.Count == 0) continue;
                foreach (var kvp in handlers)
                {
                    if (!mappings.ContainsKey(kvp.Key))
                        mappings[kvp.Key] = new List<Type>();
                    foreach (var info in kvp.Value)
                    {
                        if (info.CommandType != null && !mappings[kvp.Key].Contains(info.CommandType))
                            mappings[kvp.Key].Add(info.CommandType);
                    }
                }
            }
            return mappings;
        }

        private static Dictionary<Type, List<Type>> CollectAttributeMappings()
        {
            var mappings = new Dictionary<Type, List<Type>>();
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono") ||
                    name.StartsWith("UnityEngine") || (name.StartsWith("UnityEditor") && !name.Contains("com.nexus")))
                    continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }

                foreach (var type in types)
                {
                    if (type != null && type.IsClass && !type.IsAbstract)
                    {
                        var attrs = type.GetCustomAttributes(typeof(SignalHandlerAttribute), false);
                        foreach (SignalHandlerAttribute attr in attrs)
                        {
                            if (!mappings.ContainsKey(attr.SignalType))
                                mappings[attr.SignalType] = new List<Type>();
                            mappings[attr.SignalType].Add(type);
                        }
                    }
                }
            }
            return mappings;
        }

        private void DrawGraph(Dictionary<Type, List<Type>> mappings)
        {
            // Count total signals + commands
            int signalCount = mappings.Count;
            int cmdCount = 0;
            foreach (var kvp in mappings) cmdCount += kvp.Value.Count;
            int totalNodes = signalCount + cmdCount;

            if (mappings == null || mappings.Count == 0)
            {
                _statusLabel.text = NexusLang.Get("graph_no_mappings");
                _statusLabel.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
                return;
            }

            if (totalNodes > MaxNodes)
            {
                _graphView.ClearGraph();
                var warnLabel = new Label(
                    $"⚠ {totalNodes} nodes exceed the {MaxNodes} limit.\n" +
                    "Consider splitting your architecture into multiple smaller contexts,\n" +
                    "or use the Signal Explorer for text-based inspection.")
                {
                    style = { color = new StyleColor(NexusEditorStyles.AccentOrange), fontSize = 12,
                        alignSelf = Align.Center, marginTop = 20, whiteSpace = WhiteSpace.Normal }
                };
                _graphView.Add(warnLabel);
                _statusLabel.text = string.Format(NexusLang.Get("graph_overflow"), totalNodes, MaxNodes);
                _statusLabel.style.color = new StyleColor(NexusEditorStyles.AccentOrange);
                return;
            }

            _statusLabel.text = string.Format(NexusLang.Get("graph_stats"), signalCount, cmdCount, totalNodes, _totalEdgeCount);
            _statusLabel.style.color = new StyleColor(NexusEditorStyles.TextSecondary);

            int yOffset = 0;
            int edgeCount = 0;
            foreach (var kvp in mappings)
            {
                var signalType = kvp.Key;
                var handlerTypes = kvp.Value;

                int handlerCount = handlerTypes.Count;
                string mode = "Sequential";

                // Try to get mode from runtime registrations
                var contexts = NexusRuntime.ActiveContexts;
                if (contexts != null && contexts.Count > 0)
                {
                    var handlers = contexts[0].SignalBus.RegisteredHandlers;
                    if (handlers != null && handlers.TryGetValue(signalType, out var infos) && infos.Count > 0)
                    {
                        mode = infos[0].Mode.ToString();
                        handlerCount = infos.Count;
                    }
                }

                var signalNode = _graphView.CreateSignalNode(signalType.Name, new Vector2(100, yOffset));
                signalNode.tooltip = $"{signalType.FullName}\nHandlers: {handlerCount}\nMode: {mode}";
                _graphView.AddElement(signalNode);
                _signalNodes[signalType.Name] = signalNode;

                int handlerYOffset = yOffset;
                foreach (var handlerType in handlerTypes)
                {
                    var handlerNode = _graphView.CreateHandlerNode(handlerType.Name, new Vector2(400, handlerYOffset));
                    handlerNode.tooltip = $"{handlerType.FullName}";
                    _graphView.AddElement(handlerNode);
                    _handlerNodes[handlerType.Name] = handlerNode;

                    var edge = _graphView.ConnectNodes(
                        signalNode.outputContainer.Q<Port>(),
                        handlerNode.inputContainer.Q<Port>());
                    if (edge != null)
                    {
                        _graphView.AddElement(edge);
                        edgeCount++;
                    }

                    handlerYOffset += 100;
                }

                yOffset = Math.Max(yOffset + 150, handlerYOffset + 50);
            }
            _totalEdgeCount = edgeCount;
            _statusLabel.text = string.Format(NexusLang.Get("graph_stats"), signalCount, cmdCount, totalNodes, edgeCount);
        }

        public void Write(in TraceEvent traceEvent)
        {
            if (traceEvent.Type == TraceEventType.Signal)
            {
                if (_signalNodes.TryGetValue(traceEvent.TypeName, out var node))
                    HighlightNode(node, new Color(0.2f, 0.8f, 0.2f, 0.8f));
            }
            else if (traceEvent.Type == TraceEventType.Command)
            {
                if (_handlerNodes.TryGetValue(traceEvent.TypeName, out var node))
                    HighlightNode(node, new Color(0.2f, 0.8f, 0.8f, 0.8f));
            }
        }

        private void HighlightNode(Node node, Color flashColor)
        {
            var origColor = node.mainContainer.style.backgroundColor;
            node.mainContainer.style.backgroundColor = new StyleColor(flashColor);
            node.schedule.Execute(() => {
                node.mainContainer.style.backgroundColor = origColor;
            }).StartingIn(500);
        }
    }

    public class SignalGraphView : GraphView
    {
        public SignalGraphView()
        {
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            this.AddManipulator(new ContentZoomer());

            // Performance: disable pixel cache regen above 30 elements
            zoomerMaxElementCountWithPixelCacheRegen = 30;

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();
        }

        public void ClearGraph()
        {
            DeleteElements(graphElements.ToList());
        }

        public Node CreateSignalNode(string signalName, Vector2 position)
        {
            var node = new Node { title = signalName, style = { width = 150 } };
            node.SetPosition(new Rect(position, Vector2.zero));
            node.mainContainer.style.backgroundColor = new StyleColor(new Color(0.2f, 0.4f, 0.2f, 0.8f));

            var outputPort = node.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            outputPort.portName = "▶";
            node.outputContainer.Add(outputPort);
            node.RefreshPorts();

            return node;
        }

        public Node CreateHandlerNode(string handlerName, Vector2 position)
        {
            var node = new Node { title = handlerName, style = { width = 150 } };
            node.SetPosition(new Rect(position, Vector2.zero));
            node.mainContainer.style.backgroundColor = new StyleColor(new Color(0.2f, 0.3f, 0.5f, 0.8f));

            var inputPort = node.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            inputPort.portName = "◀";
            node.inputContainer.Add(inputPort);
            node.RefreshPorts();

            return node;
        }

        public Edge ConnectNodes(Port outputPort, Port inputPort)
        {
            if (outputPort == null || inputPort == null) return null;
            var edge = new Edge { output = outputPort, input = inputPort };
            edge.input.Connect(edge);
            edge.output.Connect(edge);
            return edge;
        }
    }
}
